using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

public sealed partial class PublicPackageTests
{
    private const string ConditionalLink = "conditional_auxiliary_participle";
    private const string ConditionalFiniteRule = "grammar.agreement.analytic_finite.v1";
    private const string ConditionalControllerRule = "grammar.agreement.active_participle_controller.v1";
    private const string ConditionalNumberRule = "grammar.agreement.auxiliary_active_participle.v1";

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Аз бих бил тук.", "бих", "бил", "")]
    [DataRow("Аз бих била тук.", "бих", "била", "")]
    [DataRow("Ти би бил тук.", "би", "бил", "")]
    [DataRow("Той би бил тук.", "би", "бил", "")]
    [DataRow("Тя би била тук.", "би", "била", "")]
    [DataRow("То би било тук.", "би", "било", "")]
    [DataRow("Ние бихме били тук.", "бихме", "били", "")]
    [DataRow("Вие бихте били тук.", "бихте", "били", "")]
    [DataRow("Те биха били тук.", "биха", "били", "")]
    [DataRow("Тя не би била тук.", "би", "била", "")]
    [DataRow("Тя би ли била тук?", "би", "била", "ли")]
    [DataRow("Тя би се върнала тук.", "би", "върнала", "се")]
    [DataRow("Той би го написал.", "би", "написал", "го")]
    [DataRow("Тя не би го написала.", "би", "написала", "го")]
    [DataRow("То не би ме хранило.", "би", "хранило", "ме")]
    [DataRow("Ти би ли искала да учиш?", "би", "искала", "ли")]
    [DataRow("Ние бихме написали писмото.", "бихме", "написали", "")]
    [DataRow("Учителят би написал писмото.", "би", "написал", "")]
    public void Conditional_groups_keep_auxiliary_participle_tuples_and_agreement(
        string text, string auxiliary, string participle, string bridge)
    {
        TextAnalysis actual = session.AnalyzeText(text, false, new() { IncludeGrammarEvidence = true }, default);
        AssertConditionalLink(actual, auxiliary, participle, bridge);
        Assert.IsFalse(actual.Diagnostics.Any(d => ConditionalAgreementWarning(d.Code)),
            string.Join(" | ", actual.Diagnostics.Select(d => d.Code + "@" + text.Substring(d.Start, d.Length))));
        Assert.IsTrue(actual.Grammar!.Matches.Any(m => m.RuleId == ConditionalNumberRule &&
            m.Assessment is TextGrammarAssessment.Compatible or TextGrammarAssessment.Ambiguous), text);
        AssertConditionalInventoryUnchanged(actual);
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Ние бих били тук.", "бих", "били", ConditionalNumberRule, "били", "")]
    [DataRow("Те биха бил тук.", "биха", "бил", ConditionalNumberRule, "бил", "")]
    [DataRow("Ние бихме бил тук.", "бихме", "бил", ConditionalNumberRule, "бил", "")]
    [DataRow("Аз би бил тук.", "би", "бил", ConditionalFiniteRule, "би", "")]
    [DataRow("Тя би бил тук.", "би", "бил", ConditionalControllerRule, "бил", "")]
    [DataRow("Тя не би бил тук.", "би", "бил", ConditionalControllerRule, "бил", "")]
    [DataRow("Тя би написал писмото.", "би", "написал", ConditionalControllerRule, "написал", "")]
    [DataRow("Тя не би го написал.", "би", "написал", ConditionalControllerRule, "написал", "го")]
    public void Conditional_mismatches_keep_the_bound_group_and_original_reading_authority(
        string text, string auxiliary, string participle, string rule, string target, string bridge)
    {
        TextAnalysis actual = session.AnalyzeText(text, false, new() { IncludeGrammarEvidence = true }, default);
        AssertConditionalLink(actual, auxiliary, participle, bridge);
        int targetIndex = actual.Tokens.Select((t, i) => (t, i)).Single(p => p.t.Text == target).i;
        Assert.IsTrue(actual.Grammar!.Matches.Any(m => m.RuleId == rule &&
            m.Assessment == TextGrammarAssessment.Mismatch && m.DependentTokenIndices.Contains(targetIndex)), text);
        Assert.IsTrue(actual.Diagnostics.Any(d => ConditionalAgreementWarning(d.Code) &&
            d.Start == actual.Tokens[targetIndex].Start && d.Length == target.Length &&
            d.Readings is { Count: > 0 } && d.ProjectedText is null), text);
        AssertConditionalInventoryUnchanged(actual);
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Може би бил там.")]
    [DataRow("Той би детето.")]
    [DataRow("Той би падналия човек.")]
    [DataRow("Да не би да закъснее?")]
    [DataRow("„Тя би бил тук.“")]
    public void Conditional_controls_do_not_promote_particles_lexical_verbs_or_quotes(string text)
    {
        TextAnalysis actual = session.AnalyzeText(text, false, new() { IncludeGrammarEvidence = true }, default);
        Assert.IsFalse(actual.PredicateLinks!.Sentences.SelectMany(s => s.Links).Any(l => l.Kind == ConditionalLink), text);
        Assert.IsFalse(actual.PredicateLinks.Sentences.SelectMany(s => s.Links).Any(l => l.Kind == "auxiliary_participle"), text);
        Assert.IsFalse(actual.Diagnostics.Any(d => d.RuleId is ConditionalNumberRule or ConditionalControllerRule), text);
        AssertConditionalInventoryUnchanged(actual);
    }

    [TestMethod, TestCategory("FullPackage")]
    public void Conditional_fronted_object_and_unspecified_first_person_gender_remain_legitimate()
    {
        foreach (string text in new[] { "Писмото би написал Иван.", "Аз бих бил тук.", "Аз бих била тук." })
        {
            TextAnalysis actual = session.AnalyzeText(text, false, new() { IncludeGrammarEvidence = true }, default);
            Assert.IsTrue(actual.PredicateLinks!.Sentences.SelectMany(s => s.Links).Any(l => l.Kind == ConditionalLink), text);
            Assert.IsFalse(actual.Diagnostics.Any(d => d.Code == "productive_predicative_agreement_mismatch"), text);
            AssertConditionalInventoryUnchanged(actual);
        }
    }

    [TestMethod, TestCategory("FullPackage")]
    public void Conditional_noun_homograph_keeps_the_feminine_participle_reading_ambiguous()
    {
        TextAnalysis actual = session.AnalyzeText("Той би била тук.", false, new() { IncludeGrammarEvidence = true }, default);
        AssertConditionalLink(actual, "би", "била", "");
        Assert.IsFalse(actual.Diagnostics.Any(d => ConditionalAgreementWarning(d.Code)));
        Assert.IsTrue(actual.Grammar!.Matches.Any(m => m.RuleId == ConditionalControllerRule &&
            m.Assessment == TextGrammarAssessment.Ambiguous));
        TextReadingOccurrence occurrence = actual.ReadingInventory!.Occurrences.Single(o => o.Text == "била");
        Assert.IsTrue(actual.ReadingInventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings.Any(row =>
            row.Raw.Lemma == "било" && row.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Number: MorphologicalNumber.Plural }));
        AssertConditionalInventoryUnchanged(actual);
    }

    [TestMethod, TestCategory("FullPackage")]
    public void Conditional_unresolved_subject_particle_keeps_the_gender_relation_ambiguous()
    {
        TextAnalysis actual = session.AnalyzeText("То не би ме хранила.", false, new() { IncludeGrammarEvidence = true }, default);
        AssertConditionalLink(actual, "би", "хранила", "ме");
        Assert.IsFalse(actual.Diagnostics.Any(d => ConditionalAgreementWarning(d.Code)));
        Assert.IsTrue(actual.Grammar!.Matches.Any(m => m.RuleId == ConditionalControllerRule &&
            m.Assessment == TextGrammarAssessment.Ambiguous));
        AssertConditionalInventoryUnchanged(actual);
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Всеки работник би ми казал веднага.", "би", "казал", "ми")]
    [DataRow("Не че би им се наложило.", "би", "наложило", "им|се")]
    [DataRow("Не бих могъл да напиша подобно писмо.", "бих", "могъл", "")]
    [DataRow("Бележка за поета би било последното му обяснение.", "би", "било", "")]
    [DataRow("Тези временни успехи биха били голяма грешка.", "биха", "били", "")]
    public void Conditional_development_patterns_keep_links_without_forcing_a_nominal_subject(
        string text, string auxiliary, string participle, string bridges)
    {
        // Invented equivalents of independently inspected development-corpus patterns.
        TextAnalysis actual = session.AnalyzeText(text, false, new() { IncludeGrammarEvidence = true }, default);
        AssertConditionalLink(actual, auxiliary, participle, bridges);
        Assert.IsFalse(actual.Diagnostics.Any(d => ConditionalAgreementWarning(d.Code)), text);
        AssertConditionalInventoryUnchanged(actual);
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Тя си се усмихвала.")]
    [DataRow("Той си се усмихвал.")]
    [DataRow("Те си се усмихвали.")]
    [DataRow("Тя си написала писмото.")]
    [DataRow("Той си написал писмото.")]
    [DataRow("Тя не си написала писмото.")]
    [DataRow("Учителката си написала писмото.")]
    [DataRow("Иван си написал писмото.")]
    [DataRow("Тя си пишела писмото.")]
    public void Si_clitic_with_compatible_third_person_bare_participle_keeps_ambiguity(string text)
    {
        TextAnalysis actual = session.AnalyzeText(text, false, new() { IncludeGrammarEvidence = true }, default);
        Assert.IsFalse(actual.Diagnostics.Any(d => ConditionalAgreementWarning(d.Code)),
            text + " | " + string.Join(" | ", actual.Diagnostics.Select(d => d.Code + "@" + text.Substring(d.Start, d.Length))));
        Assert.IsTrue(actual.Grammar!.Matches.Any(m => m.RuleId == ConditionalNumberRule &&
            m.Assessment == TextGrammarAssessment.Ambiguous), text);
        AssertSiDictionaryAlternatives(actual);
        AssertConditionalInventoryUnchanged(actual);
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Те си пили водата.")]
    [DataRow("Той си пили дървото.")]
    public void Si_participle_finite_homograph_keeps_each_compatible_original_analysis(string text)
    {
        TextAnalysis actual = session.AnalyzeText(text, false, new() { IncludeGrammarEvidence = true }, default);
        Assert.IsFalse(actual.Diagnostics.Any(d => ConditionalAgreementWarning(d.Code)), text);
        TextReadingOccurrence occurrence = actual.ReadingInventory!.Occurrences.Single(o => o.Text == "пили");
        var rows = actual.ReadingInventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings;
        Assert.IsTrue(rows.Any(r => r.Raw.Lemma == "пия" && r.Features is
            { VerbForm: MorphologicalVerbForm.Participle, Number: MorphologicalNumber.Plural }), text);
        Assert.IsTrue(rows.Any(r => r.Raw.Lemma == "пиля" && r.Features is
            { VerbForm: MorphologicalVerbForm.Finite, Person: MorphologicalPerson.Third, Number: MorphologicalNumber.Singular }), text);
        Assert.IsTrue(rows.Any(r => r.Features.VerbForm == MorphologicalVerbForm.Imperative), text);
        Assert.IsTrue(rows.Any(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun), text);
        AssertSiDictionaryAlternatives(actual);
        AssertConditionalInventoryUnchanged(actual);
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Ти си написал писмото.")]
    [DataRow("Ти си написала писмото.")]
    [DataRow("Ти си се усмихвала.")]
    [DataRow("Тя беше написала писмото.")]
    public void Si_and_past_auxiliary_readings_still_support_compatible_personal_groups(string text)
    {
        TextAnalysis actual = session.AnalyzeText(text, false, new() { IncludeGrammarEvidence = true }, default);
        Assert.IsFalse(actual.Diagnostics.Any(d => ConditionalAgreementWarning(d.Code)), text);
        Assert.IsTrue(actual.Grammar!.Matches.Any(m => m.RuleId == ConditionalFiniteRule &&
            m.Assessment is TextGrammarAssessment.Compatible or TextGrammarAssessment.Ambiguous), text);
        if (text.Contains(" си ", StringComparison.Ordinal)) AssertSiDictionaryAlternatives(actual);
        AssertConditionalInventoryUnchanged(actual);
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Аз си написал писмото.", "си")]
    [DataRow("Ние си пили водата.", "пили")]
    [DataRow("Аз беше написал писмото.", "беше")]
    [DataRow("Тя сте написала писмото.", "сте")]
    public void Si_and_past_auxiliary_controls_preserve_existing_person_conflicts(string text, string target)
    {
        TextAnalysis actual = session.AnalyzeText(text, false, new() { IncludeGrammarEvidence = true }, default);
        TokenAnalysis token = actual.Tokens.Single(t => t.Text == target);
        Assert.IsTrue(actual.Diagnostics.Any(d => ConditionalAgreementWarning(d.Code) &&
            d.Start == token.Start && d.Length == token.Length && d.Readings is { Count: > 0 } && d.ProjectedText is null), text);
        AssertConditionalInventoryUnchanged(actual);
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Тя си написал писмото.", "написал", MorphologicalGender.Feminine)]
    [DataRow("Той си написала писмото.", "написала", MorphologicalGender.Masculine)]
    [DataRow("Тя си се усмихвал.", "усмихвал", MorphologicalGender.Feminine)]
    public void Si_incompatible_participle_tuples_do_not_license_clitic_suppression(
        string text, string participle, MorphologicalGender subjectGender)
    {
        TextAnalysis actual = session.AnalyzeText(text, false, new() { IncludeGrammarEvidence = true }, default);
        TextReadingOccurrence occurrence = actual.ReadingInventory!.Occurrences.Single(o => o.Text == participle);
        var parts = actual.ReadingInventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings.Where(r =>
            r.Features.VerbForm == MorphologicalVerbForm.Participle && r.Features.ParticipleKind is
                MorphologicalParticipleKind.PastAoristActive or MorphologicalParticipleKind.PastImperfectActive).ToArray();
        Assert.IsNotEmpty(parts, text);
        Assert.IsTrue(parts.All(r => r.Features.Number == MorphologicalNumber.Singular && r.Features.Gender != subjectGender), text);
        Assert.IsTrue(actual.Diagnostics.Any(d => ConditionalAgreementWarning(d.Code) &&
            d.Readings is { Count: > 0 } && d.ProjectedText is null), text);
        Assert.IsTrue(actual.Grammar!.Matches.Any(m => m.RuleId == ConditionalControllerRule &&
            m.Assessment == TextGrammarAssessment.Ambiguous), text);
        AssertConditionalInventoryUnchanged(actual);
    }

    private static void AssertSiDictionaryAlternatives(TextAnalysis actual)
    {
        TextReadingOccurrence occurrence = actual.ReadingInventory!.Occurrences.Single(o => o.Text == "си");
        var rows = actual.ReadingInventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings;
        Assert.IsTrue(rows.Any(r => r.Raw.Lemma == "си" && r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Particle), actual.Text);
        Assert.IsTrue(rows.Any(r => r.Raw.Lemma == "свой" && r.Features is
            { PartOfSpeech: MorphologicalPartOfSpeech.Pronoun, IsShortForm: true }), actual.Text);
        Assert.IsTrue(rows.Any(r => r.Raw.Lemma == "съм" && r.Features is
            { VerbForm: MorphologicalVerbForm.Finite, Person: MorphologicalPerson.Second, Number: MorphologicalNumber.Singular }), actual.Text);
    }

    private static void AssertConditionalLink(TextAnalysis actual, string auxiliary, string participle, string bridge)
    {
        int auxIndex = actual.Tokens.Select((t, i) => (t, i)).Single(p => p.t.Text == auxiliary).i;
        int partIndex = actual.Tokens.Select((t, i) => (t, i)).Single(p => p.t.Text == participle).i;
        var links = actual.PredicateLinks!.Sentences.SelectMany(s => s.Links).Where(l => l.Kind == ConditionalLink &&
            l.Left.TokenIndex == auxIndex && l.Right.TokenIndex == partIndex).ToArray();
        Assert.IsNotEmpty(links, actual.Text);
        foreach (TextPredicateLink link in links)
        {
            Assert.IsNotEmpty(link.Left.Readings, actual.Text);
            Assert.IsNotEmpty(link.Right.Readings, actual.Text);
            foreach (TextReadingReference reference in link.Left.Readings)
            {
                var row = ConditionalReading(actual, reference);
                Assert.IsTrue(row.Raw.Lemma is "съм" or "бъда", actual.Text);
                Assert.AreEqual(MorphologicalVerbForm.Finite, row.Features.VerbForm, actual.Text);
            }
            foreach (TextReadingReference reference in link.Right.Readings)
            {
                var features = ConditionalReading(actual, reference).Features;
                Assert.AreEqual(MorphologicalVerbForm.Participle, features.VerbForm, actual.Text);
                Assert.IsTrue(features.ParticipleKind is MorphologicalParticipleKind.PastAoristActive or
                    MorphologicalParticipleKind.PastImperfectActive, actual.Text);
                Assert.IsTrue(features.Article is null or MorphologicalArticle.Indefinite, actual.Text);
            }
            foreach (string marker in bridge.Split('|', StringSplitOptions.RemoveEmptyEntries))
                Assert.IsTrue(link.Markers.Any(m => actual.Tokens[m.TokenIndex].Text == marker && m.Readings.Count > 0), actual.Text);
        }
    }

    private static void AssertConditionalInventoryUnchanged(TextAnalysis actual)
    {
        Assert.IsNotNull(actual.ReadingInventory);
        foreach (TextReadingOccurrence occurrence in actual.ReadingInventory.Occurrences)
        {
            var rows = actual.ReadingInventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings;
            CollectionAssert.AreEqual(session.Lookup(occurrence.Text, LookupMode.Exact, 256).ToArray(),
                rows.Select(r => r.Raw).ToArray(), occurrence.Text);
            Assert.AreEqual(occurrence.Text, actual.Text.Substring(occurrence.Start, occurrence.Length));
        }
        foreach (TextPredicateLink link in actual.PredicateLinks!.Sentences.SelectMany(s => s.Links).Where(l => l.Kind == ConditionalLink))
            foreach (TextReadingReference reference in link.Left.Readings.Concat(link.Right.Readings).Concat(link.Markers.SelectMany(m => m.Readings)))
            {
                TextReadingOccurrence occurrence = actual.ReadingInventory.Occurrences.Single(o => o.TokenIndex == reference.TokenIndex);
                Assert.IsTrue(reference.CompleteReadingIndex >= 0 && reference.CompleteReadingIndex <
                    actual.ReadingInventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings.Count, actual.Text);
            }
    }

    private static bool ConditionalAgreementWarning(string code) => code is
        "productive_morphology_agreement_mismatch" or "pronoun_verb_agreement_mismatch" or
        "productive_predicative_agreement_mismatch" or "auxiliary_participle_agreement_mismatch";

    private static TextReadingSnapshot ConditionalReading(TextAnalysis actual, TextReadingReference reference)
    {
        TextReadingOccurrence occurrence = actual.ReadingInventory!.Occurrences.Single(o => o.TokenIndex == reference.TokenIndex);
        return actual.ReadingInventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings[reference.CompleteReadingIndex];
    }
}
