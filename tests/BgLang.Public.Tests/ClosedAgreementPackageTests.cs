using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

// Add only the partial keyword to PublicPackageTests's existing declaration.
// Reuse its one strict complete-package initializer/session; no second selector/open.
// Literal gold and complete-row fingerprints were frozen before the implementation.
// Premeasurement capture SHA256:59ef8dcb5afea07fbf1f551738935ca4f01624704d9af5fa23d06bd3ff577c9a.
public sealed partial class PublicPackageTests
{
    private const string ClosedAgreementRule = "closed_exact_agreement.v1";
    private const string ClosedFiniteCode = "productive_morphology_agreement_mismatch";
    private const string ClosedAttributeCode = "productive_attribute_agreement_mismatch";

    [TestMethod]
    [DataRow("Аз чете.", "productive_morphology_agreement_mismatch", 1, 3, 4, "0:0|1:0|1:1|1:2", "", 0, "SourceScan")]
    [DataRow("Ние работи.", "productive_morphology_agreement_mismatch", 1, 4, 6, "0:0|1:2|1:3|1:4", "Ambiguous", 0, "Abstained")]
    [DataRow("Те спи.", "productive_morphology_agreement_mismatch", 1, 3, 3, "0:0|1:1", "", 0, "SourceScan")]
    [DataRow("Децата играе.", "productive_morphology_agreement_mismatch", 1, 7, 5, "0:0|1:0", "", 0, "SourceScan")]
    [DataRow("Котката спят.", "productive_morphology_agreement_mismatch", 1, 8, 4, "0:0|1:0", "", 0, "SourceScan")]
    [DataRow("Малкото дете плачат.", "productive_morphology_agreement_mismatch", 2, 13, 6, "1:0|2:0", "", 0, "SourceScan")]
    [DataRow("Малкият дете плаче.", "productive_attribute_agreement_mismatch", 0, 0, 7, "0:0|1:0", "", 0, "SourceScan")]
    [DataRow("Старата къща рухнаха.", "productive_morphology_agreement_mismatch", 2, 13, 7, "1:0|2:0", "", 0, "SourceScan")]
    [DataRow("Старият къща рухна.", "productive_attribute_agreement_mismatch", 0, 0, 7, "0:0|1:0", "", 0, "SourceScan")]
    [DataRow("Високите дървета шуми.", "productive_morphology_agreement_mismatch", 2, 17, 4, "1:1|2:2", "Ambiguous", 0, "Abstained")]
    [DataRow("Аз чета.", "", -1, -1, 0, "", "Selected", 2, "Selected")]
    [DataRow("Ти чете.", "", -1, -1, 0, "", "Ambiguous", 0, "Abstained")]
    [DataRow("Ние работим.", "", -1, -1, 0, "", "", 0, "SourceScan")]
    [DataRow("Те спят.", "", -1, -1, 0, "", "", 0, "SourceScan")]
    [DataRow("Децата играят.", "", -1, -1, 0, "", "", 0, "SourceScan")]
    [DataRow("Котката спи.", "", -1, -1, 0, "", "", 0, "SourceScan")]
    [DataRow("Малкото дете плаче.", "", -1, -1, 0, "", "", 0, "SourceScan")]
    [DataRow("Старата къща рухна.", "", -1, -1, 0, "", "", 0, "SourceScan")]
    [DataRow("Високите дървета шумят.", "", -1, -1, 0, "", "Ambiguous", 0, "Abstained")]
    [DataRow("Аз работи.", "productive_morphology_agreement_mismatch", 1, 3, 6, "0:0|1:2|1:3|1:4", "Ambiguous", 0, "Abstained")]
    [DataRow("Ти работи.", "", -1, -1, 0, "", "Ambiguous", 0, "Abstained")]
    [DataRow("Той работи.", "", -1, -1, 0, "", "Selected", 2, "Selected")]
    [DataRow("Новите деца играят.", "", -1, -1, 0, "", "", 0, "SourceScan")]
    public void Closed_exact_agreement_matches_literal23_gold_without_new_selection_or_reading_mutation(
        string text, string code, int targetToken, int start, int length, string references,
        string contextStatus, int selectedRoleCount, string syntaxRoute)
    {
        Assert.AreEqual("bglang-neutral:36b0b69685d6d3e9", session.Provenance.PackageId);
        Assert.AreEqual("451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7", session.Provenance.ManifestSha256.ToLowerInvariant());
        string[] forms = text[..^1].Split(' ');
        foreach (string form in forms) ClosedAssertPinned(form, session.Lookup(form, LookupMode.Exact, 256)
            .Select(row => new TextReadingSnapshot(row, row.Features)).ToArray());
        TextAnalysis actual = session.AnalyzeText(text, false, new() { IncludeAdditionalRoleHeuristics = true }, default);
        Assert.AreEqual(text, actual.Text); Assert.IsNotNull(actual.ReadingInventory);
        Assert.HasCount(forms.Length, actual.ReadingInventory.Occurrences);
        foreach (TextReadingOccurrence occurrence in actual.ReadingInventory.Occurrences)
        {
            Assert.AreEqual(actual.Tokens[occurrence.TokenIndex].Text, occurrence.Text);
            Assert.AreEqual(occurrence.Text, text.Substring(occurrence.Start, occurrence.Length));
            TextReadingSet set = actual.ReadingInventory.Sets[occurrence.ReadingSetIndex];
            ClosedAssertPinned(set.CanonicalKey, set.ResolvedReadings);
            CollectionAssert.AreEqual(set.ResolvedReadings.Take(set.DisplayedReadingCount).Select(row => row.Raw).ToArray(),
                actual.Tokens[occurrence.TokenIndex].Analysis!.Readings.ToArray(), "Display remains its actual complete-inventory prefix.");
        }
        TextDiagnostic[] warnings = actual.Diagnostics.Where(d => d.RuleId == ClosedAgreementRule).ToArray();
        Assert.HasCount(code.Length == 0 ? 0 : 1, warnings, text);
        if (warnings.Length != 0)
        {
            TextDiagnostic warning = warnings.Single();
            Assert.AreEqual(code, warning.Code); Assert.AreEqual((start, length), (warning.Start, warning.Length));
            Assert.AreEqual((start, length), (actual.Tokens[targetToken].Start, actual.Tokens[targetToken].Length));
            Assert.AreEqual(actual.Tokens[targetToken].Text, text.Substring(start, length));
            Assert.IsNull(warning.ProjectedText); Assert.IsNull(warning.GraphReadings); Assert.IsNotNull(warning.Readings);
            Assert.AreEqual(references, string.Join('|', warning.Readings.Select(r => r.TokenIndex + ":" + r.CompleteReadingIndex)));
            foreach (TextReadingReference reference in warning.Readings)
            {
                TextReadingOccurrence occurrence = actual.ReadingInventory.Occurrences.Single(o => o.TokenIndex == reference.TokenIndex);
                var rows = actual.ReadingInventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings;
                Assert.IsTrue(reference.CompleteReadingIndex >= 0 && reference.CompleteReadingIndex < rows.Count);
                if (code == ClosedFiniteCode && reference.TokenIndex == targetToken)
                    Assert.AreEqual(MorphologicalVerbForm.Finite, rows[reference.CompleteReadingIndex].Features.VerbForm,
                        "Imperative alternatives veto mismatches but are never promoted to finite proof refs.");
            }
        }
        TextContextSentence context = actual.Context!.Sentences.Single();
        Assert.AreEqual(contextStatus, context.Status?.ToString() ?? "");
        Assert.HasCount(selectedRoleCount, context.SelectedRoles);
        Assert.IsTrue(context.Candidates.Where(c => c.AuthorityLane == ClosedAgreementRule).All(c => !c.Selectable));
        Assert.AreEqual(syntaxRoute, actual.Syntax!.Sentences.Single().Route,
            "Diagnostic-only exact evidence does not promote a Context selection or graph Syntax route.");
        foreach (string form in forms) ClosedAssertPinned(form, session.Lookup(form, LookupMode.Exact, 256)
            .Select(row => new TextReadingSnapshot(row, row.Features)).ToArray());
    }

    [TestMethod]
    public void Closed_exact_addition_preserves_six_old_warning_identities_and_short_clitic_case()
    {
        const string text = "Аз четем. Ти четат. Ние чета. Вие четем. Ти не четат. Аз го четем.";
        TextAnalysis actual = session.AnalyzeText(text);
        TextDiagnostic[] old = actual.Diagnostics.Where(d => d.Code == "pronoun_verb_agreement_mismatch").ToArray();
        CollectionAssert.AreEqual(new[] { (3, 5), (13, 5), (24, 4), (34, 5), (47, 5), (60, 5) }, old.Select(d => (d.Start, d.Length)).ToArray());
        foreach (TextDiagnostic warning in old)
        {
            Assert.AreEqual("pronoun_verb_agreement", warning.RuleId);
            Assert.IsNull(warning.Readings); Assert.IsNull(warning.GraphReadings); Assert.IsNull(warning.ProjectedText);
            Assert.IsFalse(actual.Diagnostics.Any(d => d.RuleId == ClosedAgreementRule && d.Start == warning.Start && d.Length == warning.Length));
        }
    }

    private static void ClosedAssertPinned(string word, IReadOnlyList<TextReadingSnapshot> rows)
    {
        (int count, string sha) = ClosedPinnedRows(word.ToLowerInvariant());
        Assert.HasCount(count, rows, word);
        string body = string.Join('\n', rows.Select(ClosedRowIdentity));
        Assert.AreEqual(sha, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))),
            "Every full raw row and typed feature remains at its premeasurement original position: " + word);
    }
    private static string ClosedRowIdentity(TextReadingSnapshot row)
    {
        WordReading r = row.Raw; MorphologicalFeatures f = row.Features;
        return string.Join('|', new[] { r.Form, r.Lemma, r.PartOfSpeech ?? "", BitConverter.DoubleToInt64Bits(r.Score).ToString("x16", CultureInfo.InvariantCulture),
            r.LegacyTypeId?.ToString(CultureInfo.InvariantCulture) ?? "", r.LegacyBaseId?.ToString(CultureInfo.InvariantCulture) ?? "", r.LegacyWordId?.ToString(CultureInfo.InvariantCulture) ?? "", r.Source,
            f.RawTag ?? "", f.PartOfSpeech.ToString(), f.ParadigmId ?? "", f.Person?.ToString() ?? "", f.Number?.ToString() ?? "", f.Gender?.ToString() ?? "", f.Tense?.ToString() ?? "",
            f.VerbForm?.ToString() ?? "", f.Article?.ToString() ?? "", f.HasUnrecognizedFeatures.ToString(), f.HasConflictingFeatures.ToString(), f.NameKind?.ToString() ?? "",
            f.Case?.ToString() ?? "", f.IsShortForm?.ToString() ?? "", f.ParticipleKind?.ToString() ?? "" });
    }
    private static (int Count, string Sha) ClosedPinnedRows(string word) => word switch
    {
        "аз" => (1, "e1af042ad935771149ceabc92aa9e02d2026febfa37f71a4c724782cda58dc4e"),
        "високите" => (2, "eb6ea7fcaf899c9cdc5ecb346a4429ca8ff58bc47856ce544a1c5989b6106dd0"),
        "дете" => (1, "353c793fca42acd89664b337138e47974b0bb06cff26583d0069d7aa71d28636"),
        "деца" => (1, "945738c0b299832c91d5a9b8e90f67ce33bc762320bdde3e96414ae5bd616ef8"),
        "децата" => (1, "60113e7963d6a64bbff526354536e88d2da5672d7f71f10168b741c31724e0bc"),
        "дървета" => (2, "76e9220ec913fd21faee74c22b33c2f115b1cb3a91330d5664d8048abcd69bd1"),
        "играе" => (1, "249f0e7d0381c0fafe2be41830912add910fcda33ef4d8fb88a4d3028185d2fa"),
        "играят" => (1, "595b092d357c9877163412dc65b76be11fa64ad6e1e235acad5174bc137038aa"),
        "котката" => (1, "d6ae160d13c9830f0e9a3ba7c9a7da57310ea4415035766721b0562b08527d3b"),
        "къща" => (1, "4e145c9218c5202d6d6557bc5a1dd3c57aab187ca98ffe1c17c5604b3b096335"),
        "малкият" => (1, "aed79fa28dc89d455d575336c84dcba8222a14440d7da92c1adbf96e3025652e"),
        "малкото" => (1, "e2b65ccdbb73b0101348abba114064ea493d63007bfc3dcfda6a287fbd20dbdc"),
        "ние" => (1, "fdb267d28b17f6a184951f5e76ce83daeec195821c14fd079a389a11fbe48f07"),
        "новите" => (1, "c28fc493def3cb6318570d27e275e85b81aab1bf7a4631d7840ba0f546329a71"),
        "плачат" => (1, "84d736cb488a50bd7c5f3fb7f3b85604fcb0ca4c3cc85184cdf3d707ca9996f1"),
        "плаче" => (1, "2d789a5f8649f0c1e6e753a988867be4d966a3501eda62a359e9a7b1c874405c"),
        "работи" => (5, "4757ec7102f6ceeb756d585fe6d64322b891c6a8f05d46a5b644b3122f38defe"),
        "работим" => (1, "74aa7c1a7da0c41b4e89f361024c18c1a97ed4ecb6673ea0c46e7a99387e1e7d"),
        "рухна" => (3, "26a19da2e1e5f536d73f0124bc330c3527cb9e85157d1b735a4e6b1f2ba19ad8"),
        "рухнаха" => (1, "0823cb615c8295b895f845aa1e461ab29098df68b3349f6049eba110a8ca390d"),
        "спи" => (2, "9773cd6605446befc1e08176ee0f59e3a941efbd36bf21ff370d80a662f25145"),
        "спят" => (1, "d1b216a9458a16a3888bfbd0c545cb2b25bd1ebe7e600181b1b510c15aed3a7e"),
        "старата" => (1, "ce2c1d37839492134d8402986fd081fdefde7f57791d52673bbaa85671832e02"),
        "старият" => (1, "e145ea9630aa66b205947aa238b8dc838c13e1e36603608f98ae8cf8291b2310"),
        "те" => (3, "8f76f0f1bc2b22f6fb6b9dedf29d124056e2a0edddb5f3362cb32052b4005cb0"),
        "ти" => (4, "8ff398affb7f549501a0c97526abfd55578a645337cbe3bf0da56d6204091c20"),
        "той" => (1, "1bf63f85069bd69583d7e1d02fa3da926fd66bbb53206a760f20d22d90b54d53"),
        "чета" => (2, "2f3d16d9c89f9e69112d45d2c5418f120e1114c566099dfae853c2a983bbd6f6"),
        "чете" => (3, "e22c843e483c91a11388c185c5ce51dd7cdf36e15ca8e23b6aea2ceee831a983"),
        "шуми" => (3, "e17d8148757583c35c7d736f9bbf35e9d3117f1b8e74f8f4e7fefb5efeb007f7"),
        "шумят" => (1, "c6dcfc379da12ddc80dfddab9b06cb36a3c7141ba8b0ea9682b48dca86042dd4"),
        _ => throw new InvalidOperationException("No frozen complete-reading oracle for " + word)
    };

    [TestMethod]
    public void Spelling_context_support_is_advisory_and_preserves_raw_fuzzy_and_complete_readings()
    {
        Assert.AreEqual("bglang-neutral:36b0b69685d6d3e9", session.Provenance.PackageId);
        Assert.AreEqual("451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7", session.Provenance.ManifestSha256.ToLowerInvariant());
        IReadOnlyList<FuzzyMatch> rawBefore = session.FindSimilar("друфа", 2, 10);
        CollectionAssert.AreEqual(new[] { "друга", "дружа", "друза", "друма", "друса", "Аруба", "арфа", "бруса", "графа", "грифа" },
            rawBefore.Select(a => a.Form).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 1, 1, 1, 1, 2, 2, 2, 2, 2 }, rawBefore.Select(a => a.Distance).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 1, 1, 4, 3, 1, 1, 2, 5, 2 }, rawBefore.Select(a => a.Readings.Count).ToArray());
        Assert.HasCount(1, rawBefore[0].Readings);
        MorphologicalFeatures adjective = rawBefore[0].Readings[0].Features;
        Assert.AreEqual("Прилагателно:76,Женски", adjective.RawTag);
        Assert.AreEqual(MorphologicalPartOfSpeech.Adjective, adjective.PartOfSpeech);
        Assert.AreEqual(MorphologicalNumber.Singular, adjective.Number);
        Assert.AreEqual(MorphologicalGender.Feminine, adjective.Gender);
        var nounBefore = session.Lookup("дума", LookupMode.Exact, 256).Select(r => new TextReadingSnapshot(r, r.Features)).ToArray();
        CollectionAssert.AreEqual(new[] { "Съществително:41,Женски", "Глагол:186ti,2л.,Минало_свършено",
            "Глагол:186ti,3л.,Минало_свършено", "Глагол:186ti,3л.,Сегашно" }, nounBefore.Select(r => r.Raw.PartOfSpeech).ToArray());

        TextAnalysis typo = Analyze("Това е друфа дума");
        AssertSupport(typo, 2, 3, 7);
        TextAnalysis known = Analyze("Това е друга дума");
        SpellingWordEvidence knownWord = known.Spelling!.Words.Single(w => w.TokenIndex == 2);
        Assert.AreEqual(SpellingSearchDisposition.NotRequestedKnown, knownWord.Search);
        Assert.IsEmpty(knownWord.Alternatives); Assert.IsNull(knownWord.ContextualCandidates);
        Assert.IsTrue(known.Tokens[2].Analysis!.IsKnown); AssertNoun(known, 3); AssertNoUnique(known);

        TextAnalysis unfinished = Analyze("Това е дру");
        SpellingWordEvidence unfinishedWord = unfinished.Spelling!.Words.Single(w => w.TokenIndex == 2);
        Assert.AreEqual(SpellingSearchDisposition.Completed, unfinishedWord.Search);
        Assert.IsFalse(unfinished.Tokens[2].Analysis!.IsKnown); Assert.IsNull(unfinishedWord.ContextualCandidates);
        EqualRaw(session.FindSimilar("дру", 2, 10), unfinishedWord.Alternatives); AssertNoUnique(unfinished);
        TextAnalysis name = Analyze("Друфа");
        SpellingWordEvidence nameWord = name.Spelling!.Words.Single();
        Assert.AreEqual(SpellingSearchDisposition.Completed, nameWord.Search);
        Assert.IsFalse(name.Tokens[0].Analysis!.IsKnown); Assert.IsNull(nameWord.ContextualCandidates);
        Assert.IsTrue(name.ProperNames!.UnresolvedContextTokens.Any(t => t.TokenIndex == 0 && t.Reason == "sentence-initial-context-insufficient"));
        EqualRaw(rawBefore, nameWord.Alternatives); AssertNoUnique(name);

        // Prospective negative: do not invent a package-specific count/tag/index for дом before its measurement.
        TextAnalysis masculine = Analyze("Това е друфа дом");
        SpellingWordEvidence beforeDom = masculine.Spelling!.Words.Single(w => w.TokenIndex == 2);
        EqualRaw(rawBefore, beforeDom.Alternatives);
        SpellingContextCandidate? domCandidate = beforeDom.ContextualCandidates?.Single(c => c.AlternativeIndex == 0);
        Assert.IsTrue(domCandidate?.Assessment != SpellingContextAssessment.Supported,
            "The following дом cannot inherit the feminine support measured for дума in another request.");
        Assert.IsTrue(domCandidate is null || domCandidate.Matches.Count == 0);
        CollectionAssert.AreEqual(session.Lookup("дом", LookupMode.Exact, 256).Select(r => new TextReadingSnapshot(r, r.Features)).ToArray(),
            Rows(masculine, 3).ToArray(), "Every actual дом row remains in its original complete inventory.");
        AssertNoUnique(masculine);

        TextAnalysis unicode = Analyze("😀 Това е друфа дума");
        SpellingWordEvidence unicodeWord = unicode.Spelling!.Words.Single(w => w.Text == "друфа");
        AssertSupport(unicode, unicodeWord.TokenIndex, unicodeWord.TokenIndex + 1, 10);
        TextAnalysis repeated = Analyze("Това е друфа дума. Друфа");
        AssertSupport(repeated, 2, 3, 7);
        SpellingWordEvidence[] repeatedWords = repeated.Spelling!.Words.Where(w => w.NormalizedQuery == "друфа").ToArray();
        Assert.HasCount(2, repeatedWords);
        foreach (SpellingWordEvidence word in repeatedWords) EqualRaw(rawBefore, word.Alternatives);
        Assert.IsNull(repeatedWords[1].ContextualCandidates, "A shared normalized fuzzy cache does not copy occurrence-local noun support.");
        EqualRaw(rawBefore, session.FindSimilar("друфа", 2, 10));
        CollectionAssert.AreEqual(nounBefore, session.Lookup("дума", LookupMode.Exact, 256).Select(r => new TextReadingSnapshot(r, r.Features)).ToArray());

        TextAnalysis Analyze(string text) => session.AnalyzeText(text, true, new() { IncludeAdditionalRoleHeuristics = true }, default);
        IReadOnlyList<TextReadingSnapshot> Rows(TextAnalysis analysis, int token)
        {
            TextReadingInventory inventory = analysis.ReadingInventory!;
            return inventory.Sets[inventory.Occurrences.Single(o => o.TokenIndex == token).ReadingSetIndex].ResolvedReadings;
        }
        void EqualRaw(IReadOnlyList<FuzzyMatch> expected, IReadOnlyList<FuzzyMatch> actual)
        {
            CollectionAssert.AreEqual(expected.Select(a => (a.Form, a.Distance)).ToArray(), actual.Select(a => (a.Form, a.Distance)).ToArray());
            for (int i = 0; i < expected.Count; i++)
                CollectionAssert.AreEqual(expected[i].Readings.ToArray(), actual[i].Readings.ToArray(), "All raw candidate readings retain their original order and fields.");
        }
        void AssertNoun(TextAnalysis analysis, int nounToken)
        {
            IReadOnlyList<TextReadingSnapshot> rows = Rows(analysis, nounToken);
            CollectionAssert.AreEqual(nounBefore, rows.ToArray());
            Assert.HasCount(4, rows); Assert.AreEqual(MorphologicalPartOfSpeech.Noun, rows[0].Features.PartOfSpeech);
            Assert.AreEqual(MorphologicalNumber.Singular, rows[0].Features.Number); Assert.AreEqual(MorphologicalGender.Feminine, rows[0].Features.Gender);
            Assert.IsTrue(rows.Skip(1).All(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Verb));
        }
        void AssertNoUnique(TextAnalysis analysis) => Assert.IsFalse(analysis.Diagnostics.Any(d =>
            d.Code == "spelling_unknown_word_unique_distance_one_candidate"), "Local support among five distance-one forms does not prove a unique correction.");
        void AssertSupport(TextAnalysis analysis, int tokenIndex, int nounToken, int expectedStart)
        {
            SpellingWordEvidence word = analysis.Spelling!.Words.Single(w => w.TokenIndex == tokenIndex);
            Assert.AreEqual(("друфа", expectedStart, 5), (word.Text, word.Start, word.Length));
            Assert.AreEqual((expectedStart, 5), (analysis.Tokens[tokenIndex].Start, analysis.Tokens[tokenIndex].Length));
            Assert.AreEqual("друфа", analysis.Text.Substring(expectedStart, 5));
            Assert.AreEqual(SpellingSearchDisposition.Completed, word.Search); EqualRaw(rawBefore, word.Alternatives);
            Assert.HasCount(10, word.Alternatives); Assert.AreEqual(5, word.Alternatives.Count(a => a.Distance == 1));
            Assert.IsNotNull(word.ContextualCandidates); Assert.HasCount(10, word.ContextualCandidates);
            CollectionAssert.AreEqual(Enumerable.Range(0, 10).ToArray(), word.ContextualCandidates.Select(c => c.AlternativeIndex).ToArray());
            SpellingContextCandidate candidate = word.ContextualCandidates.Single(c => c.AlternativeIndex == 0);
            Assert.AreEqual(SpellingContextAssessment.Supported, candidate.Assessment); Assert.HasCount(1, candidate.Matches);
            Assert.AreEqual(0, candidate.Matches[0].CandidateReadingIndex);
            Assert.AreEqual(new TextReadingReference(nounToken, 0), candidate.Matches[0].FollowingNounReading);
            AssertNoun(analysis, nounToken); AssertNoUnique(analysis);
        }
    }

    // Exact six-case ranks measured against the identified complete package; no runtime ranking change.
    private sealed record RankingPoolCase(string Id, string Text, string Typo, string Noun,
        string Target, MorphologicalGender Gender, int RawRank, int ContextualRank);
    private sealed record RankingPoolCapture(RankingPoolCase Case, TextAnalysis Analysis,
        SpellingWordEvidence Word);

    [TestMethod]
    public void Predeclared_spelling_pool_demonstrates_equal_distance_promotion_without_raw_changes()
    {
        Assert.AreEqual("bglang-neutral:36b0b69685d6d3e9", session.Provenance.PackageId);
        Assert.AreEqual("451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7",
            session.Provenance.ManifestSha256.ToLowerInvariant());
        RankingPoolCase[] pool =
        [
            new("new-neuter", "Това е новз дете.", "новз", "дете", "ново", MorphologicalGender.Neuter, 4, 1),
            new("new-feminine", "Това е новз книга.", "новз", "книга", "нова", MorphologicalGender.Feminine, 2, 1),
            new("pretty-neuter", "Това е хубавз дете.", "хубавз", "дете", "хубаво", MorphologicalGender.Neuter, 4, 1),
            new("pretty-masculine", "Това е хубавз дом.", "хубавз", "дом", "хубав", MorphologicalGender.Masculine, 1, 1),
            new("old-feminine", "Това е старз къща.", "старз", "къща", "стара", MorphologicalGender.Feminine, 2, 1),
            new("old-masculine", "Това е старз дом.", "старз", "дом", "стар", MorphologicalGender.Masculine, 1, 1)
        ];
        var raw = pool.Select(c => c.Typo).Distinct(StringComparer.Ordinal)
            .ToDictionary(q => q, q => session.FindSimilar(q, 2, 10), StringComparer.Ordinal);
        string rawBefore = JsonSerializer.Serialize(raw);
        RankingPoolCapture[] captured = pool.Select(CaptureRankingPoolCase).ToArray();

        // Emit every declared outcome before assertions: absent/bounded/nonpromoting cases stay visible.
        Console.WriteLine(JsonSerializer.Serialize(captured.Select(c =>
        {
            int before = RawTargetIndex(c), after = ContextTargetIndex(c);
            return new { c.Case.Id, c.Case.Text, c.Case.Target,
                rawRank = before < 0 ? (int?)null : before + 1,
                contextualRank = after < 0 ? (int?)null : after + 1,
                assessment = after < 0 ? null : c.Word.ContextualCandidates![after].Assessment.ToString(),
                distance = before < 0 ? (int?)null : c.Word.Alternatives[before].Distance,
                demonstratedPromotion = IsMeasuredPromotion(c) };
        })));

        int promotions = 0;
        foreach (RankingPoolCapture c in captured)
        {
            Assert.AreEqual(c.Case.RawRank, RawTargetIndex(c) + 1, c.Case.Id);
            Assert.AreEqual(c.Case.ContextualRank, ContextTargetIndex(c) + 1, c.Case.Id);
            Assert.IsTrue(c.Word.ContextualCandidates![ContextTargetIndex(c)].Matches.Any(m =>
                HasExpectedRankingWitness(c, RawTargetIndex(c), m)), c.Case.Id);
            Assert.AreEqual(SpellingSearchDisposition.Completed, c.Word.Search);
            Assert.AreEqual(c.Case.Typo, c.Word.NormalizedQuery);
            Assert.AreEqual(c.Word.Text, c.Analysis.Text.Substring(c.Word.Start, c.Word.Length));
            AssertRankingRaw(raw[c.Case.Typo], c.Word.Alternatives);
            int nounToken = c.Word.TokenIndex + 1;
            Assert.AreEqual(c.Case.Noun, c.Analysis.Tokens[nounToken].Text);
            Assert.IsTrue(RankingRows(c.Analysis, nounToken).Any(r =>
                r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
                r.Features.Number == MorphologicalNumber.Singular && r.Features.Gender == c.Case.Gender));

            AssertRankingOverlay(c); // original refs, coherent axes, distance primary and stable ties
            if (IsMeasuredPromotion(c)) promotions++;
            // Existing uniqueness is independent. Two distance-one raw forms cannot become unique
            // merely because this overlay supports one; do not reject a legitimate old unique diagnostic.
            if (c.Word.Alternatives.Count(a => a.Distance == 1) >= 2)
                Assert.IsFalse(c.Analysis.Diagnostics.Any(d =>
                    d.Code == "spelling_unknown_word_unique_distance_one_candidate" &&
                    d.Start == c.Word.Start && d.Length == c.Word.Length));
        }

        for (int pair = 0; pair < captured.Length; pair += 2)
        {
            RankingPoolCapture first = captured[pair], contrast = captured[pair + 1];
            AssertRankingRaw(first.Word.Alternatives, contrast.Word.Alternatives);
            AssertFormerGenderCannotUseContrastTuple(first, contrast);
            // A/B/A requests use only the same six declared texts; no occurrence-local support leaks.
            string retained = JsonSerializer.Serialize(first.Word);
            RankingPoolCapture replay = CaptureRankingPoolCase(first.Case);
            AssertRankingRaw(first.Word.Alternatives, replay.Word.Alternatives);
            Assert.AreEqual(retained, JsonSerializer.Serialize(first.Word), "A later request cannot mutate A.");
            Assert.AreEqual(retained, JsonSerializer.Serialize(replay.Word), "A/B/A context is request-local.");
        }
        foreach (var entry in raw) AssertRankingRaw(entry.Value, session.FindSimilar(entry.Key, 2, 10));
        Assert.AreEqual(rawBefore, JsonSerializer.Serialize(raw));

        Assert.IsTrue(promotions > 0,
            "The six-case pool did not demonstrate a non-first equal-distance promotion. " +
            "Retain every pool outcome and report the benefit-evidence gap; do not change runtime to force it.");

        RankingPoolCapture CaptureRankingPoolCase(RankingPoolCase c)
        {
            TextAnalysis a = session.AnalyzeText(c.Text, true,
                new TextContextOptions { IncludeAdditionalRoleHeuristics = true }, CancellationToken.None);
            return new(c, a, a.Spelling!.Words.Single(w => w.Text == c.Typo));
        }
    }

    private static int RawTargetIndex(RankingPoolCapture c) =>
        Enumerable.Range(0, c.Word.Alternatives.Count)
            .FirstOrDefault(i => StringComparer.Ordinal.Equals(c.Word.Alternatives[i].Form, c.Case.Target), -1);
    private static int ContextTargetIndex(RankingPoolCapture c)
    {
        int original = RawTargetIndex(c);
        return original < 0 || c.Word.ContextualCandidates is null ? -1 :
            Enumerable.Range(0, c.Word.ContextualCandidates.Count)
                .FirstOrDefault(i => c.Word.ContextualCandidates[i].AlternativeIndex == original, -1);
    }
    private static bool IsMeasuredPromotion(RankingPoolCapture c)
    {
        int before = RawTargetIndex(c), after = ContextTargetIndex(c);
        if (before <= 0 || after < 0 || after >= before) return false;
        SpellingContextCandidate target = c.Word.ContextualCandidates![after];
        if (target.Assessment != SpellingContextAssessment.Supported ||
            !target.Matches.Any(m => HasExpectedRankingWitness(c, before, m))) return false;
        return c.Word.ContextualCandidates.Skip(after + 1).Any(o => o.AlternativeIndex < before &&
            c.Word.Alternatives[o.AlternativeIndex].Distance == c.Word.Alternatives[before].Distance &&
            o.Assessment != SpellingContextAssessment.Supported);
    }
    private static bool HasExpectedRankingWitness(RankingPoolCapture c, int alternative,
        SpellingContextMatch match)
    {
        MorphologicalFeatures a = c.Word.Alternatives[alternative].Readings[match.CandidateReadingIndex].Features;
        MorphologicalFeatures n = RankingRows(c.Analysis, match.FollowingNounReading.TokenIndex)
            [match.FollowingNounReading.CompleteReadingIndex].Features;
        return a.PartOfSpeech == MorphologicalPartOfSpeech.Adjective &&
            n.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
            a.Number == MorphologicalNumber.Singular && n.Number == MorphologicalNumber.Singular &&
            a.Gender == c.Case.Gender && n.Gender == c.Case.Gender;
    }
    private static void AssertRankingRaw(IReadOnlyList<FuzzyMatch> expected, IReadOnlyList<FuzzyMatch> actual)
    {
        // Exact Form equality also protects meaningful dictionary case. WordReading record equality
        // includes Form/Lemma/PartOfSpeech/Score/Legacy IDs/Source and original reading order.
        CollectionAssert.AreEqual(expected.Select(a => (a.Form, a.Distance)).ToArray(),
            actual.Select(a => (a.Form, a.Distance)).ToArray());
        for (int i = 0; i < expected.Count; i++)
            CollectionAssert.AreEqual(expected[i].Readings.ToArray(), actual[i].Readings.ToArray());
    }
    private static IReadOnlyList<TextReadingSnapshot> RankingRows(TextAnalysis a, int token)
    {
        TextReadingInventory inventory = a.ReadingInventory!;
        return inventory.Sets[inventory.Occurrences.Single(o => o.TokenIndex == token).ReadingSetIndex].ResolvedReadings;
    }
    private static void AssertRankingOverlay(RankingPoolCapture c)
    {
        if (c.Word.ContextualCandidates is not { } overlay) return; // retained as a measured gap
        CollectionAssert.AreEquivalent(Enumerable.Range(0, c.Word.Alternatives.Count).ToArray(),
            overlay.Select(o => o.AlternativeIndex).ToArray());
        for (int i = 0; i < overlay.Count; i++)
        {
            var current = overlay[i];
            Assert.AreEqual(current.Assessment == SpellingContextAssessment.Supported, current.Matches.Count > 0);
            foreach (var m in current.Matches)
            {
                Assert.AreEqual(c.Word.TokenIndex + 1, m.FollowingNounReading.TokenIndex);
                Assert.IsTrue(m.CandidateReadingIndex >= 0 &&
                    m.CandidateReadingIndex < c.Word.Alternatives[current.AlternativeIndex].Readings.Count);
                var nounRows = RankingRows(c.Analysis, m.FollowingNounReading.TokenIndex);
                Assert.IsTrue(m.FollowingNounReading.CompleteReadingIndex >= 0 &&
                    m.FollowingNounReading.CompleteReadingIndex < nounRows.Count);
                var adjective = c.Word.Alternatives[current.AlternativeIndex].Readings[m.CandidateReadingIndex].Features;
                var noun = nounRows[m.FollowingNounReading.CompleteReadingIndex].Features;
                Assert.AreEqual(MorphologicalPartOfSpeech.Adjective, adjective.PartOfSpeech);
                Assert.AreEqual(MorphologicalPartOfSpeech.Noun, noun.PartOfSpeech);
                Assert.IsTrue(adjective.Number.HasValue && noun.Number.HasValue);
                Assert.AreEqual(adjective.Number, noun.Number);
                if (noun.Number == MorphologicalNumber.Singular)
                {
                    Assert.IsTrue(adjective.Gender.HasValue && noun.Gender.HasValue);
                    Assert.AreEqual(adjective.Gender, noun.Gender);
                }
            }
            for (int j = i + 1; j < overlay.Count; j++)
            {
                var later = overlay[j];
                int a = c.Word.Alternatives[current.AlternativeIndex].Distance;
                int b = c.Word.Alternatives[later.AlternativeIndex].Distance;
                Assert.IsTrue(a <= b, "Greater distance cannot outrank smaller distance through support.");
                if (a != b) continue;
                bool s = current.Assessment == SpellingContextAssessment.Supported;
                bool t = later.Assessment == SpellingContextAssessment.Supported;
                Assert.IsFalse(!s && t, "Supported precedes unsupported only within a distance level.");
                if (s == t) Assert.IsTrue(current.AlternativeIndex < later.AlternativeIndex, "Stable original ties.");
            }
        }
    }
    private static void AssertFormerGenderCannotUseContrastTuple(RankingPoolCapture first, RankingPoolCapture contrast)
    {
        int a = RawTargetIndex(first);
        if (a < 0 || contrast.Word.ContextualCandidates is null || first.Case.Gender == contrast.Case.Gender) return;
        var former = contrast.Word.ContextualCandidates.Single(c => c.AlternativeIndex == a);
        foreach (var m in former.Matches)
        {
            var adjective = contrast.Word.Alternatives[a].Readings[m.CandidateReadingIndex].Features;
            var noun = RankingRows(contrast.Analysis, m.FollowingNounReading.TokenIndex)
                [m.FollowingNounReading.CompleteReadingIndex].Features;
            Assert.IsFalse(adjective.Number == MorphologicalNumber.Singular &&
                adjective.Gender == first.Case.Gender && noun.Number == MorphologicalNumber.Singular &&
                noun.Gender == contrast.Case.Gender, "The former gender cannot borrow the new incompatible noun tuple.");
        }
        // Other legitimate readings may still support this same alternative; no blanket NotSupported gold.
    }
}
