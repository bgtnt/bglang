using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BgLang.Web.Tests.TextCompositionHttpFixture;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class InverseSpellingHttpTests
{
    private static readonly string?[] Formats = [HttpContract.RawFormat, null, SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat];

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Inverse_spelling_http_preserves_literal_modifier_refs_utf16_spans_and_raw_pool(string version)
    {
        await ContextCompositionHttpTests.WithPackage(async (unusedHost, session, client) =>
        {
            Assert.AreEqual("bglang-neutral:36b0b69685d6d3e9", session.Provenance.PackageId);
            Assert.AreEqual("451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7", session.Provenance.ManifestSha256.ToLowerInvariant());
            JsonElement rawPool = JsonSerializer.SerializeToElement(session.FindSimilar("кнгиа", 2, 10), HttpContract.Json);
            // Literal ordinary-OSA pool from the preserved Source7-before Baseline5 probe.
            CollectionAssert.AreEqual(new[] { "книга", "ангина", "кегла", "киа", "Кнежа", "книги", "книжа", "княза", "кога", "юнга" },
                rawPool.EnumerateArray().Select(a => a.GetProperty("form").GetString()).ToArray());
            CollectionAssert.AreEqual(new[] { 1, 2, 2, 2, 2, 2, 2, 2, 2, 2 },
                rawPool.EnumerateArray().Select(a => a.GetProperty("distance").GetInt32()).ToArray());
            foreach (var example in new[] { (Text: "Нова кнгиа.", Modifier: 0, Start: 0, Typo: 1, TypoStart: 5),
                (Text: "😀 Нова кнгиа.", Modifier: 1, Start: 3, Typo: 2, TypoStart: 8) })
            {
                session.AnalyzeText(example.Text);
                JsonElement? baseline = null;
                foreach (string? format in Formats)
                {
                    using JsonDocument encoded = await Post(client, version, example.Text, format);
                    using JsonDocument restored = Restore(encoded);
                    JsonElement data = restored.RootElement.GetProperty("data");
                    Assert.AreEqual(example.Text, data.GetProperty("text").GetString());
                    JsonElement spelling = data.GetProperty("spelling");
                    Assert.AreEqual(2, spelling.GetProperty("configuredMaxDistance").GetInt32());
                    Assert.AreEqual(10, spelling.GetProperty("configuredLimit").GetInt32());
                    JsonElement word = Word(data, "кнгиа");
                    Assert.AreEqual((example.Typo, example.TypoStart, 5, "кнгиа", "Completed"),
                        (word.GetProperty("tokenIndex").GetInt32(), word.GetProperty("start").GetInt32(), word.GetProperty("length").GetInt32(),
                         word.GetProperty("text").GetString(), word.GetProperty("search").GetString()));
                    Assert.AreEqual("кнгиа", example.Text.Substring(example.TypoStart, 5));
                    Assert.IsFalse(data.GetProperty("tokens")[example.Typo].GetProperty("analysis").GetProperty("isKnown").GetBoolean());
                    Assert.IsTrue(SharedDonorResponseTests.SameJson(rawPool, word.GetProperty("alternatives")),
                        "HTTP keeps every ordinary raw candidate reading and its original distance/order.");
                    JsonElement[] contextual = word.GetProperty("contextualCandidates").EnumerateArray().ToArray();
                    CollectionAssert.AreEquivalent(Enumerable.Range(0, 10).ToArray(), contextual.Select(c => c.GetProperty("alternativeIndex").GetInt32()).ToArray());
                    using JsonDocument expected = JsonDocument.Parse($$$"""
                        {"alternativeIndex":0,"assessment":"Supported","matches":[],"previousModifierMatches":[{"candidateReadingIndex":0,"previousModifierReading":{"tokenIndex":{{{example.Modifier}}},"completeReadingIndex":0}}]}
                        """);
                    Assert.IsTrue(SharedDonorResponseTests.SameJson(expected.RootElement,
                        contextual.Single(c => c.GetProperty("alternativeIndex").GetInt32() == 0)), "A preceding adjective has its own inverse reference; it is never FollowingNounReading.");
                    JsonElement modifier = data.GetProperty("tokens")[example.Modifier];
                    Assert.AreEqual(("Нова", example.Start, 4), (modifier.GetProperty("text").GetString(), modifier.GetProperty("start").GetInt32(), modifier.GetProperty("length").GetInt32()));
                    Assert.AreEqual("Нова", example.Text.Substring(example.Start, 4));
                    JsonElement rows = Complete(data, example.Modifier);
                    Assert.AreEqual(2, rows.GetArrayLength(), "The proper-name alternative remains in the complete original inventory.");
                    Assert.AreEqual("Прилагателно:76,Женски", rows[0].GetProperty("raw").GetProperty("partOfSpeech").GetString());
                    Assert.AreEqual("Място:195", rows[1].GetProperty("raw").GetProperty("partOfSpeech").GetString());
                    JsonElement features = rows[0].GetProperty("features");
                    Assert.AreEqual(("Adjective", "Singular", "Feminine", "Indefinite"),
                        (features.GetProperty("partOfSpeech").GetString(), features.GetProperty("number").GetString(), features.GetProperty("gender").GetString(), features.GetProperty("article").GetString()));
                    JsonElement noun = word.GetProperty("alternatives")[0].GetProperty("readings")[0];
                    Assert.AreEqual(("книга", "книга", "Съществително:41,Женски"),
                        (noun.GetProperty("form").GetString(), noun.GetProperty("lemma").GetString(), noun.GetProperty("partOfSpeech").GetString()));
                    if (baseline is null) baseline = restored.RootElement.Clone();
                    else Assert.IsTrue(SharedDonorResponseTests.SameJson(baseline.Value, restored.RootElement));
                }
            }
            Assert.IsTrue(SharedDonorResponseTests.SameJson(rawPool,
                JsonSerializer.SerializeToElement(session.FindSimilar("кнгиа", 2, 10), HttpContract.Json)));
        });
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Forward_spelling_http_keeps_following_noun_contract_and_omits_null_inverse_field(string version)
    {
        await ContextCompositionHttpTests.WithPackage(async (unusedHost, session, client) =>
        {
            JsonElement rawPool = JsonSerializer.SerializeToElement(session.FindSimilar("друфа", 2, 10), HttpContract.Json);
            session.AnalyzeText("Това е друфа дума");
            JsonElement? baseline = null;
            foreach (string? format in Formats)
            {
                using JsonDocument encoded = await Post(client, version, "Това е друфа дума", format);
                using JsonDocument restored = Restore(encoded);
                JsonElement data = restored.RootElement.GetProperty("data"), word = Word(data, "друфа");
                Assert.AreEqual((2, 7, 5), (word.GetProperty("tokenIndex").GetInt32(), word.GetProperty("start").GetInt32(), word.GetProperty("length").GetInt32()));
                Assert.IsTrue(SharedDonorResponseTests.SameJson(rawPool, word.GetProperty("alternatives")));
                Assert.AreEqual(("друга", 1), (rawPool[0].GetProperty("form").GetString(), rawPool[0].GetProperty("distance").GetInt32()));
                JsonElement[] contextual = word.GetProperty("contextualCandidates").EnumerateArray().ToArray();
                Assert.IsTrue(contextual.All(c => !c.TryGetProperty("previousModifierMatches", out _)),
                    "Forward-only JSON must omit the optional null property, preserving the existing shape.");
                using JsonDocument expected = JsonDocument.Parse("""
                    {"alternativeIndex":0,"assessment":"Supported","matches":[{"candidateReadingIndex":0,"followingNounReading":{"tokenIndex":3,"completeReadingIndex":0}}]}
                    """);
                Assert.IsTrue(SharedDonorResponseTests.SameJson(expected.RootElement,
                    contextual.Single(c => c.GetProperty("alternativeIndex").GetInt32() == 0)));
                Assert.AreEqual("Прилагателно:76,Женски", rawPool[0].GetProperty("readings")[0].GetProperty("partOfSpeech").GetString());
                Assert.AreEqual("Съществително:41,Женски", Complete(data, 3)[0].GetProperty("raw").GetProperty("partOfSpeech").GetString());
                if (baseline is null) baseline = restored.RootElement.Clone();
                else Assert.IsTrue(SharedDonorResponseTests.SameJson(baseline.Value, restored.RootElement));
            }
        });
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Default_null_empty_and_disabled_options_keep_inverse_http_evidence_identical(string version)
    {
        await ContextCompositionHttpTests.WithPackage(async (unusedHost, session, client) =>
        {
            session.AnalyzeText("Нова кнгиа.");
            JsonElement? baseline = null;
            foreach (string options in new[] { "", ",\"context\":null", ",\"context\":{}",
                """, "includeUnknownContextProperNames":false,"context":{"includeAdditionalRoleHeuristics":false,"includeSubordinateCommaDiagnostics":false,"includeFragmentDiagnostics":false,"includeSpacingDiagnostics":false,"includeWordConstructions":false,"includeNominalConstructions":false,"continuation":null}""" })
            foreach (string? format in Formats)
            {
                using JsonDocument encoded = await Post(client, version, "Нова кнгиа.", format, options);
                using JsonDocument restored = Restore(encoded);
                JsonElement data = restored.RootElement.GetProperty("data");
                Assert.IsFalse(data.TryGetProperty("continuation", out _));
                Assert.AreEqual(JsonValueKind.Null, data.GetProperty("wordConstructions").ValueKind);
                Assert.IsFalse(data.TryGetProperty("nominalConstructions", out _));
                if (baseline is null)
                {
                    baseline = restored.RootElement.Clone();
                    Assert.AreEqual(1, Word(data, "кнгиа").GetProperty("contextualCandidates")[0].GetProperty("previousModifierMatches").GetArrayLength());
                }
                else Assert.IsTrue(SharedDonorResponseTests.SameJson(baseline.Value, restored.RootElement),
                    "Inverse spelling evidence does not enable context/proper-name opt-ins or change default output for equivalent disabled options.");
            }
        });
    }

    private static JsonElement Word(JsonElement data, string query) => data.GetProperty("spelling").GetProperty("words").EnumerateArray()
        .Single(w => w.GetProperty("normalizedQuery").GetString() == query);

    private static JsonElement Complete(JsonElement data, int token)
    {
        JsonElement inventory = data.GetProperty("readingInventory");
        int set = inventory.GetProperty("occurrences").EnumerateArray().Single(o => o.GetProperty("tokenIndex").GetInt32() == token).GetProperty("readingSetIndex").GetInt32();
        JsonElement rows = inventory.GetProperty("sets")[set];
        return rows.GetProperty("supportReadings").ValueKind == JsonValueKind.Null ? rows.GetProperty("canonicalReadings") : rows.GetProperty("supportReadings");
    }

    private static void AssertWireRepresentation(JsonElement envelope, string version, string effective)
    {
        Assert.AreEqual(version, envelope.GetProperty("apiVersion").GetString());
        JsonElement[] tokens = envelope.GetProperty("data").GetProperty("tokens").EnumerateArray().ToArray();
        if (effective == HttpContract.RawFormat)
        {
            Assert.IsFalse(envelope.TryGetProperty("evidenceFormat", out _));
            Assert.IsFalse(envelope.TryGetProperty("donorSets", out _));
            Assert.IsFalse(envelope.TryGetProperty("wordAnalyses", out _));
            Assert.IsTrue(tokens.All(t => t.TryGetProperty("analysis", out _) && !t.TryGetProperty("analysisRef", out _)));
            return;
        }
        Assert.AreEqual(effective, envelope.GetProperty("evidenceFormat").GetString());
        Assert.AreEqual(JsonValueKind.Array, envelope.GetProperty("donorSets").ValueKind);
        if (effective == SharedDonorResponse.Format)
        {
            Assert.IsFalse(envelope.TryGetProperty("wordAnalyses", out _));
            Assert.IsTrue(tokens.All(t => t.TryGetProperty("analysis", out _) && !t.TryGetProperty("analysisRef", out _)));
            return;
        }
        JsonElement analyses = envelope.GetProperty("wordAnalyses");
        Assert.AreEqual(JsonValueKind.Array, analyses.ValueKind);
        Assert.IsTrue(tokens.All(t => !t.TryGetProperty("analysis", out _) && t.TryGetProperty("analysisRef", out _)));
        foreach (JsonElement token in tokens)
        {
            JsonElement reference = token.GetProperty("analysisRef");
            if (token.GetProperty("kind").GetString() == "Word")
                Assert.IsTrue(reference.GetInt32() >= 0 && reference.GetInt32() < analyses.GetArrayLength());
            else Assert.AreEqual(JsonValueKind.Null, reference.ValueKind);
        }
    }

    private static async Task<JsonDocument> Post(HttpClient client, string version, string text, string? format, string options = "")
    {
        string json = "{\"text\":" + JsonSerializer.Serialize(text, HttpContract.Json)
            + (format is null ? "" : ",\"evidenceFormat\":" + JsonSerializer.Serialize(format)) + options + "}";
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("/api/v" + version + "/analyze-text", body);
        string actual = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, actual);
        JsonDocument encoded = JsonDocument.Parse(actual);
        try
        {
            string effective = format ?? (version == "2" ? SharedDonorResponse.AnalysisFormat : HttpContract.RawFormat);
            AssertWireRepresentation(encoded.RootElement, version, effective);
            return encoded;
        }
        catch
        {
            encoded.Dispose();
            throw;
        }
    }
}
