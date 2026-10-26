using System.Net;
using System.Text;
using System.Text.Json;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BgLang.Web.Tests.TextCompositionHttpFixture;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class AgreementHttpTests
{
    private const string Rule = "closed_exact_agreement.v1";
    private const string FiniteCode = "productive_morphology_agreement_mismatch";
    private sealed record Example(string Text, string Code, int Target, int Start, int Length,
        string References, string ContextStatus, int SelectedRoles, string SyntaxRoute);

    // Independent profile4/premeasurement literals; no current result generates a gold count/span/ref.
    // One existing full-package fixture host, sequential28POSTs, below default30 peer budget.
    [TestMethod, TestCategory("FullPackage")]
    public async Task Exact_agreement_warnings_and_controls_keep_original_wire_ranges_complete_refs_and_old_selection()
    {
        await ContextCompositionHttpTests.WithPackage(async (unusedHost, session, client) =>
        {
            Assert.AreEqual("bglang-neutral:36b0b69685d6d3e9", session.Provenance.PackageId);
            Assert.AreEqual("451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7", session.Provenance.ManifestSha256.ToLowerInvariant());
            Example[] examples =
            [
                new("Аз чете.", "productive_morphology_agreement_mismatch", 1, 3, 4, "0:0|1:0|1:1|1:2", "", 0, "SourceScan"),
                new("Те спи.", "productive_morphology_agreement_mismatch", 1, 3, 3, "0:0|1:1", "", 0, "SourceScan"),
                new("Децата играе.", "productive_morphology_agreement_mismatch", 1, 7, 5, "0:0|1:0", "", 0, "SourceScan"),
                new("Малкият дете плаче.", "productive_attribute_agreement_mismatch", 0, 0, 7, "0:0|1:0", "", 0, "SourceScan"),
                new("Високите дървета шуми.", "productive_morphology_agreement_mismatch", 2, 17, 4, "1:1|2:2", "Ambiguous", 0, "Abstained"),
                new("Аз работи.", "productive_morphology_agreement_mismatch", 1, 3, 6, "0:0|1:2|1:3|1:4", "Ambiguous", 0, "Abstained"),
                new("Ти чете.", "", -1, -1, 0, "", "Ambiguous", 0, "Abstained"),
                new("Ти работи.", "", -1, -1, 0, "", "Ambiguous", 0, "Abstained"),
                new("Той работи.", "", -1, -1, 0, "", "Selected", 2, "Selected"),
                new("Новите деца играят.", "", -1, -1, 0, "", "", 0, "SourceScan"),
            ];
            int posted = 0;
            foreach (string version in new[] { "1", "2" })
            {
                foreach (Example example in examples)
                {
                    TextAnalysis direct = session.AnalyzeText(example.Text, false,
                        new() { IncludeAdditionalRoleHeuristics = true }, default);
                    using JsonDocument raw = await Post(client, version, example.Text, HttpContract.RawFormat); posted++;
                    AssertEnvelope(raw.RootElement, version, example.Text);
                    JsonElement data = raw.RootElement.GetProperty("data");
                    Assert.IsTrue(SharedDonorResponseTests.SameJson(JsonSerializer.SerializeToElement(direct, HttpContract.Json), data),
                        "The entire wire text/complete inventory/context/syntax/diagnostic data must equal the direct public result.");
                    AssertCase(data, example);
                    if (example.Text is "Малкият дете плаче." or "Аз работи.")
                    {
                        using JsonDocument encoded = await Post(client, version, example.Text, SharedDonorResponse.AnalysisFormat); posted++;
                        using JsonDocument restored = Restore(encoded);
                        Assert.IsTrue(SharedDonorResponseTests.SameJson(raw.RootElement, restored.RootElement),
                            "Shared-analysis reconstruction must preserve the entire raw envelope and exact support indices.");
                        AssertCase(restored.RootElement.GetProperty("data"), example);
                    }
                }
                const string unicode = "😀. Аз чете.\r\nАз чете.";
                using (JsonDocument raw = await Post(client, version, unicode, HttpContract.RawFormat))
                {
                    posted++; AssertEnvelope(raw.RootElement, version, unicode);
                    JsonElement data = raw.RootElement.GetProperty("data");
                    JsonElement[] diagnostics = Exact(data);
                    Assert.HasCount(2, diagnostics);
                    AssertDiagnostic(data, diagnostics[0], new(unicode, FiniteCode, 3, 7, 4, "2:0|3:0|3:1|3:2", "", 0, ""));
                    AssertDiagnostic(data, diagnostics[1], new(unicode, FiniteCode, 6, 17, 4, "5:0|6:0|6:1|6:2", "", 0, ""));
                    JsonElement[] occurrences = data.GetProperty("readingInventory").GetProperty("occurrences").EnumerateArray().ToArray();
                    Assert.AreEqual(occurrences.Single(o => o.GetProperty("tokenIndex").GetInt32() == 2).GetProperty("readingSetIndex").GetInt32(),
                        occurrences.Single(o => o.GetProperty("tokenIndex").GetInt32() == 5).GetProperty("readingSetIndex").GetInt32());
                    Assert.AreEqual(occurrences.Single(o => o.GetProperty("tokenIndex").GetInt32() == 3).GetProperty("readingSetIndex").GetInt32(),
                        occurrences.Single(o => o.GetProperty("tokenIndex").GetInt32() == 6).GetProperty("readingSetIndex").GetInt32());
                }
                using (JsonDocument raw = await Post(client, version, "Аз го четем.", HttpContract.RawFormat))
                {
                    posted++; JsonElement data = raw.RootElement.GetProperty("data"); Assert.IsEmpty(Exact(data));
                    JsonElement old = data.GetProperty("diagnostics").EnumerateArray().Single(d =>
                        d.GetProperty("code").GetString() == "pronoun_verb_agreement_mismatch");
                    Assert.AreEqual((6, 5, "pronoun_verb_agreement"),
                        (old.GetProperty("start").GetInt32(), old.GetProperty("length").GetInt32(), old.GetProperty("ruleId").GetString()));
                    Assert.IsFalse(old.TryGetProperty("readings", out _)); Assert.IsFalse(old.TryGetProperty("graphReadings", out _));
                    Assert.AreEqual(JsonValueKind.Null, old.GetProperty("projectedText").ValueKind);
                }
            }
            Assert.AreEqual(28, posted, "Batch stays within the existing fixture peer budget; no new host/rate framework.");
        });
    }

    private static void AssertEnvelope(JsonElement envelope, string version, string text)
    {
        Assert.AreEqual(version, envelope.GetProperty("apiVersion").GetString());
        Assert.AreEqual("451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7",
            envelope.GetProperty("package").GetProperty("manifestSha256").GetString()!.ToLowerInvariant());
        Assert.AreEqual(text, envelope.GetProperty("data").GetProperty("text").GetString());
    }
    private static JsonElement[] Exact(JsonElement data) => data.GetProperty("diagnostics").EnumerateArray()
        .Where(d => d.GetProperty("ruleId").ValueKind == JsonValueKind.String && d.GetProperty("ruleId").GetString() == Rule).ToArray();
    private static void AssertCase(JsonElement data, Example example)
    {
        Assert.AreEqual(example.Text, data.GetProperty("text").GetString());
        JsonElement[] warnings = Exact(data); Assert.HasCount(example.Code.Length == 0 ? 0 : 1, warnings, example.Text);
        if (warnings.Length != 0) AssertDiagnostic(data, warnings.Single(), example);
        JsonElement context = data.GetProperty("context").GetProperty("sentences")[0];
        Assert.AreEqual(example.ContextStatus, context.GetProperty("status").ValueKind == JsonValueKind.Null ? "" : context.GetProperty("status").GetString());
        Assert.AreEqual(example.SelectedRoles, context.GetProperty("selectedRoles").GetArrayLength());
        foreach (JsonElement candidate in context.GetProperty("candidates").EnumerateArray().Where(c =>
            c.GetProperty("authorityLane").ValueKind == JsonValueKind.String && c.GetProperty("authorityLane").GetString() == Rule))
            Assert.IsFalse(candidate.GetProperty("selectable").GetBoolean());
        Assert.AreEqual(example.SyntaxRoute, data.GetProperty("syntax").GetProperty("sentences")[0].GetProperty("route").GetString());
        if (example.Text is "Ти работи." or "Той работи." or "Аз работи.")
        {
            JsonElement rows = Complete(data, 1); Assert.AreEqual(5, rows.GetArrayLength());
            Assert.AreEqual("Noun", rows[0].GetProperty("features").GetProperty("partOfSpeech").GetString());
            Assert.AreEqual("Imperative", rows[1].GetProperty("features").GetProperty("verbForm").GetString());
            Assert.AreEqual("Second", rows[2].GetProperty("features").GetProperty("person").GetString());
            Assert.AreEqual("Aorist", rows[2].GetProperty("features").GetProperty("tense").GetString());
            Assert.AreEqual("Third", rows[3].GetProperty("features").GetProperty("person").GetString());
            Assert.AreEqual("Aorist", rows[3].GetProperty("features").GetProperty("tense").GetString());
            Assert.AreEqual("Present", rows[4].GetProperty("features").GetProperty("tense").GetString());
        }
        if (example.Text == "Те спи.")
        {
            JsonElement subject = Complete(data, 0); Assert.AreEqual(3, subject.GetArrayLength());
            Assert.AreEqual("Third", subject[0].GetProperty("features").GetProperty("person").GetString());
            Assert.AreEqual("Plural", subject[0].GetProperty("features").GetProperty("number").GetString());
            Assert.AreEqual("Particle", subject[1].GetProperty("features").GetProperty("partOfSpeech").GetString());
            Assert.AreEqual("Accusative", subject[2].GetProperty("features").GetProperty("case").GetString());
            Assert.IsTrue(subject[2].GetProperty("features").GetProperty("isShortForm").GetBoolean());
            Assert.IsTrue(subject[2].GetProperty("features").GetProperty("hasUnrecognizedFeatures").GetBoolean());
            JsonElement predicate = Complete(data, 1); Assert.AreEqual(2, predicate.GetArrayLength());
            Assert.AreEqual("Imperative", predicate[0].GetProperty("features").GetProperty("verbForm").GetString());
            Assert.AreEqual("Finite", predicate[1].GetProperty("features").GetProperty("verbForm").GetString());
        }
        if (example.Text == "Високите дървета шуми.")
        {
            JsonElement head = Complete(data, 1); Assert.AreEqual(2, head.GetArrayLength());
            Assert.AreEqual("unknown:500", head[0].GetProperty("raw").GetProperty("partOfSpeech").GetString());
            Assert.IsTrue(head[0].GetProperty("features").GetProperty("hasUnrecognizedFeatures").GetBoolean());
            Assert.AreEqual("Noun", head[1].GetProperty("features").GetProperty("partOfSpeech").GetString());
            Assert.AreEqual("Plural", head[1].GetProperty("features").GetProperty("number").GetString());
            JsonElement predicate = Complete(data, 2); Assert.AreEqual(3, predicate.GetArrayLength());
            Assert.AreEqual("Noun", predicate[0].GetProperty("features").GetProperty("partOfSpeech").GetString());
            Assert.AreEqual("Imperative", predicate[1].GetProperty("features").GetProperty("verbForm").GetString());
            Assert.AreEqual("Finite", predicate[2].GetProperty("features").GetProperty("verbForm").GetString());
        }
        if (example.Text is "Ти чете." or "Аз чете.")
        {
            JsonElement rows = Complete(data, 1); Assert.AreEqual(3, rows.GetArrayLength());
            Assert.AreEqual("Second", rows[0].GetProperty("features").GetProperty("person").GetString());
            Assert.AreEqual("Aorist", rows[0].GetProperty("features").GetProperty("tense").GetString());
        }
    }
    private static void AssertDiagnostic(JsonElement data, JsonElement warning, Example example)
    {
        Assert.AreEqual(Rule, warning.GetProperty("ruleId").GetString());
        Assert.AreEqual(example.Code, warning.GetProperty("code").GetString());
        Assert.AreEqual((example.Start, example.Length), (warning.GetProperty("start").GetInt32(), warning.GetProperty("length").GetInt32()));
        JsonElement target = data.GetProperty("tokens")[example.Target];
        Assert.AreEqual((example.Start, example.Length), (target.GetProperty("start").GetInt32(), target.GetProperty("length").GetInt32()));
        Assert.AreEqual(target.GetProperty("text").GetString(), example.Text.Substring(example.Start, example.Length));
        Assert.AreEqual(JsonValueKind.Null, warning.GetProperty("projectedText").ValueKind);
        Assert.IsFalse(warning.TryGetProperty("graphReadings", out JsonElement graph) && graph.ValueKind != JsonValueKind.Null,
            "Exact diagnostics allow omitted/null graph support, never an empty or fabricated graph array.");
        JsonElement[] references = warning.GetProperty("readings").EnumerateArray().ToArray();
        Assert.AreEqual(example.References, string.Join('|', references.Select(r => r.GetProperty("tokenIndex").GetInt32() + ":" + r.GetProperty("completeReadingIndex").GetInt32())));
        foreach (JsonElement reference in references)
        {
            CollectionAssert.AreEquivalent(new[] { "tokenIndex", "completeReadingIndex" }, reference.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.IsFalse(reference.TryGetProperty("readingIndex", out _));
            int token = reference.GetProperty("tokenIndex").GetInt32(), index = reference.GetProperty("completeReadingIndex").GetInt32();
            JsonElement rows = Complete(data, token); Assert.IsTrue(index >= 0 && index < rows.GetArrayLength());
            if (example.Code == FiniteCode && token == example.Target)
                Assert.AreEqual("Finite", rows[index].GetProperty("features").GetProperty("verbForm").GetString());
        }
    }
    private static JsonElement Complete(JsonElement data, int token)
    {
        JsonElement inventory = data.GetProperty("readingInventory");
        JsonElement occurrence = inventory.GetProperty("occurrences").EnumerateArray().Single(o => o.GetProperty("tokenIndex").GetInt32() == token);
        JsonElement set = inventory.GetProperty("sets")[occurrence.GetProperty("readingSetIndex").GetInt32()];
        return set.GetProperty("supportReadings").ValueKind == JsonValueKind.Array ? set.GetProperty("supportReadings") : set.GetProperty("canonicalReadings");
    }
    private static async Task<JsonDocument> Post(HttpClient client, string version, string text, string format)
    {
        var body = new Dictionary<string, object?> { ["text"] = text, ["evidenceFormat"] = format,
            ["context"] = new { includeAdditionalRoleHeuristics = true } };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v" + version + "/analyze-text")
        { Content = new StringContent(JsonSerializer.Serialize(body, HttpContract.Json), Encoding.UTF8, "application/json") };
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, Encoding.UTF8.GetString(bytes));
        return JsonDocument.Parse(bytes);
    }

    // A separate full-package host keeps the existing agreement method's 28-request budget unchanged.
    [TestMethod, TestCategory("FullPackage")]
    public async Task Spelling_context_preserves_raw_fuzzy_wire_and_original_complete_noun_references()
    {
        await ContextCompositionHttpTests.WithPackage(async (unusedHost, session, client) =>
        {
            Assert.AreEqual("bglang-neutral:36b0b69685d6d3e9", session.Provenance.PackageId);
            Assert.AreEqual("451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7", session.Provenance.ManifestSha256.ToLowerInvariant());
            IReadOnlyList<FuzzyMatch> rawFind = session.FindSimilar("друфа", 2, 10);
            Assert.HasCount(10, rawFind); Assert.AreEqual(5, rawFind.Count(a => a.Distance == 1));
            Assert.AreEqual(("друга", 1), (rawFind[0].Form, rawFind[0].Distance));
            Assert.HasCount(1, rawFind[0].Readings);
            MorphologicalFeatures adjective = rawFind[0].Readings[0].Features;
            Assert.AreEqual(MorphologicalPartOfSpeech.Adjective, adjective.PartOfSpeech);
            Assert.AreEqual(MorphologicalNumber.Singular, adjective.Number); Assert.AreEqual(MorphologicalGender.Feminine, adjective.Gender);
            JsonElement rawFindJson = JsonSerializer.SerializeToElement(rawFind, HttpContract.Json);
            JsonElement nounBefore = JsonSerializer.SerializeToElement(session.Lookup("дума", LookupMode.Exact, 256)
                .Select(r => new TextReadingSnapshot(r, r.Features)).ToArray(), HttpContract.Json);
            string[] texts = [ "Това е друфа дума", "Това е друга дума", "Това е дру", "Друфа", "Това е друфа дом",
                "😀 Това е друфа дума", "Това е друфа дума. Друфа" ];
            int posted = 0;
            foreach (string version in new[] { "1", "2" })
            {
                foreach (string text in texts)
                {
                    TextAnalysis direct = session.AnalyzeText(text, true, new() { IncludeAdditionalRoleHeuristics = true }, default);
                    using JsonDocument raw = await PostSpelling(version, text, HttpContract.RawFormat); posted++;
                    AssertEnvelope(raw.RootElement, version, text);
                    JsonElement data = raw.RootElement.GetProperty("data");
                    Assert.IsTrue(SharedDonorResponseTests.SameJson(JsonSerializer.SerializeToElement(direct, HttpContract.Json), data));
                    Assert.IsFalse(data.GetProperty("diagnostics").EnumerateArray().Any(d =>
                        d.GetProperty("code").GetString() == "spelling_unknown_word_unique_distance_one_candidate"));
                    JsonElement[] words = data.GetProperty("spelling").GetProperty("words").EnumerateArray().ToArray();
                    foreach (JsonElement word in words)
                    {
                        JsonElement search = word.GetProperty("search"); Assert.AreEqual(JsonValueKind.String, search.ValueKind);
                        if (search.GetString() != "Completed") continue;
                        string query = word.GetProperty("normalizedQuery").GetString()!;
                        JsonElement expectedRaw = query == "друфа" ? rawFindJson : JsonSerializer.SerializeToElement(session.FindSimilar(query, 2, 10), HttpContract.Json);
                        Assert.IsTrue(SharedDonorResponseTests.SameJson(expectedRaw, word.GetProperty("alternatives")),
                            "The wire keeps the ordinary FindSimilar order and every raw candidate reading field.");
                    }
                    if (text is "Това е друфа дума" or "😀 Това е друфа дума" or "Това е друфа дума. Друфа")
                    {
                        JsonElement word = words.Single(w => w.GetProperty("text").GetString() == "друфа");
                        int token = word.GetProperty("tokenIndex").GetInt32();
                        AssertSupport(data, word, text.StartsWith("😀", StringComparison.Ordinal) ? 10 : 7, token + 1);
                        if (text == "Това е друфа дума")
                        {
                            Assert.AreEqual(2, token);
                            using JsonDocument encoded = await PostSpelling(version, text, SharedDonorResponse.AnalysisFormat); posted++;
                            using JsonDocument restored = Restore(encoded);
                            Assert.IsTrue(SharedDonorResponseTests.SameJson(raw.RootElement, restored.RootElement),
                                "Shared encoding preserves contextual assessment strings and original candidate/noun indices.");
                            JsonElement restoredData = restored.RootElement.GetProperty("data");
                            JsonElement restoredWord = restoredData.GetProperty("spelling").GetProperty("words")[2];
                            AssertSupport(restoredData, restoredWord, 7, 3);
                        }
                        if (text == "Това е друфа дума. Друфа")
                        {
                            JsonElement[] occurrences = words.Where(w => w.GetProperty("normalizedQuery").GetString() == "друфа").ToArray();
                            Assert.HasCount(2, occurrences);
                            Assert.IsTrue(SharedDonorResponseTests.SameJson(occurrences[0].GetProperty("alternatives"), occurrences[1].GetProperty("alternatives")));
                            Assert.IsFalse(occurrences[1].TryGetProperty("contextualCandidates", out _),
                                "A shared normalized fuzzy cache cannot copy context to a different original occurrence.");
                        }
                    }
                    else if (text == "Това е друга дума")
                    {
                        Assert.AreEqual("NotRequestedKnown", words[2].GetProperty("search").GetString());
                        Assert.AreEqual(0, words[2].GetProperty("alternatives").GetArrayLength());
                        Assert.IsFalse(words[2].TryGetProperty("contextualCandidates", out _)); AssertNoun(data, 3);
                        Assert.IsTrue(data.GetProperty("tokens")[2].GetProperty("analysis").GetProperty("isKnown").GetBoolean());
                    }
                    else if (text == "Това е дру")
                    {
                        Assert.AreEqual("Completed", words[2].GetProperty("search").GetString());
                        Assert.IsFalse(words[2].TryGetProperty("contextualCandidates", out _));
                        Assert.IsFalse(data.GetProperty("tokens")[2].GetProperty("analysis").GetProperty("isKnown").GetBoolean());
                    }
                    else if (text == "Друфа")
                    {
                        Assert.AreEqual("Completed", words[0].GetProperty("search").GetString());
                        Assert.IsFalse(words[0].TryGetProperty("contextualCandidates", out _));
                        Assert.IsFalse(data.GetProperty("tokens")[0].GetProperty("analysis").GetProperty("isKnown").GetBoolean());
                        Assert.IsTrue(data.GetProperty("properNames").GetProperty("unresolvedContextTokens").EnumerateArray().Any(t =>
                            t.GetProperty("tokenIndex").GetInt32() == 0 && t.GetProperty("reason").GetString() == "sentence-initial-context-insufficient"));
                    }
                    else
                    {
                        Assert.AreEqual("Това е друфа дом", text);
                        // Prospective negative; no invented package-specific дом count, tag or reading index.
                        if (words[2].TryGetProperty("contextualCandidates", out JsonElement contextual))
                        {
                            JsonElement candidate = contextual.EnumerateArray().Single(c => c.GetProperty("alternativeIndex").GetInt32() == 0);
                            Assert.AreEqual(JsonValueKind.String, candidate.GetProperty("assessment").ValueKind);
                            Assert.AreNotEqual("Supported", candidate.GetProperty("assessment").GetString());
                            Assert.AreEqual(0, candidate.GetProperty("matches").GetArrayLength());
                        }
                        JsonElement domRows = JsonSerializer.SerializeToElement(session.Lookup("дом", LookupMode.Exact, 256)
                            .Select(r => new TextReadingSnapshot(r, r.Features)).ToArray(), HttpContract.Json);
                        Assert.IsTrue(SharedDonorResponseTests.SameJson(domRows, Complete(data, 3)));
                    }
                }
            }
            Assert.AreEqual(16, posted, "Separate spelling host stays below the existing 30-request peer budget.");
            Assert.IsTrue(SharedDonorResponseTests.SameJson(rawFindJson, JsonSerializer.SerializeToElement(session.FindSimilar("друфа", 2, 10), HttpContract.Json)));

            async Task<JsonDocument> PostSpelling(string version, string text, string format)
            {
                var body = new Dictionary<string, object?> { ["text"] = text, ["evidenceFormat"] = format,
                    ["includeUnknownContextProperNames"] = true, ["context"] = new { includeAdditionalRoleHeuristics = true } };
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v" + version + "/analyze-text")
                { Content = new StringContent(JsonSerializer.Serialize(body, HttpContract.Json), Encoding.UTF8, "application/json") };
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, Encoding.UTF8.GetString(bytes)); return JsonDocument.Parse(bytes);
            }
            void AssertNoun(JsonElement data, int nounToken)
            {
                JsonElement rows = Complete(data, nounToken); Assert.IsTrue(SharedDonorResponseTests.SameJson(nounBefore, rows));
                Assert.AreEqual(4, rows.GetArrayLength());
                CollectionAssert.AreEqual(new[] { "Съществително:41,Женски", "Глагол:186ti,2л.,Минало_свършено",
                    "Глагол:186ti,3л.,Минало_свършено", "Глагол:186ti,3л.,Сегашно" },
                    rows.EnumerateArray().Select(r => r.GetProperty("raw").GetProperty("partOfSpeech").GetString()).ToArray());
                JsonElement noun = rows[0].GetProperty("features"); Assert.AreEqual("Noun", noun.GetProperty("partOfSpeech").GetString());
                Assert.AreEqual("Singular", noun.GetProperty("number").GetString()); Assert.AreEqual("Feminine", noun.GetProperty("gender").GetString());
                Assert.IsTrue(rows.EnumerateArray().Skip(1).All(r => r.GetProperty("features").GetProperty("partOfSpeech").GetString() == "Verb"));
            }
            void AssertSupport(JsonElement data, JsonElement word, int expectedStart, int nounToken)
            {
                Assert.AreEqual("Completed", word.GetProperty("search").GetString());
                Assert.AreEqual(("друфа", expectedStart, 5), (word.GetProperty("text").GetString(), word.GetProperty("start").GetInt32(), word.GetProperty("length").GetInt32()));
                JsonElement token = data.GetProperty("tokens")[word.GetProperty("tokenIndex").GetInt32()];
                Assert.AreEqual((expectedStart, 5), (token.GetProperty("start").GetInt32(), token.GetProperty("length").GetInt32()));
                Assert.AreEqual("друфа", data.GetProperty("text").GetString()!.Substring(expectedStart, 5));
                JsonElement alternatives = word.GetProperty("alternatives"); Assert.AreEqual(10, alternatives.GetArrayLength());
                Assert.AreEqual(5, alternatives.EnumerateArray().Count(a => a.GetProperty("distance").GetInt32() == 1));
                Assert.IsTrue(SharedDonorResponseTests.SameJson(rawFindJson, alternatives));
                Assert.AreEqual("друга", alternatives[0].GetProperty("form").GetString());
                Assert.AreEqual("Прилагателно:76,Женски", alternatives[0].GetProperty("readings")[0].GetProperty("partOfSpeech").GetString());
                JsonElement[] contextual = word.GetProperty("contextualCandidates").EnumerateArray().ToArray(); Assert.HasCount(10, contextual);
                CollectionAssert.AreEqual(Enumerable.Range(0, 10).ToArray(), contextual.Select(c => c.GetProperty("alternativeIndex").GetInt32()).ToArray());
                JsonElement candidate = contextual.Single(c => c.GetProperty("alternativeIndex").GetInt32() == 0);
                Assert.AreEqual(JsonValueKind.String, candidate.GetProperty("assessment").ValueKind);
                Assert.AreEqual("Supported", candidate.GetProperty("assessment").GetString());
                JsonElement[] matches = candidate.GetProperty("matches").EnumerateArray().ToArray(); Assert.HasCount(1, matches);
                CollectionAssert.AreEquivalent(new[] { "candidateReadingIndex", "followingNounReading" }, matches[0].EnumerateObject().Select(p => p.Name).ToArray());
                Assert.AreEqual(0, matches[0].GetProperty("candidateReadingIndex").GetInt32());
                JsonElement reference = matches[0].GetProperty("followingNounReading");
                CollectionAssert.AreEquivalent(new[] { "tokenIndex", "completeReadingIndex" }, reference.EnumerateObject().Select(p => p.Name).ToArray());
                Assert.AreEqual((nounToken, 0), (reference.GetProperty("tokenIndex").GetInt32(), reference.GetProperty("completeReadingIndex").GetInt32()));
                AssertNoun(data, nounToken);
            }
        });
    }
}
