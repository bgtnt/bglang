using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

public sealed partial class PublicPackageTests
{
    [TestMethod]
    [DataRow("Върнах се понеже забравих ключа.", "понеже", 10, "Върнах|понеже|забравих", DisplayName = "ConjCommaPackage.report.ponezhe")]
    [DataRow("Чаках докато спре дъждът.", "докато", 6, "Чаках|докато|спре", DisplayName = "ConjCommaPackage.report.dokato")]
    [DataRow("Оставам понеже вали.", "понеже", 8, "Оставам|понеже|вали", DisplayName = "ConjCommaPackage.revised.ponezhe-old-negative")]
    [DataRow("😀 Върнах се понеже забравих ключа.", "понеже", 13, "Върнах|понеже|забравих", DisplayName = "ConjCommaPackage.utf16.ponezhe")]
    [DataRow("😀 Чаках докато спре дъждът.", "докато", 9, "Чаках|докато|спре", DisplayName = "ConjCommaPackage.utf16.dokato")]
    [DataRow("„Върнах се понеже забравих ключа.“", "понеже", 11, "Върнах|понеже|забравих", DisplayName = "ConjCommaPackage.quoted-missing.ponezhe")]
    [DataRow("„Чаках докато спре дъждът.“", "докато", 7, "Чаках|докато|спре", DisplayName = "ConjCommaPackage.quoted-missing.dokato")]
    public void Report_conjunctions_return_optional_candidates_with_every_original_witness_row(
        string text, string marker, int start, string witnessWords)
    {
        RequireConjunctionReportBaseline();
        TextAnalysis ordinary = session.AnalyzeText(text, false, default, default);
        TextAnalysis result = session.AnalyzeText(text, false, new() { IncludeSubordinateCommaDiagnostics = true }, default);
        Assert.AreEqual(text, result.Text);
        Assert.IsNull(ordinary.Validation!.SubordinateCommaDiagnostics);
        Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(ordinary.ReadingInventory),
            System.Text.Json.JsonSerializer.Serialize(result.ReadingInventory));
        TextValidationDiagnostic actual = result.Validation!.SubordinateCommaDiagnostics!.Single();
        Assert.AreEqual((start, marker.Length, marker), (actual.Start, actual.Length, text.Substring(actual.Start, actual.Length)));
        Assert.AreEqual("missing_comma_before_subordinate_conjunction", actual.Code);
        Assert.AreEqual("reviewed_literal_subordinate_marker;possible_finite_verb_topology;complete_context_rows_retained;source_separator_absent;candidate_only;not_grammar_verdict", actual.Basis);
        List<TextReadingReference> expected = [];
        foreach (string word in witnessWords.Split('|'))
        {
            TextReadingOccurrence occurrence = result.ReadingInventory!.Occurrences.Single(item =>
                string.Equals(item.Text, word, StringComparison.Ordinal));
            TextReadingSet set = result.ReadingInventory.Sets[occurrence.ReadingSetIndex];
            IReadOnlyList<TextReadingSnapshot> rows = set.CanonicalReadings;
            Assert.AreSame(set.CanonicalReadings, set.ResolvedReadings, word);
            Assert.IsNotEmpty(rows, word);
            for (int row = 0; row < rows.Count; row++)
            {
                Assert.AreEqual(rows[row].Raw.Features, rows[row].Features, word);
                Assert.IsFalse(rows[row].Features.HasConflictingFeatures || rows[row].Features.HasUnrecognizedFeatures, word);
                Assert.AreEqual(word, rows[row].Raw.Form, true, word);
                if (word == marker)
                    Assert.IsTrue(rows[row].Features.PartOfSpeech is MorphologicalPartOfSpeech.Conjunction or MorphologicalPartOfSpeech.Adverb, word);
                else
                {
                    Assert.AreEqual(MorphologicalPartOfSpeech.Verb, rows[row].Features.PartOfSpeech, word);
                    Assert.IsTrue(rows[row].Features.VerbForm is MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative, word);
                }
                expected.Add(new(occurrence.TokenIndex, row));
            }
            if (word != marker)
                Assert.IsTrue(rows.Any(row => row.Features.VerbForm == MorphologicalVerbForm.Finite &&
                    row.Features.Person is not null && row.Features.Number is not null && row.Features.Tense is not null), word);
        }
        CollectionAssert.AreEqual(expected.ToArray(), actual.Supports.ToArray());
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.AreEqual(cancellation.Token, Assert.ThrowsExactly<OperationCanceledException>(() =>
            session.AnalyzeText(text, false, new() { IncludeSubordinateCommaDiagnostics = true }, cancellation.Token)).CancellationToken);
    }

    [TestMethod]
    [DataRow("Върнах се, понеже забравих ключа.", DisplayName = "ConjCommaPackage.comma.ponezhe")]
    [DataRow("Чаках, докато спре дъждът.", DisplayName = "ConjCommaPackage.comma.dokato")]
    [DataRow("Понеже забравих ключа, върнах се.", DisplayName = "ConjCommaPackage.initial.ponezhe")]
    [DataRow("Докато валеше, чаках.", DisplayName = "ConjCommaPackage.initial.dokato")]
    [DataRow("„Върнах се, понеже забравих ключа.“", DisplayName = "ConjCommaPackage.quoted-clause.ponezhe")]
    [DataRow("„Чаках, докато спре дъждът.“", DisplayName = "ConjCommaPackage.quoted-clause.dokato")]
    [DataRow("Написах „понеже“ и прочетох текста.", DisplayName = "ConjCommaPackage.quoted-term.ponezhe")]
    [DataRow("Написах „докато“ и прочетох текста.", DisplayName = "ConjCommaPackage.quoted-term.dokato")]
    [DataRow("Върнах се само понеже забравих ключа.", DisplayName = "ConjCommaPackage.rule90.ponezhe-samo")]
    [DataRow("Върнах се не понеже забравих ключа, а понеже заваля.", DisplayName = "ConjCommaPackage.rule90.ponezhe-ne")]
    [DataRow("Чаках само докато спре дъждът.", DisplayName = "ConjCommaPackage.rule90.dokato-samo")]
    [DataRow("Чаках не докато вали, а докато спре.", DisplayName = "ConjCommaPackage.rule90.dokato-ne")]
    [DataRow("Оставам и понеже вали.", DisplayName = "ConjCommaPackage.adjacent.ponezhe-i")]
    [DataRow("Оставам или понеже вали, или понеже чакам.", DisplayName = "ConjCommaPackage.adjacent.ponezhe-ili")]
    [DataRow("Чаках и докато валеше, четях.", DisplayName = "ConjCommaPackage.adjacent.dokato-i")]
    [DataRow("Чаках или докато валеше, или докато четях.", DisplayName = "ConjCommaPackage.adjacent.dokato-ili")]
    [DataRow("Звънях, но понеже валеше, си тръгнах.", DisplayName = "ConjCommaPackage.rule93.ponezhe-no")]
    [DataRow("Разбрах, че понеже забравих ключа, се върнах.", DisplayName = "ConjCommaPackage.rule93.ponezhe-che")]
    [DataRow("Чаках, а докато валеше, четях.", DisplayName = "ConjCommaPackage.rule93.dokato-a")]
    [DataRow("Разбрах, че докато валеше, чаках.", DisplayName = "ConjCommaPackage.rule93.dokato-che")]
    [DataRow("Върнах се, понеже, ако завали, ще се намокря.", DisplayName = "ConjCommaPackage.rule93.multisyllabic-first")]
    [DataRow("Знам защо закъсня.", DisplayName = "ConjCommaPackage.rule89.zashto")]
    [DataRow("Не знам дали ще дойде.", DisplayName = "ConjCommaPackage.rule89.dali")]
    [DataRow("Попитах кога тръгва влакът.", DisplayName = "ConjCommaPackage.rule89.koga")]
    public void Normative_controls_preserve_separators_initials_qualifiers_adjacent_conjunctions_and_quotes(string text)
    {
        RequireConjunctionReportBaseline();
        TextAnalysis result = session.AnalyzeText(text, false, new() { IncludeSubordinateCommaDiagnostics = true }, default);
        Assert.AreEqual(text, result.Text);
        Assert.IsEmpty(result.Validation!.SubordinateCommaDiagnostics!, text);
    }

    [TestMethod]
    [DataRow("Чета докато чакам.", "Чета", DisplayName = "ConjCommaPackage.abstain.cherta-noun-verb")]
    public void Genuine_noun_verb_ambiguity_preserves_abstention_without_calling_the_punctuation_correct(string text, string ambiguous)
    {
        RequireConjunctionReportBaseline();
        TextAnalysis result = session.AnalyzeText(text, false, new() { IncludeSubordinateCommaDiagnostics = true }, default);
        TextReadingOccurrence occurrence = result.ReadingInventory!.Occurrences.Single(item => item.Text == ambiguous);
        IReadOnlyList<TextReadingSnapshot> rows = result.ReadingInventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings;
        Assert.IsTrue(rows.Any(row => row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun));
        Assert.IsTrue(rows.Any(row => row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Verb));
        Assert.IsEmpty(result.Validation!.SubordinateCommaDiagnostics!);
        CollectionAssert.Contains(result.Validation.UnavailableChecks.ToArray(), "subordinate_comma:reviewed_verb_authority_unavailable");
        Assert.AreEqual(text, result.Text);
    }

    [TestMethod]
    [DataRow("В случай, че утре вали, ще остана.", "В случай, че", "вали")]
    [DataRow("Влизам, при условие, че той дойде.", "при условие, че", "дойде")]
    [DataRow("😀 Тръгвам — въпреки, че не вали — с теб.", "въпреки, че", "вали")]
    [DataRow("Макар, че чета, чувам всичко.", "Макар, че", "чета")]
    [DataRow("Независимо, че те чакат, влез.", "Независимо, че", "чакат")]
    [DataRow("И в случай, че спре, остани.", "в случай, че", "спре")]
    [DataRow("Тръгвам, но в случай, че спре, оставам.", "в случай, че", "спре")]
    [DataRow("Тръгвам, а при условие, че вали, оставам.", "при условие, че", "вали")]
    public void Compound_conjunction_reports_only_the_internal_comma_with_its_reading_witnesses(
        string text, string conjunction, string verb)
    {
        RequireConjunctionReportBaseline();
        const string code = "spurious_comma_inside_compound_conjunction";
        int start = text.IndexOf(conjunction, StringComparison.Ordinal);
        int comma = start + conjunction.IndexOf(',');
        TextContextOptions options = new() { IncludeSubordinateCommaDiagnostics = true };
        TextAnalysis result = session.AnalyzeText(text, false, options, default);
        var advice = result.Validation!.SubordinateCommaDiagnostics!.Single(d => d.Code == code);
        Assert.AreEqual((comma, 1, ","), (advice.Start, advice.Length, text.Substring(advice.Start, advice.Length)));
        StringAssert.Contains(advice.Basis, "no_automatic_rewrite");
        int verbStart = text.IndexOf(verb, start + conjunction.Length, StringComparison.Ordinal);
        var inventory = result.ReadingInventory!;
        var expected = inventory.Occurrences.Where(w => w.Start >= start && w.Start < start + conjunction.Length || w.Start == verbStart)
            .SelectMany(w => Enumerable.Range(0, inventory.Sets[w.ReadingSetIndex].ResolvedReadings.Count)
                .Select(i => new TextReadingReference(w.TokenIndex, i))).ToArray();
        CollectionAssert.AreEqual(expected, advice.Supports.ToArray());
        Assert.IsNull(session.AnalyzeText(text).Validation!.SubordinateCommaDiagnostics);
        var corrected = session.AnalyzeText(text.Remove(comma, 1), false, options, default);
        Assert.IsFalse(corrected.Validation!.SubordinateCommaDiagnostics!.Any(d => d.Code == code ||
            d.Code == "missing_comma_before_subordinate_conjunction" && d.Start >= start && d.Start < start + conjunction.Length - 1));
    }

    [TestMethod]
    [DataRow("Тръгнаха рано в случай, че завали.", "в случай, че", "Тръгнаха|рано|в|случай|че|завали")]
    [DataRow("Ще дойда при условие, че ме поканят.", "при условие, че", "дойда|при|условие|че|поканят")]
    [DataRow("😀 Тръгнаха рано в случай, че завали.", "в случай, че", "Тръгнаха|рано|в|случай|че|завали")]
    public void Embedded_compound_conjunction_reports_both_commas_and_requires_both_corrections(
        string text, string conjunction, string witnessWords)
    {
        RequireConjunctionReportBaseline();
        const string internalCode = "spurious_comma_inside_compound_conjunction";
        const string missingCode = "missing_comma_before_subordinate_conjunction";
        int start = text.IndexOf(conjunction, StringComparison.Ordinal);
        int comma = start + conjunction.IndexOf(',');
        TextContextOptions options = new() { IncludeSubordinateCommaDiagnostics = true };
        TextAnalysis ordinary = session.AnalyzeText(text);
        TextAnalysis result = session.AnalyzeText(text, false, options, default);
        var diagnostics = result.Validation!.SubordinateCommaDiagnostics!;
        Assert.HasCount(2, diagnostics);
        var internalComma = diagnostics.Single(row => row.Code == internalCode);
        var missing = diagnostics.Single(row => row.Code == missingCode);
        Assert.AreEqual((comma, 1, ","), (internalComma.Start, internalComma.Length, text.Substring(internalComma.Start, internalComma.Length)));
        Assert.AreEqual((start, conjunction.Length, conjunction), (missing.Start, missing.Length, text.Substring(missing.Start, missing.Length)));
        StringAssert.Contains(internalComma.Basis, "no_automatic_rewrite");
        StringAssert.Contains(missing.Basis, "candidate_only;not_grammar_verdict");
        var inventory = result.ReadingInventory!;
        var expected = witnessWords.Split('|').SelectMany(word =>
        {
            var occurrence = inventory.Occurrences.Single(item => item.Text == word);
            var set = inventory.Sets[occurrence.ReadingSetIndex];
            Assert.AreSame(set.CanonicalReadings, set.ResolvedReadings, word);
            Assert.IsNotEmpty(set.ResolvedReadings, word);
            return Enumerable.Range(0, set.ResolvedReadings.Count).Select(row => new TextReadingReference(occurrence.TokenIndex, row));
        }).ToArray();
        CollectionAssert.AreEqual(expected, internalComma.Supports.ToArray());
        CollectionAssert.AreEqual(expected, missing.Supports.ToArray());
        Assert.AreEqual(text, result.Text);
        Assert.IsNull(ordinary.Validation!.SubordinateCommaDiagnostics);
        Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(ordinary.ReadingInventory),
            System.Text.Json.JsonSerializer.Serialize(result.ReadingInventory));

        string onlyInternalFixed = text.Remove(comma, 1);
        var missingOnly = session.AnalyzeText(onlyInternalFixed, false, options, default).Validation!.SubordinateCommaDiagnostics!.Single();
        Assert.AreEqual((missingCode, start, conjunction.Length - 1), (missingOnly.Code, missingOnly.Start, missingOnly.Length));
        string onlyBoundaryFixed = text.Insert(start - 1, ",");
        var internalOnly = session.AnalyzeText(onlyBoundaryFixed, false, options, default).Validation!.SubordinateCommaDiagnostics!.Single();
        Assert.AreEqual((internalCode, comma + 1, 1), (internalOnly.Code, internalOnly.Start, internalOnly.Length));
        string corrected = onlyInternalFixed.Insert(start - 1, ",");
        Assert.IsEmpty(session.AnalyzeText(corrected, false, options, default).Validation!.SubordinateCommaDiagnostics!, corrected);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.AreEqual(cancellation.Token, Assert.ThrowsExactly<OperationCanceledException>(() =>
            session.AnalyzeText(text, false, options, cancellation.Token)).CancellationToken);
    }

    [TestMethod]
    [DataRow("в", "случай", "само")]
    [DataRow("при", "условие", "само")]
    [DataRow("в", "случай", "дори")]
    [DataRow("при", "условие", "дори")]
    [DataRow("в", "случай", "тъкмо")]
    [DataRow("при", "условие", "тъкмо")]
    [DataRow("в", "случай", "единствено")]
    [DataRow("при", "условие", "единствено")]
    [DataRow("в", "случай", "не")]
    [DataRow("при", "условие", "не")]
    [DataRow("в", "случай", "даже")]
    [DataRow("при", "условие", "даже")]
    [DataRow("в", "случай", "едва")]
    [DataRow("при", "условие", "едва")]
    [DataRow("в", "случай", "особено")]
    [DataRow("при", "условие", "особено")]
    [DataRow("в", "случай", "малко")]
    [DataRow("при", "условие", "малко")]
    [DataRow("в", "случай", "много")]
    [DataRow("при", "условие", "много")]
    [DataRow("в", "случай", "дълго")]
    [DataRow("при", "условие", "дълго")]
    [DataRow("в", "случай", "именно")]
    [DataRow("при", "условие", "именно")]
    [DataRow("в", "случай", "поне")]
    [DataRow("при", "условие", "поне")]
    [DataRow("в", "случай", "чак")]
    [DataRow("при", "условие", "чак")]
    [DataRow("в", "случай", "твърде дълго")]
    [DataRow("при", "условие", "твърде дълго")]
    [DataRow("в", "случай", "твърде скоро")]
    [DataRow("при", "условие", "твърде скоро")]
    public void Conditional_compound_qualifiers_preserve_internal_comma_advice_without_requesting_a_boundary_comma(
        string preposition, string noun, string qualifier)
    {
        RequireConjunctionReportBaseline();
        string text = $"Ще дойда {qualifier} {preposition} {noun}, че ме поканят.";
        int comma = text.IndexOf(',');
        TextContextOptions options = new() { IncludeSubordinateCommaDiagnostics = true };
        TextAnalysis result = session.AnalyzeText(text, false, options, default);
        TextValidationDiagnostic actual = result.Validation!.SubordinateCommaDiagnostics!.Single();
        Assert.AreEqual(("spurious_comma_inside_compound_conjunction", comma, 1), (actual.Code, actual.Start, actual.Length));
        StringAssert.Contains(actual.Basis, "candidate_only;not_grammar_verdict;no_automatic_rewrite");
        foreach (string word in qualifier.Split(' '))
        {
            var occurrence = result.ReadingInventory!.Occurrences.Single(row => row.Text == word);
            var readings = result.ReadingInventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings;
            Assert.IsNotEmpty(readings, word);
            for (int row = 0; row < readings.Count; row++)
                CollectionAssert.Contains(actual.Supports.ToArray(), new TextReadingReference(occurrence.TokenIndex, row));
        }
        Assert.AreEqual(text, result.Text);
        string corrected = text.Remove(comma, 1);
        Assert.IsEmpty(session.AnalyzeText(corrected, false, options, default).Validation!.SubordinateCommaDiagnostics!, corrected);
    }

    [TestMethod]
    [DataRow("Ще дойда и само при условие, че ме поканят.")]
    [DataRow("Ще дойда или само при условие, че ме поканят.")]
    [DataRow("Ще дойда, но само в случай, че ме поканят.")]
    [DataRow("Ще дойда, а само в случай, че ме поканят.")]
    [DataRow("Само при условие, че ме поканят, ще дойда.")]
    [DataRow("Само в случай, че ме поканят, ще дойда.")]
    public void Qualified_compound_boundaries_preserve_initial_and_narrow_connector_controls(string text)
    {
        RequireConjunctionReportBaseline();
        TextContextOptions options = new() { IncludeSubordinateCommaDiagnostics = true };
        TextValidationDiagnostic actual = session.AnalyzeText(text, false, options, default).Validation!.SubordinateCommaDiagnostics!.Single();
        int comma = text.IndexOf(", че", StringComparison.Ordinal);
        Assert.AreEqual(("spurious_comma_inside_compound_conjunction", comma, 1), (actual.Code, actual.Start, actual.Length));
        Assert.IsEmpty(session.AnalyzeText(text.Remove(comma, 1), false, options, default).Validation!.SubordinateCommaDiagnostics!);
    }

    [TestMethod]
    [DataRow("Разчитам само на условието, че той ще дойде.")]
    [DataRow("Съгласих се само при едно условие, че ще ми пишат.")]
    [DataRow("Ще дойда само при едно изрично условие, че ще ме поканят.")]
    [DataRow("Той написа „само при условие, че ме поканят“ на листа.")]
    public void Qualified_literal_nouns_and_quoted_terms_do_not_become_conditional_compound_findings(string text)
    {
        RequireConjunctionReportBaseline();
        Assert.IsEmpty(session.AnalyzeText(text, false, new() { IncludeSubordinateCommaDiagnostics = true }, default)
            .Validation!.SubordinateCommaDiagnostics!, text);
    }

    [TestMethod]
    [DataRow("Той каза, че вали.")]
    [DataRow("Подреди ги така, че да се виждат.")]
    [DataRow("Ще дойда, само, че по-късно.")]
    [DataRow("Приех при едно изрично условие, че ще ми пишат.")]
    [DataRow("Разчитам на условието, че той ще дойде.")]
    [DataRow("Съгласих се при едно условие, че ще ми пишат.")]
    [DataRow("Тя действа толкова независимо, че всички се учудиха.")]
    [DataRow("Записах „макар, че навън вали“ на листа.")]
    [DataRow("Той написа `в случай, че вали` и излезе.")]
    [DataRow("Той написа „въпреки, че вали.")]
    [DataRow("В случай, че … после разказах всичко.")]
    [DataRow("Макар, че утре; другите чакат.")]
    [DataRow("В случай, че „той чете“.")]
    [DataRow("В случай,, че вали, ела.")]
    [DataRow("В (случай), че вали, ела.")]
    public void Compound_conjunction_abstains_for_ordinary_commas_quotes_and_missing_local_evidence(string text)
    {
        RequireConjunctionReportBaseline();
        var result = session.AnalyzeText(text, false, new() { IncludeSubordinateCommaDiagnostics = true }, default);
        Assert.IsFalse(result.Validation!.SubordinateCommaDiagnostics!.Any(d => d.Code == "spurious_comma_inside_compound_conjunction"));
    }

    private static void RequireConjunctionReportBaseline()
    {
        Assert.AreEqual("bglang-neutral:36b0b69685d6d3e9", candidate.GetProperty("packageId").GetString());
        Assert.AreEqual("bglang-neutral:36b0b69685d6d3e9", session.Provenance.PackageId);
        Assert.AreEqual("451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7",
            session.Provenance.ManifestSha256.ToLowerInvariant());
        string? selected = Environment.GetEnvironmentVariable("BGLANG_PUBLIC_PACKAGE_MANIFEST");
        Assert.IsFalse(string.IsNullOrWhiteSpace(selected), "The strictly bound complete Baseline package is required; this gate never skips.");
        Assert.AreEqual(Path.GetFullPath(selected!), manifest, true);
    }
}
