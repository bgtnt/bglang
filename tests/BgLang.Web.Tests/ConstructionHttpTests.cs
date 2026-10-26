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
using static BgLang.Web.Tests.TextCompositionHttpFixture;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class ConstructionHttpTests
{
    private static readonly string[] SharedPhraseFamilies = ["typed-construction-candidate.past-indefinite.v1",
        "typed-construction-candidate.renarrative-past-aorist.v1"];

    [TestMethod]
    public void Construction_request_keeps_opaque_ID_and_rejects_inapplicable_fields()
    {
        WebInput opaque = new(" owner:exact ");
        ApiOperations.Validate("word-constructions", opaque); Assert.AreEqual(" owner:exact ", opaque.Text);
        foreach (string format in new[] { HttpContract.RawFormat, SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat })
            ApiOperations.Validate("word-constructions", opaque with { EvidenceFormat = format });
        foreach (string? text in new string?[] { null, "", " ", "\0", "owner\n", new string('я', 257) })
            Assert.Throws<ApiRequestException>(() => ApiOperations.Validate("word-constructions", new(text)));
        foreach (int limit in new[] { 1, 256 })
            Assert.AreEqual("limit-not-applicable", Assert.Throws<ApiRequestException>(() =>
                ApiOperations.Validate("word-constructions", opaque with { Limit = limit })).Code);
        foreach (int limit in new[] { 0, -1, 257 })
            Assert.AreEqual("invalid-limit", Assert.Throws<ApiRequestException>(() =>
                ApiOperations.Validate("word-constructions", opaque with { Limit = limit })).Code);
        Assert.AreEqual("query-not-applicable", Assert.Throws<ApiRequestException>(() =>
            ApiOperations.Validate("word-constructions", opaque with { Query = "" })).Code);
        Assert.AreEqual("option-not-applicable", Assert.Throws<ApiRequestException>(() =>
            ApiOperations.Validate("word-constructions", opaque with { Context = default(TextContextOptions) })).Code);
        foreach (bool names in new[] { false, true })
            Assert.AreEqual("option-not-applicable", Assert.Throws<ApiRequestException>(() =>
                ApiOperations.Validate("word-constructions", opaque with { IncludeUnknownContextProperNames = names })).Code);
        Assert.AreEqual("invalid-evidence-format", Assert.Throws<ApiRequestException>(() =>
            ApiOperations.Validate("word-constructions", opaque with { EvidenceFormat = "unknown" })).Code);
    }

    [TestMethod]
    public void Construction_switch_is_a_fifth_strict_boolean_with_all_defaults_disabled()
    {
        string[] fields = ["IncludeAdditionalRoleHeuristics", "IncludeSubordinateCommaDiagnostics",
            "IncludeFragmentDiagnostics", "IncludeSpacingDiagnostics", "IncludeWordConstructions"];
        var properties = typeof(TextContextOptions).GetProperties();
        CollectionAssert.AreEquivalent(fields.Append(nameof(TextContextOptions.Continuation)).ToArray(),
            properties.Select(property => property.Name).ToArray());
        Assert.IsTrue(properties.Where(property => property.Name != nameof(TextContextOptions.Continuation))
            .All(property => property.PropertyType == typeof(bool)));
        Assert.AreEqual(typeof(TextContinuationRequest), properties.Single(property =>
            property.Name == nameof(TextContextOptions.Continuation)).PropertyType);
        Assert.IsNull(default(TextContextOptions).Continuation);
        foreach (string context in new[] { "null", "{}", "{\"includeWordConstructions\":false}" })
        {
            WebInput input = JsonSerializer.Deserialize<WebInput>("{\"text\":\"знаме.\",\"context\":" + context + "}", HttpContract.Json)!;
            ApiOperations.Validate("analyze-text", input); Assert.AreEqual(default(TextContextOptions), input.Context ?? default);
        }
        WebInput enabled = JsonSerializer.Deserialize<WebInput>("{\"text\":\"знаме.\",\"context\":{\"includeWordConstructions\":true}}", HttpContract.Json)!;
        Assert.IsTrue(enabled.Context!.Value.IncludeWordConstructions);
        Assert.IsFalse(enabled.Context.Value.IncludeAdditionalRoleHeuristics);
        Assert.IsFalse(enabled.Context.Value.IncludeSubordinateCommaDiagnostics);
        Assert.IsFalse(enabled.Context.Value.IncludeFragmentDiagnostics); Assert.IsFalse(enabled.Context.Value.IncludeSpacingDiagnostics);
        foreach (string value in new[] { "null", "\"true\"", "0", "[]", "{}" })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WebInput>(
                "{\"text\":\"знаме.\",\"context\":{\"includeWordConstructions\":" + value + "}}", HttpContract.Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WebInput>("{\"text\":\"id\",\"entryId\":\"id\"}", HttpContract.Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WebInput>("{\"text\":1}", HttpContract.Json));
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Complete_constructions_use_existing_routes_all_formats_and_prequeue_validation(string version)
    {
        string manifest = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST") ?? "";
        string hash = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST_SHA256") ?? "";
        Assert.IsTrue(File.Exists(manifest), "The strictly identified complete annotated package is mandatory.");
        Assert.IsTrue(hash.Length == 64 && hash.All(Uri.IsHexDigit));
        var clock = new ManualClock(); BulgarianLanguageSession? session = null;
        var app = WebHostFactory.Create(["--urls", "http://127.0.0.1:0", "--BgLang:ManifestPath", manifest,
            "--BgLang:ManifestSha256", hash], () => session = BulgarianLanguageSession.Open(manifest), clock);
        try
        {
            await app.StartAsync(); Assert.IsNotNull(session!.Provenance.AnnotationsSource);
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
            SessionHost host = app.Services.GetRequiredService<SessionHost>();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task active = host.ExecuteResponseAsync("lookup", new("книга"), async (_, _) =>
            { entered.TrySetResult(); await release.Task; }, CancellationToken.None);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                foreach ((string operation, string body, string code) in new[]
                {
                    ("word-constructions", "{\"text\":\"id\",\"context\":{}}", "option-not-applicable"),
                    ("word-constructions", "{\"text\":\"id\",\"includeUnknownContextProperNames\":false}", "option-not-applicable"),
                    ("word-constructions", "{\"text\":\"id\",\"limit\":1}", "limit-not-applicable"),
                    ("word-constructions", "{\"text\":\"id\",\"query\":\"x\"}", "query-not-applicable"),
                    ("word-constructions", "{\"text\":\"id\",\"entryId\":\"x\"}", "invalid-json"),
                    ("word-constructions", "{\"text\":1}", "invalid-json"),
                    ("analyze-text", "{\"text\":\"знаме.\",\"context\":{\"includeWordConstructions\":null}}", "invalid-json")
                })
                {
                    clock.Now = clock.Now.AddMinutes(1);
                    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v" + version + "/" + operation)
                    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    using HttpResponseMessage rejected = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
                    using JsonDocument error = JsonDocument.Parse(await rejected.Content.ReadAsByteArrayAsync());
                    Assert.AreEqual(code, error.RootElement.GetProperty("code").GetString());
                    Assert.IsFalse(error.RootElement.TryGetProperty("data", out _));
                    Assert.IsFalse(active.IsCompleted, "Transport rejection must finish before the occupied language lane.");
                }
            }
            finally { release.TrySetResult(); await active; }
            LexicalEntryLookup lookup = session.LookupLexicalEntries("пиша", 256);
            Assert.IsTrue(lookup.MetadataAvailable); Assert.IsNotEmpty(lookup.Entries);
            WordConstructionResult[] ownerQueries = lookup.Entries.Select(entry => session.GetWordConstructionCandidates(entry.EntryId)).ToArray();
            WordConstructionResult query = ownerQueries.First(result => result.OwnForms?.Any(row => row.Raw.Form == "писал" && row.Raw.Lemma == "пиша") == true);
            LexicalEntry owner = query.Owner!;
            Assert.AreEqual(WordConstructionStatus.Ready, query.Status); Assert.IsNotNull(query.OwnForms);
            Assert.IsTrue(query.Candidates.All(candidate => candidate.EntryId == owner.EntryId && candidate.LexemeKey == owner.LexemeKey));
            AssertSharedPhrase(JsonSerializer.SerializeToElement(query, HttpContract.Json).GetProperty("candidates"));
            WordConstructionResult unknown = session.GetWordConstructionCandidates(" " + owner.EntryId + " ");
            Assert.AreEqual(WordConstructionStatus.OwnerNotFound, unknown.Status);
            const string source = "😀 писал съм.";
            TextAnalysis disabled = session.AnalyzeText(source);
            TextAnalysis enabled = session.AnalyzeText(source, false,
                new TextContextOptions { IncludeWordConstructions = true }, CancellationToken.None);
            Assert.IsNull(disabled.WordConstructions); Assert.IsNotNull(enabled.WordConstructions);
            foreach (string format in new[] { HttpContract.RawFormat, SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat })
            {
                await Compare("word-constructions", new(owner.EntryId, EvidenceFormat: format), query);
                await Compare("word-constructions", new(" " + owner.EntryId + " ", EvidenceFormat: format), unknown);
                await Compare("analyze-text", new(source, EvidenceFormat: format), disabled);
                await Compare("analyze-text", new(source, EvidenceFormat: format,
                    Context: new TextContextOptions { IncludeWordConstructions = true }), enabled);
            }
            async Task Compare(string operation, WebInput input, object expected)
            {
                clock.Now = clock.Now.AddMinutes(1);
                using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v" + version + "/" + operation, input, HttpContract.Json);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                using JsonDocument encoded = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
                using JsonDocument restored = Restore(encoded);
                JsonElement data = restored.RootElement.GetProperty("data");
                Assert.IsTrue(SharedDonorResponseTests.SameJson(JsonSerializer.SerializeToElement(expected, HttpContract.Json), data));
                Assert.AreEqual(version, encoded.RootElement.GetProperty("apiVersion").GetString());
                Assert.IsFalse(encoded.RootElement.GetProperty("coverage").GetProperty("hostTruncated").GetBoolean());
                Assert.AreNotEqual(JsonValueKind.True, encoded.RootElement.GetProperty("coverage").GetProperty("complete").ValueKind,
                    "Construction scope completeness must not promote global HTTP coverage.");
                if (operation == "word-constructions")
                {
                    Assert.AreEqual(JsonValueKind.Null, encoded.RootElement.GetProperty("limits").GetProperty("requested").ValueKind);
                    Assert.AreEqual(JsonValueKind.Null, encoded.RootElement.GetProperty("limits").GetProperty("effective").ValueKind);
                    Assert.AreEqual(JsonValueKind.Null, encoded.RootElement.GetProperty("limits").GetProperty("hostMaximum").ValueKind);
                    Assert.AreEqual("scalar-evidence", encoded.RootElement.GetProperty("limits").GetProperty("scope").GetString());
                    Assert.IsFalse(data.GetProperty("productiveReplayAvailable").GetBoolean());
                    Assert.AreEqual("OriginalGenerationSeedNotRetained", data.GetProperty("productiveReplayUnavailableReason").GetString());
                }
                else if (input.Context is { IncludeWordConstructions: true })
                {
                    JsonElement evidence = data.GetProperty("wordConstructions"), candidates = evidence.GetProperty("candidates");
                    JsonElement[] matches = evidence.GetProperty("matches").EnumerateArray()
                        .Where(match => match.GetProperty("start").GetInt32() == 3 && match.GetProperty("length").GetInt32() == 9).ToArray();
                    CollectionAssert.AreEquivalent(SharedPhraseFamilies, matches.Select(match => candidates[match.GetProperty("candidateIndex").GetInt32()]
                        .GetProperty("familyId").GetString()).Distinct().ToArray());
                    foreach (JsonElement match in matches)
                    {
                        Assert.AreEqual("писал съм", source.Substring(match.GetProperty("start").GetInt32(), match.GetProperty("length").GetInt32()));
                        JsonElement anchor = match.GetProperty("anchor"); Assert.AreEqual(1, anchor.GetProperty("tokenIndex").GetInt32());
                        JsonElement occurrence = data.GetProperty("readingInventory").GetProperty("occurrences").EnumerateArray()
                            .Single(value => value.GetProperty("tokenIndex").GetInt32() == anchor.GetProperty("tokenIndex").GetInt32());
                        JsonElement set = data.GetProperty("readingInventory").GetProperty("sets")[occurrence.GetProperty("readingSetIndex").GetInt32()];
                        JsonElement rows = set.GetProperty("supportReadings").ValueKind == JsonValueKind.Null ? set.GetProperty("canonicalReadings") : set.GetProperty("supportReadings");
                        JsonElement raw = rows[anchor.GetProperty("completeReadingIndex").GetInt32()].GetProperty("raw");
                        Assert.AreEqual("писал", raw.GetProperty("form").GetString()); Assert.AreEqual("пиша", raw.GetProperty("lemma").GetString());
                    }
                }
                else Assert.AreEqual(JsonValueKind.Null, data.GetProperty("wordConstructions").ValueKind);
            }
        }
        finally { try { await app.StopAsync(); } finally { await app.DisposeAsync(); } }
        Assert.Throws<ObjectDisposedException>(() => _ = session!.Provenance);
    }

    private static void AssertSharedPhrase(JsonElement candidates)
    {
        JsonElement[] shared = candidates.EnumerateArray().Where(candidate =>
            string.Join(" ", candidate.GetProperty("parts").EnumerateArray().Select(value => value.GetString())) == "писал съм").ToArray();
        CollectionAssert.AreEquivalent(SharedPhraseFamilies, shared.Select(candidate => candidate.GetProperty("familyId").GetString()).Distinct().ToArray());
        foreach (JsonElement candidate in shared)
        {
            Assert.AreEqual(0, candidate.GetProperty("basePartIndex").GetInt32());
            Assert.AreEqual("писал", candidate.GetProperty("baseForm").GetProperty("raw").GetProperty("form").GetString());
            string expectedTag = candidate.GetProperty("familyId").GetString() == SharedPhraseFamilies[0]
                ? "мин.неопр.вр., 1л., ед.ч., м.р." : "пр.накл., мин.св.вр., 1л., ед.ч., м.р.";
            Assert.AreEqual(expectedTag, candidate.GetProperty("targetConstructionTag").GetString());
            Assert.IsFalse(candidate.GetProperty("acceptedWordForm").GetBoolean()); Assert.IsFalse(candidate.GetProperty("grammarAuthority").GetBoolean());
        }
    }
}
