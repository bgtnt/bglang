using System.Text.Json;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

public sealed partial class PublicPackageTests
{
    private const string JoinedNegationCode = "joined_verb_negation_should_be_separate";
    private const string JoinedNegationRule = "spelling.joined_verb_negation.reviewed_forms.v1";
    private const string JoinedNegationBaselineId = "bglang-neutral:36b0b69685d6d3e9";
    private const string JoinedNegationBaselineSha = "451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7";

    // Literal orthography/UTF16 controls were frozen before new results; no runtime-derived correction gold.
    [TestMethod, TestCategory("FullPackage")]
    [DataRow("незнам.", 0, "незнам", "не знам", "не знам.")]
    [DataRow("неможе.", 0, "неможе", "не може", "не може.")]
    [DataRow("Незнам.", 0, "Незнам", "Не знам", "Не знам.")]
    [DataRow("НЕМОЖЕ.", 0, "НЕМОЖЕ", "НЕ МОЖЕ", "НЕ МОЖЕ.")]
    [DataRow("Аз незнам нищо.", 3, "незнам", "не знам", "Аз не знам нищо.")]
    [DataRow("Той неможе да дойде.", 4, "неможе", "не може", "Той не може да дойде.")]
    [DataRow("😀 „Незнам“.", 4, "Незнам", "Не знам", "😀 „Не знам“.")]
    [DataRow("„НЕМОЖЕ“!", 1, "НЕМОЖЕ", "НЕ МОЖЕ", "„НЕ МОЖЕ“!")]
    public void Joined_negation_public_literal_projection_preserves_original_span_and_raw_fuzzy_evidence(
        string text, int start, string original, string projected, string applied)
    {
        RequireJoinedNegationBaseline();
        TextAnalysis actual = session.AnalyzeText(text);
        TextDiagnostic diagnostic = actual.Diagnostics.Single(d => d.Code == JoinedNegationCode);
        Assert.AreEqual((start, original.Length, JoinedNegationRule, projected),
            (diagnostic.Start, diagnostic.Length, diagnostic.RuleId, diagnostic.ProjectedText));
        Assert.AreEqual(original, text.Substring(diagnostic.Start, diagnostic.Length));
        Assert.AreEqual(applied, text.Remove(diagnostic.Start, diagnostic.Length).Insert(diagnostic.Start, diagnostic.ProjectedText!));
        Assert.IsNull(diagnostic.Readings, "Suffix dictionary rows are not original unknown-token references.");
        Assert.IsNull(diagnostic.GraphReadings, "No graph path licenses this spelling relation.");
        Assert.IsFalse(actual.Diagnostics.Any(d => d.Start == start && d.Length == original.Length &&
            d.Code == "spelling_unknown_word_unique_distance_one_candidate"));
        AssertJoinedNegationSourceAndFuzzy(actual, text);
    }

    [TestMethod, TestCategory("FullPackage")]
    public void Joined_negation_public_repeated_occurrences_keep_case_utf16_and_immutable_snapshots()
    {
        RequireJoinedNegationBaseline();
        const string text = "😀 незнам. Незнам. НЕМОЖЕ.";
        TextAnalysis actual = session.AnalyzeText(text);
        TextDiagnostic[] diagnostics = actual.Diagnostics.Where(d => d.Code == JoinedNegationCode).ToArray();
        CollectionAssert.AreEqual(new[] { 3, 11, 19 }, diagnostics.Select(d => d.Start).ToArray());
        CollectionAssert.AreEqual(new[] { "не знам", "Не знам", "НЕ МОЖЕ" }, diagnostics.Select(d => d.ProjectedText).ToArray());
        Assert.IsTrue(diagnostics.All(d => d.Length == 6 && d.Readings is null && d.GraphReadings is null));
        Assert.AreEqual("😀 незнам. Не знам. НЕМОЖЕ.",
            text.Remove(diagnostics[1].Start, diagnostics[1].Length).Insert(diagnostics[1].Start, diagnostics[1].ProjectedText!));
        AssertJoinedNegationSourceAndFuzzy(actual, text);
        string captured = JsonSerializer.Serialize(actual);
        TextAnalysis again = session.AnalyzeText(text);
        Assert.AreEqual(captured, JsonSerializer.Serialize(again));
        Assert.AreEqual(captured, JsonSerializer.Serialize(actual), "Later queries must not mutate prior snapshots.");
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Аз не знам нищо.")]
    [DataRow("Той не може да дойде.")]
    [DataRow("Недей да викаш.")]
    [DataRow("Ненавиждам лъжите.")]
    [DataRow("Недовиждам.")]
    [DataRow("Нехая.")]
    [DataRow("Неможещ човек.")]
    [DataRow("Незнайко.")]
    [DataRow("Немога.")]
    [DataRow("Днес НЕЗНАМ.")]
    [DataRow("нeзнам.")]
    [DataRow("нЕзнам.")]
    [DataRow("незна̀м.")]
    [DataRow("42незнам.")]
    public void Joined_negation_public_leaves_lexical_competitors_and_unsupported_surfaces_untouched(string text)
    {
        RequireJoinedNegationBaseline();
        TextAnalysis actual = session.AnalyzeText(text);
        Assert.IsFalse(actual.Diagnostics.Any(d => d.Code == JoinedNegationCode));
        AssertJoinedNegationSourceAndFuzzy(actual, text);
    }

    private static void RequireJoinedNegationBaseline()
    {
        // Reuse the existing strict ClassInitialize: exact4 file set/hashes and immutable API session.
        Assert.AreEqual(JoinedNegationBaselineId, candidate.GetProperty("packageId").GetString());
        Assert.AreEqual(JoinedNegationBaselineId, session.Provenance.PackageId);
        Assert.AreEqual(JoinedNegationBaselineSha, session.Provenance.ManifestSha256.ToLowerInvariant());
        string? selected = Environment.GetEnvironmentVariable("BGLANG_PUBLIC_PACKAGE_MANIFEST");
        Assert.IsFalse(string.IsNullOrWhiteSpace(selected), "This package gate cannot skip or substitute another candidate.");
        Assert.AreEqual(Path.GetFullPath(selected!), manifest, true);
    }

    private static void AssertJoinedNegationSourceAndFuzzy(TextAnalysis actual, string text)
    {
        Assert.AreEqual(text, actual.Text, "Advice never edits the original text.");
        foreach (TokenAnalysis token in actual.Tokens)
            Assert.AreEqual(text.Substring(token.Start, token.Length), token.Text);
        Assert.IsNotNull(actual.Spelling);
        foreach (SpellingWordEvidence word in actual.Spelling.Words)
        {
            Assert.AreEqual(text.Substring(word.Start, word.Length), word.Text);
            if (word.Search != SpellingSearchDisposition.Completed) continue;
            IReadOnlyList<FuzzyMatch> raw = session.FindSimilar(word.NormalizedQuery,
                actual.Spelling.ConfiguredMaxDistance, actual.Spelling.ConfiguredLimit);
            Assert.AreEqual(JsonSerializer.Serialize(raw), JsonSerializer.Serialize(word.Alternatives),
                "Unmodified ordinary lookup is the raw-evidence invariant, not correction gold.");
            if (word.NormalizedQuery == "неможе")
                Assert.IsTrue(word.Alternatives.Any(a => a.Form == "неможещ"),
                    "Retain the previously recorded one-word competitor in raw alternatives.");
        }
    }
}
