using System.Security.Cryptography;
using System.Text.Json;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

public sealed partial class PublicPackageTests
{
    // Frozen before measurement: contract76bd133b, independent oracle4bd38dd8.
    // Candidate records are literals. Context ordinals are preserved against the
    // separate complete original inventory; no unverified package ordinal is invented.
    private const string NfSource = "neutral-source-v3:7e43ca45a13e2b3ef2948d32b7576834e5225e724cb14f26e1474a54db4410c7";
    private sealed record NfToken(TokenKind Kind, string Text, int Start, int Length);
    private sealed record NfCase(string Text, int Caret, int Start, int Length, string Prefix,
        int Head, int[] Modifiers, int[] GraphTokens, MorphologicalNumber Number, NfToken[] Tokens);

    [TestMethod]
    [DataRow("P01-existing-exact-whitespace", false)]
    [DataRow("P01-existing-exact-whitespace", true)]
    [DataRow("P02-exact-modifier-comma", false)]
    [DataRow("P02-exact-modifier-comma", true)]
    [DataRow("P03-exact-comma-and", false)]
    [DataRow("P03-exact-comma-and", true)]
    [DataRow("P04-all-licensed-graph", false)]
    [DataRow("P04-all-licensed-graph", true)]
    [DataRow("P05-mixed-exact-modifiers-graph-head", false)]
    [DataRow("P05-mixed-exact-modifiers-graph-head", true)]
    [DataRow("P06-mixed-graph-modifiers-exact-head", false)]
    [DataRow("P06-mixed-graph-modifiers-exact-head", true)]
    [DataRow("P07-plural-exact-modifiers-graph-head", false)]
    [DataRow("P07-plural-exact-modifiers-graph-head", true)]
    [DataRow("P08-previous-emoji-sentence-midword", false)]
    [DataRow("P08-previous-emoji-sentence-midword", true)]
    public void Nominal_finite_successor_matches_frozen_package_controls(string id, bool automatic)
    {
        NfRequireBaseline(); NfCase fixture = NfPositive(id);
        var target = automatic ? TextContinuationTarget.Auto : TextContinuationTarget.Finite;
        TextAnalysis before = session.AnalyzeText(fixture.Text);
        TextAnalysis actual = session.AnalyzeText(fixture.Text, false, new()
            { Continuation = new(fixture.Caret, 256) { Target = target } }, default);
        TextContinuationEvidence result = actual.Continuation!;
        Assert.IsTrue(result.Available, result.UnavailableReason); Assert.IsNull(result.UnavailableReason);
        Assert.AreEqual(TextContinuationTarget.Finite, result.Target); Assert.IsNull(result.NominalExpectations);
        Assert.AreEqual("finite_continuation.v1", result.Version); Assert.AreEqual("exact_package_finite_vocabulary", result.CandidateSource);
        Assert.AreEqual("dictionary_score", result.Ranking);
        Assert.AreEqual(fixture.Caret, result.Caret); Assert.AreEqual(fixture.Start, result.Start);
        Assert.AreEqual(fixture.Length, result.Length); Assert.AreEqual(fixture.Prefix, result.Prefix);
        Assert.HasCount(fixture.Tokens.Length, actual.Tokens);
        for (int i = 0; i < fixture.Tokens.Length; i++)
        {
            NfToken expected = fixture.Tokens[i]; TokenAnalysis token = actual.Tokens[i];
            Assert.AreEqual((expected.Kind, expected.Text, expected.Start, expected.Length), (token.Kind, token.Text, token.Start, token.Length));
        }
        TextContinuationExpectation expectation = result.Expectations.Single();
        Assert.AreEqual(MorphologicalPerson.Third, expectation.Person); Assert.AreEqual(fixture.Number, expectation.Number);
        int[] supportOrder = [fixture.Head, .. fixture.Modifiers];
        CollectionAssert.AreEqual(supportOrder, expectation.Supports.Select(s => s.TokenIndex).ToArray());
        for (int i = 0; i < expectation.Supports.Count; i++)
        {
            var support = expectation.Supports[i]; bool head = i == 0;
            Assert.AreEqual(head ? TextContextRole.Subject : TextContextRole.Attribute, support.Role);
            Assert.AreEqual(actual.Tokens[support.TokenIndex].Start, support.Start);
            Assert.AreEqual(actual.Tokens[support.TokenIndex].Length, support.Length);
            Assert.AreEqual(actual.Tokens[support.TokenIndex].Text, fixture.Text.Substring(support.Start, support.Length));
            if (fixture.GraphTokens.Contains(support.TokenIndex)) NfAssertGraph(actual, support, head, fixture.Number);
            else
            {
                Assert.IsTrue(support.GraphReadings is null || support.GraphReadings.Count == 0);
                var inventory = NfComplete(before, support.TokenIndex);
                // This is source-ordinal preservation against an independent baseline
                // inventory. Third/Number and the physical participant set are frozen gold.
                int[] expected = Enumerable.Range(0, inventory.Count).Where(r =>
                {
                    var f = inventory[r].Features;
                    return f.PartOfSpeech == (head ? MorphologicalPartOfSpeech.Noun : MorphologicalPartOfSpeech.Adjective) &&
                        f.Number == fixture.Number && (fixture.Number == MorphologicalNumber.Plural || f.Gender == MorphologicalGender.Masculine) &&
                        !f.HasConflictingFeatures && !f.HasUnrecognizedFeatures;
                }).ToArray();
                Assert.IsNotEmpty(expected);
                CollectionAssert.AreEqual(expected.Select(r => new TextReadingReference(support.TokenIndex, r)).ToArray(), support.Readings.ToArray());
                foreach (var reference in support.Readings)
                    Assert.AreEqual(JsonSerializer.Serialize(inventory[reference.CompleteReadingIndex]),
                        JsonSerializer.Serialize(NfComplete(actual, support.TokenIndex)[reference.CompleteReadingIndex]));
            }
        }
        TextContinuationSuggestion candidate = result.Suggestions.Single(s => s.Form == (fixture.Number == MorphologicalNumber.Plural ? "работят" : "работи"));
        NfAssertCandidate(candidate, fixture.Number);
        foreach (var suggestion in result.Suggestions)
        foreach (var match in suggestion.Matches)
        {
            var f = suggestion.Readings[match.ReadingIndex].Features;
            Assert.AreEqual(MorphologicalVerbForm.Finite, f.VerbForm); Assert.AreEqual(MorphologicalPerson.Third, f.Person);
            Assert.AreEqual(fixture.Number, f.Number); CollectionAssert.AreEqual(new[] { 0 }, match.ExpectationIndices.ToArray());
        }
        string simple = fixture.Number == MorphologicalNumber.Plural ? "Супертрактори раб" : "трактор раб";
        var baseline = session.AnalyzeText(simple, false, new() { Continuation = new(simple.Length, 256) { Target = target } }, default).Continuation!;
        Assert.IsTrue(baseline.Available);
        // Preservation only: the old simple context has the same finite axes. No
        // exact global rank is prescribed or selected from this returned list.
        CollectionAssert.AreEqual(baseline.Suggestions.Select(s => JsonSerializer.Serialize(s)).ToArray(),
            result.Suggestions.Select(s => JsonSerializer.Serialize(s)).ToArray());
        Assert.AreEqual(baseline.Truncated, result.Truncated); Assert.AreEqual(baseline.SearchComplete, result.SearchComplete);
        if (id == "P07-plural-exact-modifiers-graph-head") Assert.IsTrue(actual.Tokens[fixture.Head].Analysis!.GraphCandidatesTruncated);
        if (id == "P08-previous-emoji-sentence-midword")
            Assert.AreEqual("😀. Хубав, хубав трактор работи вдясно", fixture.Text[..result.Start] + candidate.Form + fixture.Text[(result.Start + result.Length)..]);
        Assert.AreEqual(JsonSerializer.Serialize(before), JsonSerializer.Serialize(actual with { Continuation = null }));
    }

    [TestMethod]
    [DataRow("N01-head-comma", false)]
    [DataRow("N01-head-comma", true)]
    [DataRow("N02-two-physical-heads", false)]
    [DataRow("N02-two-physical-heads", true)]
    [DataRow("N03-sentence-boundary", false)]
    [DataRow("N03-sentence-boundary", true)]
    [DataRow("N04-quoted-context", false)]
    [DataRow("N04-quoted-context", true)]
    [DataRow("N05-clause-or-committed-predicate", false)]
    [DataRow("N05-clause-or-committed-predicate", true)]
    [DataRow("N06-same-sentence-emoji-prefix", false)]
    [DataRow("N06-same-sentence-emoji-prefix", true)]
    [DataRow("N07-incompatible-gender", false)]
    [DataRow("N07-incompatible-gender", true)]
    [DataRow("N08-modifier-to-head-comma", false)]
    [DataRow("N08-modifier-to-head-comma", true)]
    [DataRow("N09-and-to-head", false)]
    [DataRow("N09-and-to-head", true)]
    [DataRow("N10-double-boundary", false)]
    [DataRow("N10-double-boundary", true)]
    public void Nominal_finite_successor_keeps_frozen_package_topology_negatives(string id, bool automatic)
    {
        NfRequireBaseline(); string text = NfNegative(id);
        TextAnalysis before = session.AnalyzeText(text);
        TextAnalysis actual = session.AnalyzeText(text, false, new()
            { Continuation = new(text.Length, 256) { Target = automatic ? TextContinuationTarget.Auto : TextContinuationTarget.Finite } }, default);
        var result = actual.Continuation!;
        Assert.IsFalse(result.Available); Assert.IsNotEmpty(result.UnavailableReason!);
        Assert.IsEmpty(result.Expectations); Assert.IsEmpty(result.Suggestions);
        Assert.IsFalse(result.SearchComplete); Assert.IsFalse(result.Truncated); Assert.AreEqual(0L, result.ExaminedReadings);
        Assert.AreEqual(JsonSerializer.Serialize(before), JsonSerializer.Serialize(actual with { Continuation = null }));
    }

    [TestMethod]
    public void Nominal_finite_successor_public_cancellation_preserves_session_and_default_wire()
    {
        NfRequireBaseline(); const string text = "Хубав, хубав и хубав трактор раб";
        TextAnalysis before = session.AnalyzeText(text); Assert.IsNull(before.Continuation);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.AreEqual(cancel.Token, Assert.ThrowsExactly<OperationCanceledException>(() => session.AnalyzeText(text, false,
            new() { Continuation = new(text.Length) { Target = TextContinuationTarget.Auto } }, cancel.Token)).CancellationToken);
        var retry = session.AnalyzeText(text, false, new() { Continuation = new(text.Length) }, default);
        Assert.IsTrue(retry.Continuation!.Available); Assert.AreEqual(JsonSerializer.Serialize(before), JsonSerializer.Serialize(retry with { Continuation = null }));
    }

    private static void NfRequireBaseline()
    {
        string? selected = Environment.GetEnvironmentVariable("BGLANG_PUBLIC_PACKAGE_MANIFEST");
        Assert.IsFalse(string.IsNullOrWhiteSpace(selected), "Complete Baseline36b0 must be selected; the gate never skips.");
        Assert.IsTrue(selected == selected!.Trim() && Path.IsPathFullyQualified(selected!));
        Assert.AreEqual(Path.GetFullPath(selected!), manifest);
        Assert.AreEqual("451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7", Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(manifest))));
        Assert.AreEqual("bglang-neutral:36b0b69685d6d3e9", session.Provenance.PackageId);
    }

    private static IReadOnlyList<TextReadingSnapshot> NfComplete(TextAnalysis analysis, int token)
    {
        var inventory = analysis.ReadingInventory!;
        return inventory.Sets[inventory.Occurrences.Single(o => o.TokenIndex == token).ReadingSetIndex].ResolvedReadings;
    }

    private static void NfAssertGraph(TextAnalysis analysis, TextContextRoleSelection support, bool head, MorphologicalNumber number)
    {
        int token = support.TokenIndex; Assert.IsEmpty(support.Readings);
        CollectionAssert.AreEqual(new[] { new TextGraphReadingReference(token, 0, 0) }, support.GraphReadings!.ToArray());
        WordAnalysis word = analysis.Tokens[token].Analysis!; Assert.IsFalse(word.IsKnown); Assert.IsEmpty(word.Readings); Assert.IsEmpty(NfComplete(analysis, token));
        Assert.IsTrue(word.GraphExactRootCandidatesComplete); Assert.IsFalse(word.GraphSearchIncomplete);
        WordDerivation path = word.GraphCandidates[0]; Assert.IsFalse(path.IsConditional);
        Assert.IsFalse(path.UnmetExactSelfReadingRequirements?.Count > 0);
        Assert.AreEqual(head ? "супертрактор" : "суперхубав", path.DerivedLemma);
        Assert.AreEqual(head ? number == MorphologicalNumber.Plural ? "Съществително:7,Мъжки,мн.ч." : "Съществително:7,Мъжки" : "Прилагателно:76,Мъжки", path.FullTags![0]);
        Assert.AreEqual(path.FullTags[0], path.Features[0].RawTag);
    }

    private static void NfAssertCandidate(TextContinuationSuggestion candidate, MorphologicalNumber number)
    {
        (string Lemma, string Tag)[] expected = number == MorphologicalNumber.Plural
            ? [("работя", "Глагол:173ti,3л.,Сегашно,мн.ч.")]
            : [("работа", "Съществително:41,Женски,мн.ч."), ("работя", "Глагол:173ti,1л.,Сегашно,Повелително"),
               ("работя", "Глагол:173ti,2л.,Минало_свършено"), ("работя", "Глагол:173ti,3л.,Минало_свършено"), ("работя", "Глагол:173ti,3л.,Сегашно")];
        Assert.HasCount(expected.Length, candidate.Readings);
        for (int i = 0; i < expected.Length; i++)
        {
            WordReading raw = candidate.Readings[i]; Assert.AreEqual(candidate.Form, raw.Form);
            Assert.AreEqual(expected[i].Lemma, raw.Lemma); Assert.AreEqual(expected[i].Tag, raw.PartOfSpeech);
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(1d), BitConverter.DoubleToInt64Bits(raw.Score)); Assert.AreEqual(NfSource, raw.Source);
            Assert.IsNull(raw.LegacyTypeId); Assert.IsNull(raw.LegacyBaseId); Assert.IsNull(raw.LegacyWordId);
        }
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(1d), BitConverter.DoubleToInt64Bits(candidate.Score));
        CollectionAssert.AreEqual(number == MorphologicalNumber.Plural ? new[] { 0 } : new[] { 3, 4 }, candidate.Matches.Select(m => m.ReadingIndex).ToArray());
        foreach (var match in candidate.Matches) CollectionAssert.AreEqual(new[] { 0 }, match.ExpectationIndices.ToArray());
    }

    private static NfCase NfPositive(string id) => id switch
    {
        "P01-existing-exact-whitespace" => new("Хубав трактор раб", 17, 14, 3, "раб", 1, [0], [], MorphologicalNumber.Singular, [new(TokenKind.Word, "Хубав", 0, 5), new(TokenKind.Word, "трактор", 6, 7), new(TokenKind.Word, "раб", 14, 3)]),
        "P02-exact-modifier-comma" => new("Хубав, хубав трактор раб", 24, 21, 3, "раб", 3, [0, 2], [], MorphologicalNumber.Singular, [new(TokenKind.Word, "Хубав", 0, 5), new(TokenKind.Punctuation, ",", 5, 1), new(TokenKind.Word, "хубав", 7, 5), new(TokenKind.Word, "трактор", 13, 7), new(TokenKind.Word, "раб", 21, 3)]),
        "P03-exact-comma-and" => new("Хубав, хубав и хубав трактор раб", 32, 29, 3, "раб", 5, [0, 2, 4], [], MorphologicalNumber.Singular, [new(TokenKind.Word, "Хубав", 0, 5), new(TokenKind.Punctuation, ",", 5, 1), new(TokenKind.Word, "хубав", 7, 5), new(TokenKind.Word, "и", 13, 1), new(TokenKind.Word, "хубав", 15, 5), new(TokenKind.Word, "трактор", 21, 7), new(TokenKind.Word, "раб", 29, 3)]),
        "P04-all-licensed-graph" => new("Суперхубав, суперхубав и суперхубав супертрактор раб", 52, 49, 3, "раб", 5, [0, 2, 4], [0, 2, 4, 5], MorphologicalNumber.Singular, [new(TokenKind.Word, "Суперхубав", 0, 10), new(TokenKind.Punctuation, ",", 10, 1), new(TokenKind.Word, "суперхубав", 12, 10), new(TokenKind.Word, "и", 23, 1), new(TokenKind.Word, "суперхубав", 25, 10), new(TokenKind.Word, "супертрактор", 36, 12), new(TokenKind.Word, "раб", 49, 3)]),
        "P05-mixed-exact-modifiers-graph-head" => new("Хубав, хубав супертрактор раб", 29, 26, 3, "раб", 3, [0, 2], [3], MorphologicalNumber.Singular, [new(TokenKind.Word, "Хубав", 0, 5), new(TokenKind.Punctuation, ",", 5, 1), new(TokenKind.Word, "хубав", 7, 5), new(TokenKind.Word, "супертрактор", 13, 12), new(TokenKind.Word, "раб", 26, 3)]),
        "P06-mixed-graph-modifiers-exact-head" => new("Суперхубав, суперхубав трактор раб", 34, 31, 3, "раб", 3, [0, 2], [0, 2], MorphologicalNumber.Singular, [new(TokenKind.Word, "Суперхубав", 0, 10), new(TokenKind.Punctuation, ",", 10, 1), new(TokenKind.Word, "суперхубав", 12, 10), new(TokenKind.Word, "трактор", 23, 7), new(TokenKind.Word, "раб", 31, 3)]),
        "P07-plural-exact-modifiers-graph-head" => new("Нови, нови супертрактори раб", 28, 25, 3, "раб", 3, [0, 2], [3], MorphologicalNumber.Plural, [new(TokenKind.Word, "Нови", 0, 4), new(TokenKind.Punctuation, ",", 4, 1), new(TokenKind.Word, "нови", 6, 4), new(TokenKind.Word, "супертрактори", 11, 13), new(TokenKind.Word, "раб", 25, 3)]),
        "P08-previous-emoji-sentence-midword" => new("😀. Хубав, хубав трактор работят вдясно", 28, 25, 7, "раб", 5, [2, 4], [], MorphologicalNumber.Singular, [new(TokenKind.Symbol, "😀", 0, 2), new(TokenKind.Punctuation, ".", 2, 1), new(TokenKind.Word, "Хубав", 4, 5), new(TokenKind.Punctuation, ",", 9, 1), new(TokenKind.Word, "хубав", 11, 5), new(TokenKind.Word, "трактор", 17, 7), new(TokenKind.Word, "работят", 25, 7), new(TokenKind.Word, "вдясно", 33, 6)]),
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };
    private static string NfNegative(string id) => id switch
    {
        "N01-head-comma" => "Хубав трактор, раб",
        "N02-two-physical-heads" => "Хубав трактор и хубав трактор раб",
        "N03-sentence-boundary" => "Хубав трактор. Раб",
        "N04-quoted-context" => "„Хубав, хубав трактор“ раб",
        "N05-clause-or-committed-predicate" => "Работи, хубав трактор раб",
        "N06-same-sentence-emoji-prefix" => "😀 Хубав, хубав трактор раб",
        "N07-incompatible-gender" => "Суперхубав, суперхубав суперкнига раб",
        "N08-modifier-to-head-comma" => "Хубав, трактор раб",
        "N09-and-to-head" => "Хубав и трактор раб",
        "N10-double-boundary" => "Хубав, и хубав трактор раб",
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };
}
