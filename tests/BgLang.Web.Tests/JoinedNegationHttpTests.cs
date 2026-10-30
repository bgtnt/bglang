using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BgLang.Api;
using BgLang.Contracts;
using BgLang.Web;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BgLang.Web.Tests.TextCompositionHttpFixture;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class JoinedNegationHttpTests
{
    private const string Code = "joined_verb_negation_should_be_separate";
    private const string Rule = "spelling.joined_verb_negation.reviewed_forms.v1";
    private const string BaselineId = "bglang-neutral:36b0b69685d6d3e9";
    private const string BaselineSha = "451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7";

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")]
    [DataRow("2")]
    public async Task Joined_negation_http_preserves_literal_source_refs_and_raw_evidence_in_all_formats(string version)
    {
        RequireBaseline();
        await ContextCompositionHttpTests.WithPackage(async (_, session, client) =>
        {
            Assert.AreEqual(BaselineId, session.Provenance.PackageId);
            Assert.AreEqual(BaselineSha, session.Provenance.ManifestSha256.ToLowerInvariant());
            var cases = new[]
            {
                (Text: "Той неможе да дойде.", Starts: new[] { 4 }, Projected: new[] { "не може" }, Applied: "Той не може да дойде."),
                (Text: "Аз незнам нищо.", Starts: new[] { 3 }, Projected: new[] { "не знам" }, Applied: "Аз не знам нищо."),
                (Text: "😀 незнам. Незнам. НЕМОЖЕ.", Starts: new[] { 3, 11, 19 },
                    Projected: new[] { "не знам", "Не знам", "НЕ МОЖЕ" }, Applied: "😀 не знам. Не знам. НЕ МОЖЕ."),
                (Text: "Недей. Ненавиждам. Недовиждам. Нехая. Неможещ човек. Незнайко.",
                    Starts: Array.Empty<int>(), Projected: Array.Empty<string>(),
                    Applied: "Недей. Ненавиждам. Недовиждам. Нехая. Неможещ човек. Незнайко."),
                (Text: "Аз не знам нищо. Той не може да дойде.", Starts: Array.Empty<int>(), Projected: Array.Empty<string>(),
                    Applied: "Аз не знам нищо. Той не може да дойде."),
                (Text: "Днес НЕЗНАМ. нЕзнам. нeзнам. незна̀м. 42незнам.",
                    Starts: Array.Empty<int>(), Projected: Array.Empty<string>(),
                    Applied: "Днес НЕЗНАМ. нЕзнам. нeзнам. незна̀м. 42незнам.")
            };
            foreach (var example in cases)
            {
                TextAnalysis api = session.AnalyzeText(example.Text);
                using JsonDocument raw = await Post(example.Text, HttpContract.RawFormat);
                AssertJoinedData(raw.RootElement.GetProperty("data"), api, example.Text, example.Starts, example.Projected, example.Applied, session);
                JsonElement expectedApi = JsonSerializer.SerializeToElement(api, HttpContract.Json);
                Assert.IsTrue(SharedDonorResponseTests.SameJson(expectedApi, raw.RootElement.GetProperty("data")),
                    "Actual HTTP raw data must preserve the public API result, not a transport-specific correction.");
                foreach (string format in new[] { SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat })
                {
                    using JsonDocument encoded = await Post(example.Text, format);
                    using JsonDocument restored = Restore(encoded);
                    Assert.IsTrue(SharedDonorResponseTests.SameJson(raw.RootElement, restored.RootElement),
                        "All representations retain source spans, null suffix references, raw candidates and original text.");
                    AssertJoinedData(restored.RootElement.GetProperty("data"), api, example.Text,
                        example.Starts, example.Projected, example.Applied, session);
                }
            }

            async Task<JsonDocument> Post(string text, string format)
            {
                using var body = new StringContent(JsonSerializer.Serialize(new { text, evidenceFormat = format }),
                    Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await client.PostAsync("/api/v" + version + "/analyze-text", body);
                string json = await response.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, json);
                return JsonDocument.Parse(json);
            }
        });
    }

    private static void RequireBaseline()
    {
        string manifest = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST") ?? "";
        Assert.IsTrue(Path.IsPathFullyQualified(manifest) && File.Exists(manifest),
            "The strict complete Baseline selector is mandatory; this gate cannot skip or substitute Current.");
        Assert.AreEqual(BaselineSha, Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST_SHA256")?.ToLowerInvariant());
        Assert.AreEqual("manifest.json", Path.GetFileName(manifest));
        string directory = Path.GetDirectoryName(manifest)!;
        CollectionAssert.AreEquivalent(new[] { "manifest.json", "lexicon.v3.jsonl", "word-formation.v0.json", "lexeme-annotations.v1.json" },
            Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).ToArray());
        Assert.IsTrue(Directory.GetFileSystemEntries(directory).All(path => !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)));
        using var stream = File.OpenRead(manifest);
        Assert.AreEqual(BaselineSha, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    private static void AssertJoinedData(JsonElement data, TextAnalysis api, string text, int[] starts,
        string[] projected, string applied, BulgarianLanguageSession session)
    {
        Assert.AreEqual(text, data.GetProperty("text").GetString(), "Advice must not edit the request.");
        JsonElement[] allDiagnostics = data.GetProperty("diagnostics").EnumerateArray().ToArray();
        JsonElement[] diagnostics = allDiagnostics.Where(d => d.GetProperty("code").GetString() == Code).ToArray();
        CollectionAssert.AreEqual(starts, diagnostics.Select(d => d.GetProperty("start").GetInt32()).ToArray());
        CollectionAssert.AreEqual(projected, diagnostics.Select(d => d.GetProperty("projectedText").GetString()).ToArray());
        string manuallyApplied = text;
        foreach (JsonElement diagnostic in diagnostics.Reverse())
        {
            int start = diagnostic.GetProperty("start").GetInt32(), length = diagnostic.GetProperty("length").GetInt32();
            Assert.AreEqual(6, length); Assert.AreEqual(Rule, diagnostic.GetProperty("ruleId").GetString());
            Assert.IsTrue(text.Substring(start, length).ToLowerInvariant() is "незнам" or "неможе");
            AssertNullOrAbsent(diagnostic, "readings"); AssertNullOrAbsent(diagnostic, "graphReadings");
            Assert.IsFalse(allDiagnostics.Any(d => d.GetProperty("start").GetInt32() == start &&
                d.GetProperty("length").GetInt32() == length &&
                d.GetProperty("code").GetString() == "spelling_unknown_word_unique_distance_one_candidate"));
            manuallyApplied = manuallyApplied.Remove(start, length).Insert(start, diagnostic.GetProperty("projectedText").GetString()!);
        }
        Assert.AreEqual(applied, manuallyApplied, "Only explicit captured ranges may be manually replaced.");
        foreach (JsonElement token in data.GetProperty("tokens").EnumerateArray())
            Assert.AreEqual(text.Substring(token.GetProperty("start").GetInt32(), token.GetProperty("length").GetInt32()),
                token.GetProperty("text").GetString());
        Assert.IsNotNull(api.Spelling);
        foreach (JsonElement word in data.GetProperty("spelling").GetProperty("words").EnumerateArray())
        {
            int start = word.GetProperty("start").GetInt32(), length = word.GetProperty("length").GetInt32();
            Assert.AreEqual(text.Substring(start, length), word.GetProperty("text").GetString());
            SpellingWordEvidence original = api.Spelling.Words.Single(w => w.Start == start && w.Length == length);
            if (original.Search != SpellingSearchDisposition.Completed) continue;
            IReadOnlyList<FuzzyMatch> raw = session.FindSimilar(original.NormalizedQuery,
                api.Spelling.ConfiguredMaxDistance, api.Spelling.ConfiguredLimit);
            Assert.IsTrue(SharedDonorResponseTests.SameJson(JsonSerializer.SerializeToElement(raw, HttpContract.Json), word.GetProperty("alternatives")));
            if (original.NormalizedQuery == "неможе")
                Assert.IsTrue(word.GetProperty("alternatives").EnumerateArray().Any(a => a.GetProperty("form").GetString() == "неможещ"));
        }
    }

    private static void AssertNullOrAbsent(JsonElement value, string property)
    {
        if (value.TryGetProperty(property, out JsonElement support))
            Assert.AreEqual(JsonValueKind.Null, support.ValueKind, "Suffix indices must never masquerade as original references.");
    }
}
