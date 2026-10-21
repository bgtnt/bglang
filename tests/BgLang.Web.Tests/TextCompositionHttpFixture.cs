using System.Security.Cryptography;
using System.Text.Json;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Web.Tests;

// Independent synthetic input; this is not a candidate-generated oracle or native package fixture.
internal static class TextCompositionHttpFixture
{
    internal static readonly JsonElement Root = Load();
    internal static readonly string[] TextFields = Root.GetProperty("expected").GetProperty("textFields")
        .EnumerateArray().Select(value => value.GetString()!).Concat(new[] { "context", "syntax", "validation", "wordConstructions" }).ToArray();

    private static JsonElement Load()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "text-composition-http-independent.v1.json"));
        Assert.AreEqual("83a4a06887e5a5668237e67ccf9e560824b792731f9bb06f078c91f7912b090e", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using JsonDocument parsed = JsonDocument.Parse(bytes);
        return parsed.RootElement.Clone();
    }

    internal static TextAnalysis Create(bool enabled)
    {
        TextReadingSnapshot[] rows = Root.GetProperty("rows").EnumerateArray()
            .Select(row => row.Deserialize<TextReadingSnapshot>(HttpContract.Json)).ToArray();
        WordAnalysis word = new("Светла", "светла", Array.AsReadOnly(rows.Take(32).Select(row => row.Raw).ToArray()), null)
        {
            GraphCandidates = [new("Светла", "светъл", "from", "to", [], ["edge"], ["rule"], ["application"], .85, ["proof"], "literal")
            {
                IsConditional = true, ConditionalRootProgramId = "program", ConditionalRootBaseFullTag = "tag",
                ConditionalRootSupportCount = 2, ConditionalSupportingLemmas = ["source-a", "source-b"],
                UnmetExactSelfReadingRequirements = [new("root", "tag", "edge")]
            }]
        };
        TextReadingInventory inventory = new([new("Светла", "светла", rows, null, null, 32)],
            [new(1, "Светла", 3, 6, 0), new(2, "Светла", 10, 6, 0)]);
        return new(Root.GetProperty("text").GetString()!,
            [new("😀", TokenKind.Symbol, 0, 2, null), new("Светла", TokenKind.Word, 3, 6, word),
             new("Светла", TokenKind.Word, 10, 6, word), new(".", TokenKind.Punctuation, 16, 1, null)])
        {
            Sentences = [new(0, 17)], Phonetics = new([], [], []),
            Diagnostics = [new("literal", 3, 6, "literal.rule")], Lexical = new(false, [], []),
            ReadingInventory = inventory,
            Metrics = Root.GetProperty("metrics").Deserialize<TextMetrics>(HttpContract.Json)!,
            Profile = Root.GetProperty("profile").Deserialize<DocumentReadingProfile>(HttpContract.Json)!,
            Spelling = new(2, 3, [new(1, "Светла", 3, 6, "светла", SpellingSearchDisposition.NotRequestedKnown, "exact-known", []),
                new(2, "Светла", 10, 6, "светла", SpellingSearchDisposition.NotRequestedKnown, "exact-known", [])]),
            ProperNames = new([new(1, 3, 6, ProperNameLane.Exact, ProperNameKinds.Person, true, [new(1, 35)], []),
                    new(2, 10, 6, ProperNameLane.Exact, ProperNameKinds.Person, true, [new(2, 35)], [])],
                [new(0, 2, 3, 13, ProperNameKinds.Person, true)], enabled ? [] : null, [], true)
        };
    }

    internal static ApiEnvelope Envelope(object data, string version = "2") => new(version,
        new("host", "api", "contracts", "dotnet"),
        new("package", "schema", new string('a', 64), "lexicon", "source", "graph", "graph-source"),
        new(null, 32, 32, false, "per-word-analysis"), new(false, true, null), data);

    internal static byte[] Encode(ApiEnvelope raw, string? requested)
    {
        string format = requested ?? (raw.ApiVersion == "2" ? SharedDonorResponse.AnalysisFormat : HttpContract.RawFormat);
        if (format == HttpContract.RawFormat) return JsonSerializer.SerializeToUtf8Bytes(raw, HttpContract.Json);
        var shared = new SharedDonorResponse(raw, format == SharedDonorResponse.AnalysisFormat);
        return JsonSerializer.SerializeToUtf8Bytes(shared.Envelope, shared.Json);
    }

    internal static JsonDocument Restore(JsonDocument encoded) => encoded.RootElement.TryGetProperty("donorSets", out _)
        ? SharedDonorResponseTests.Reconstruct(encoded) : JsonDocument.Parse(encoded.RootElement.GetRawText());

    internal static void AssertWholeText(JsonElement text, bool enabled)
    {
        CollectionAssert.AreEqual(TextFields, text.EnumerateObject().Select(property => property.Name).ToArray());
        JsonElement inventory = text.GetProperty("readingInventory"), set = inventory.GetProperty("sets")[0];
        JsonElement rows = set.GetProperty("canonicalReadings");
        Assert.AreEqual(36, rows.GetArrayLength());
        Assert.AreEqual(32, set.GetProperty("displayedReadingCount").GetInt32());
        Assert.IsTrue(set.GetProperty("displayTruncated").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, set.GetProperty("supportReadings").ValueKind);
        Assert.IsTrue(SharedDonorResponseTests.SameJson(rows[0], rows[1]), "Equal duplicates retain two array positions.");
        foreach (string field in new[] { "rawTupleFields", "featureFields" })
        {
            string property = field == "rawTupleFields" ? "raw" : "features";
            string[] expectedFields = Root.GetProperty("expected").GetProperty(field).EnumerateArray().Select(value => value.GetString()!).ToArray();
            if (field == "featureFields") expectedFields = [.. expectedFields, "case", "isShortForm", "participleKind"];
            CollectionAssert.AreEquivalent(expectedFields,
                rows[35].GetProperty(property).EnumerateObject().Select(value => value.Name).ToArray());
        }
        Assert.AreEqual("GivenName", rows[35].GetProperty("features").GetProperty("nameKind").GetString());
        JsonElement names = text.GetProperty("properNames");
        Assert.AreEqual(enabled, names.GetProperty("unknownContextEvaluated").GetBoolean());
        Assert.AreEqual(enabled ? JsonValueKind.Array : JsonValueKind.Null, names.GetProperty("unresolvedContextTokens").ValueKind);
        if (enabled) Assert.AreEqual(0, names.GetProperty("unresolvedContextTokens").GetArrayLength());
        JsonElement[] references = names.GetProperty("candidates").EnumerateArray().Select(candidate => candidate.GetProperty("exactSupports")[0]).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2 }, references.Select(reference => reference.GetProperty("tokenIndex").GetInt32()).ToArray());
        CollectionAssert.AreEqual(new[] { 35, 35 }, references.Select(reference => reference.GetProperty("completeReadingIndex").GetInt32()).ToArray());
        Assert.AreEqual(72, text.GetProperty("profile").GetProperty("fullReadingOccurrences").GetInt32());
    }
}
