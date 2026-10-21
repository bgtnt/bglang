using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BgLang.Api;
using BgLang.Contracts;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class FullPackageHttpTests
{
    [TestMethod, TestCategory("FullPackage")]
    public async Task Complete_package_routes_demo_caps_provenance_and_peer_limits_match_direct_public_calls()
    {
        string manifest = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST") ?? "";
        string hash = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST_SHA256") ?? "";
        Assert.IsTrue(File.Exists(manifest), "A strictly identified complete release package is required; no package-free acceptance.");
        Assert.IsTrue(hash.Length == 64 && hash.All(Uri.IsHexDigit), "Set the independently selected manifest SHA-256.");
        BulgarianLanguageSession? direct = null;
        int opens = 0;
        var app = WebHostFactory.Create(
            ["--urls", "http://127.0.0.1:0", "--BgLang:ManifestPath", manifest, "--BgLang:ManifestSha256", hash], () =>
            {
                opens++;
                return direct = BulgarianLanguageSession.Open(manifest);
            }, new ManualClock());
        try
        {
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
            using JsonDocument metadata = JsonDocument.Parse(await client.GetStringAsync("/api/v1/"));
            Assert.AreEqual(hash.ToLowerInvariant(), metadata.RootElement.GetProperty("package").GetProperty("manifestSha256").GetString());
            Assert.AreEqual(JsonSerializer.Serialize(direct!.Provenance, HttpContract.Json),
                metadata.RootElement.GetProperty("package").GetRawText());
            Assert.AreEqual("1", metadata.RootElement.GetProperty("apiVersion").GetString());
            using (JsonDocument metadataV2 = JsonDocument.Parse(await client.GetStringAsync("/api/v2/")))
            {
                Assert.AreEqual("2", metadataV2.RootElement.GetProperty("apiVersion").GetString());
                Assert.AreEqual(hash.ToLowerInvariant(), metadataV2.RootElement.GetProperty("package").GetProperty("manifestSha256").GetString());
                Assert.AreEqual(metadata.RootElement.GetProperty("package").GetRawText(),
                    metadataV2.RootElement.GetProperty("package").GetRawText());
                Assert.AreEqual(metadata.RootElement.GetProperty("versions").GetRawText(),
                    metadataV2.RootElement.GetProperty("versions").GetRawText());
            }
            Assert.IsFalse(metadata.RootElement.GetProperty("forwardedHeadersTrusted").GetBoolean());
            Assert.AreEqual(1, metadata.RootElement.GetProperty("limits").GetProperty("concurrency").GetInt32());
            JsonElement hostLimits = metadata.RootElement.GetProperty("limits");
            Assert.AreEqual(256, hostLimits.GetProperty("resultLimit").GetInt32());
            Assert.AreEqual(32, hostLimits.GetProperty("defaultResultLimit").GetInt32());
            Assert.AreEqual(128, hostLimits.GetProperty("generateFormsDefaultLimit").GetInt32());
            Assert.AreEqual(32, hostLimits.GetProperty("textWordAnalysisLimit").GetInt32());
            Assert.AreEqual(32, hostLimits.GetProperty("queryAnalysisLimit").GetInt32());
            Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            foreach (string path in new[] { "/demo", "/docs", "/demo.js", "/site.css" })
                Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode, path);

            int posted = 0;
            static void AssertAnalysisCoverage(JsonElement coverage, IEnumerable<WordAnalysis> analyses, int limit,
                LexicalTextEvidence? lexical = null)
            {
                WordAnalysis[] words = analyses.ToArray();
                bool lexicalTruncated = lexical?.Tokens.Any(token => token.Owners.Truncated) ?? false;
                bool reached = lexicalTruncated || words.Any(word => new[] { word.Readings.Count, word.GraphCandidates.Count,
                    word.Candidates.Count, word.KnownBaseCandidates.Count, word.BaseTypeCandidates.Count }
                    .Any(count => count >= limit));
                bool incomplete = lexical?.PackageHasUnresolvedRelations == true || lexicalTruncated || words.Any(word => word.GraphSearchIncomplete || word.GraphCandidatesTruncated ||
                    word.CandidateSearchLimited || word.CandidateEvidenceLimited || word.KnownBaseSearchLimited ||
                    word.BaseTypeSearchLimited || word.BaseTypeEvidenceLimited);
                Assert.AreEqual(reached, coverage.GetProperty("possibleTruncation").GetBoolean());
                Assert.AreEqual(incomplete ? JsonValueKind.False : JsonValueKind.Null,
                    coverage.GetProperty("complete").ValueKind);
            }

            async Task<JsonDocument> Compare(string operation, WebInput input, object expected)
            {
                posted++;
                using HttpResponseMessage http = await client.PostAsJsonAsync("/api/v1/" + operation, input, HttpContract.Json);
                Assert.AreEqual(HttpStatusCode.OK, http.StatusCode, await http.Content.ReadAsStringAsync());
                JsonDocument json = JsonDocument.Parse(await http.Content.ReadAsStringAsync());
                Assert.AreEqual(JsonSerializer.Serialize(expected, HttpContract.Json), json.RootElement.GetProperty("data").GetRawText(), operation);
                Assert.AreEqual(JsonSerializer.Serialize(direct.Provenance, HttpContract.Json), json.RootElement.GetProperty("package").GetRawText());
                JsonElement coverage = json.RootElement.GetProperty("coverage");
                Assert.IsFalse(coverage.GetProperty("hostTruncated").GetBoolean());
                if (operation is "lookup" or "analyze-word" or "generate-forms" or "derive-words")
                {
                    int expectedLimit = input.Limit ?? (operation == "generate-forms" ? 128 : 32);
                    JsonElement limits = json.RootElement.GetProperty("limits");
                    if (input.Limit is { } requested) Assert.AreEqual(requested, limits.GetProperty("requested").GetInt32());
                    else Assert.AreEqual(JsonValueKind.Null, limits.GetProperty("requested").ValueKind);
                    Assert.AreEqual(expectedLimit, limits.GetProperty("effective").GetInt32());
                    Assert.AreEqual(256, limits.GetProperty("hostMaximum").GetInt32());
                    Assert.IsFalse(limits.GetProperty("hostLimitApplied").GetBoolean());
                    Assert.AreEqual("per-result-collection", limits.GetProperty("scope").GetString());
                    if (expected is WordAnalysis word) AssertAnalysisCoverage(coverage, [word], expectedLimit);
                    else
                    {
                        Assert.AreEqual(json.RootElement.GetProperty("data").GetArrayLength() >= expectedLimit,
                            coverage.GetProperty("possibleTruncation").GetBoolean());
                        Assert.AreEqual(JsonValueKind.Null, coverage.GetProperty("complete").ValueKind,
                            "A returned list, including one below its limit, does not prove exhaustive language coverage.");
                    }
                }
                else if (expected is TextAnalysis text)
                {
                    JsonElement limits = json.RootElement.GetProperty("limits");
                    Assert.AreEqual(JsonValueKind.Null, limits.GetProperty("requested").ValueKind);
                    Assert.AreEqual(32, limits.GetProperty("effective").GetInt32());
                    Assert.AreEqual(32, limits.GetProperty("hostMaximum").GetInt32());
                    Assert.IsFalse(limits.GetProperty("hostLimitApplied").GetBoolean());
                    Assert.AreEqual("per-word-analysis", limits.GetProperty("scope").GetString());
                    AssertAnalysisCoverage(coverage, text.Tokens.Where(token => token.Analysis is not null)
                        .Select(token => token.Analysis!), 32, text.Lexical);
                }
                else if (expected is WordDecomposition)
                {
                    JsonElement limits = json.RootElement.GetProperty("limits");
                    foreach (string field in new[] { "requested", "effective", "hostMaximum" })
                        Assert.AreEqual(JsonValueKind.Null, limits.GetProperty(field).ValueKind);
                    Assert.IsFalse(limits.GetProperty("hostLimitApplied").GetBoolean());
                    Assert.AreEqual("scalar-evidence", limits.GetProperty("scope").GetString());
                    Assert.IsFalse(coverage.GetProperty("possibleTruncation").GetBoolean());
                    Assert.AreEqual(JsonValueKind.Null, coverage.GetProperty("complete").ValueKind);
                }
                return json;
            }
            using (await Compare("lookup", new("книга"), direct.Lookup("книга", LookupMode.Exact, 32))) { }
            using (await Compare("lookup", new("книга", 1), direct.Lookup("книга", LookupMode.Exact, 1))) { }
            using (await Compare("lookup", new("книга", 256), direct.Lookup("книга", LookupMode.Exact, 256))) { }
            using (await Compare("analyze-word", new("книга"), direct.AnalyzeWord("книга", 32))) { }
            using (await Compare("generate-forms", new("книга", 32), direct.GenerateForms("книга", 32))) { }
            using (await Compare("derive-words", new("трактор"), direct.DeriveWords("трактор", 32))) { }
            using (await Compare("decompose-word", new("супермравка"), direct.DecomposeWord("супермравка"))) { }
            using (await Compare("analyze-text", new("Книгата е тук.\nСупермравка."), direct.AnalyzeText("Книгата е тук.\nСупермравка."))) { }
            using (await Compare("analyze-word", new("щщщщщщщщщщщ", 32), direct.AnalyzeWord("щщщщщщщщщщщ", 32))) { }

            WordAnalysis ambiguous = direct.AnalyzeWord("пила", 32);
            Assert.IsTrue(ambiguous.Readings.Select(reading => (reading.Lemma, reading.PartOfSpeech)).Distinct().Count() > 1,
                "The ambiguous demo input must have multiple real package readings.");
            using (await Compare("analyze-word", new("пила", 32), ambiguous)) { }
            WordAnalysis novel = direct.AnalyzeWord("супермравка", 32);
            Assert.IsFalse(novel.IsKnown, "The graph demo must use an unstored derived surface.");
            Assert.IsTrue(novel.GraphCandidates.Any(path => path.Base == "мравка" && !path.IsConditional),
                "The selected package must actually license the demonstration path.");
            using (await Compare("analyze-word", new("супермравка", 32), novel)) { }
            foreach (int requested in new[] { 100, 256 })
                using (await Compare("analyze-word", new("супермравка", requested), direct.AnalyzeWord("супермравка", requested))) { }

            IReadOnlyList<WordForm> widestVerbForms = direct.GenerateForms("пиша", 256);
            Assert.IsTrue(widestVerbForms.Count > 32, "The useful-result witness must contain more than 32 real forms, not synthetic rows.");
            foreach (int requested in new[] { 100, 256 })
            {
                using JsonDocument expanded = await Compare("generate-forms", new("пиша", requested),
                    requested == 256 ? widestVerbForms : direct.GenerateForms("пиша", requested));
                Assert.IsTrue(expanded.RootElement.GetProperty("data").GetArrayLength() > 32,
                    "The host must retain useful forms above the former 32-row ceiling.");
            }
            using (await Compare("generate-forms", new("пиша"), direct.GenerateForms("пиша", 128))) { }
            using (JsonDocument belowLimit = await Compare("generate-forms", new("книга", 256), direct.GenerateForms("книга", 256)))
            {
                int count = belowLimit.RootElement.GetProperty("data").GetArrayLength();
                Assert.IsTrue(count > 0 && count < 256, "A real finite paradigm below the requested limit is required.");
                Assert.AreEqual(JsonValueKind.Null, belowLimit.RootElement.GetProperty("coverage").GetProperty("complete").ValueKind);
            }
            using (await Compare("derive-words", new("трактор", 256), direct.DeriveWords("трактор", 256))) { }

            // Main demo flow: original text plus an independent query, preserving both public analyses.
            const string comparisonText = "📚 Книга, книга, книгата, книгите. Пила.";
            WordAnalysis comparisonQuery = direct.AnalyzeWord("книга", 32);
            TextAnalysis comparisonAnalysis = direct.AnalyzeText(comparisonText);
            posted++;
            using (HttpResponseMessage comparisonHttp = await client.PostAsJsonAsync("/api/v1/find-forms-in-text",
                       new WebInput(comparisonText, Query: "книга"), HttpContract.Json))
            {
                Assert.AreEqual(HttpStatusCode.OK, comparisonHttp.StatusCode, await comparisonHttp.Content.ReadAsStringAsync());
                using JsonDocument comparison = JsonDocument.Parse(await comparisonHttp.Content.ReadAsStringAsync());
                JsonElement data = comparison.RootElement.GetProperty("data");
                Assert.AreEqual(JsonSerializer.Serialize(comparisonQuery, HttpContract.Json), data.GetProperty("queryAnalysis").GetRawText());
                Assert.AreEqual(JsonSerializer.Serialize(comparisonAnalysis, HttpContract.Json), data.GetProperty("textAnalysis").GetRawText());
                JsonElement[] matches = data.GetProperty("matches").EnumerateArray().ToArray();
                CollectionAssert.AreEqual(new[] { "Книга", "книга", "книгата", "книгите" },
                    matches.Select(match => match.GetProperty("text").GetString()!).ToArray());
                Assert.AreEqual(1, matches.Count(match => match.GetProperty("exactMatch").GetBoolean()));
                Assert.AreEqual(3, matches[0].GetProperty("start").GetInt32(), "Emoji occupies two UTF-16 units, followed by one space.");
                foreach (JsonElement match in matches)
                {
                    int start = match.GetProperty("start").GetInt32(), length = match.GetProperty("length").GetInt32();
                    Assert.AreEqual(comparisonText.Substring(start, length), match.GetProperty("text").GetString());
                    Assert.IsTrue(match.GetProperty("storedLemmaMatches").EnumerateArray()
                        .Any(lemma => lemma.GetProperty("lemma").GetString() == "книга"));
                    Assert.IsFalse(match.GetProperty("unknown").GetBoolean());
                }
                Assert.IsFalse(comparison.RootElement.GetProperty("coverage").GetProperty("hostTruncated").GetBoolean());
                JsonElement limits = comparison.RootElement.GetProperty("limits");
                Assert.AreEqual(JsonValueKind.Null, limits.GetProperty("requested").ValueKind);
                Assert.AreEqual(32, limits.GetProperty("effective").GetInt32());
                Assert.AreEqual(32, limits.GetProperty("hostMaximum").GetInt32());
                Assert.IsFalse(limits.GetProperty("hostLimitApplied").GetBoolean());
                Assert.AreEqual("per-query-and-word-analysis; all-text-matches-retained",
                    comparison.RootElement.GetProperty("limits").GetProperty("scope").GetString());
                AssertAnalysisCoverage(comparison.RootElement.GetProperty("coverage"), comparisonAnalysis.Tokens
                    .Where(token => token.Analysis is not null).Select(token => token.Analysis!).Prepend(comparisonQuery), 32, comparisonAnalysis.Lexical);
            }

            async Task Invalid(string operation, string body, HttpStatusCode expected, string contentType = "application/json", bool chunked = false)
            {
                posted++;
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/" + operation)
                { Content = new StringContent(body, Encoding.UTF8, contentType) };
                if (chunked) request.Headers.TransferEncodingChunked = true;
                using HttpResponseMessage response = await client.SendAsync(request);
                byte[] responseBody = await response.Content.ReadAsByteArrayAsync();
                Assert.AreEqual(expected, response.StatusCode, Encoding.UTF8.GetString(responseBody));
                if (expected == HttpStatusCode.RequestEntityTooLarge)
                {
                    Assert.AreEqual((long)responseBody.Length, response.Content.Headers.ContentLength);
                    Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
                    Assert.AreEqual(true, response.Headers.ConnectionClose);
                    using JsonDocument error = JsonDocument.Parse(responseBody);
                    Assert.AreEqual(chunked ? "invalid-http-request" : "body-too-large", error.RootElement.GetProperty("code").GetString());
                }
            }
            await Invalid("analyze-word", "{", HttpStatusCode.BadRequest);
            await Invalid("analyze-word", "{\"text\":\"книга\",\"unknown\":true}", HttpStatusCode.BadRequest);
            await Invalid("analyze-word", "{\"text\":\"книга\",\"limit\":0}", HttpStatusCode.BadRequest);
            await Invalid("analyze-word", "{\"text\":\"книга\",\"limit\":257}", HttpStatusCode.BadRequest);
            await Invalid("decompose-word", "{\"text\":\"книга\",\"limit\":32}", HttpStatusCode.BadRequest);
            await Invalid("find-forms-in-text", "{\"text\":\"книга\"}", HttpStatusCode.BadRequest);
            await Invalid("analyze-word", JsonSerializer.Serialize(new { text = new string('а', 257) }), HttpStatusCode.BadRequest);
            await Invalid("analyze-text", JsonSerializer.Serialize(new { text = new string('а', 4097) }), HttpStatusCode.BadRequest);
            await Invalid("analyze-word", "книга", HttpStatusCode.UnsupportedMediaType, "text/plain");
            string excessiveBody = new string(' ', HttpContract.BodyBytes) + "{\"text\":\"книга\"}";
            await Invalid("analyze-word", excessiveBody, HttpStatusCode.RequestEntityTooLarge);
            await Invalid("analyze-word", excessiveBody, HttpStatusCode.RequestEntityTooLarge, chunked: true);

            // Invalid POSTs count too. Forged forwarded headers cannot create a fresh rate partition.
            Assert.IsTrue(posted <= HttpContract.RequestsPerMinute, "All functional assertions must fit before the peer-rate boundary.");
            while (posted < HttpContract.RequestsPerMinute)
            {
                posted++;
                using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/lookup", new WebInput("книга", 1));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }
            using var forged = new HttpRequestMessage(HttpMethod.Post, "/api/v1/lookup")
            { Content = JsonContent.Create(new WebInput("книга", 1)) };
            forged.Headers.Add("X-Forwarded-For", "203.0.113.123");
            using HttpResponseMessage rejected = await client.SendAsync(forged);
            Assert.AreEqual(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.IsNotNull(rejected.Headers.RetryAfter);
            Assert.AreEqual(1, opens, "One process-wide session, not one Open per request.");
        }
        finally
        {
            try { await app.StopAsync(); }
            finally { await app.DisposeAsync(); }
        }
        Assert.Throws<ObjectDisposedException>(() => _ = direct!.Provenance);
    }
}
