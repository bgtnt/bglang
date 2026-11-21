using BgLang.Api;
using BgLang.Contracts;
using BgLang.Tests.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

// Devised short counterparts of the observed UD-dev future/reflexive construction classes.
// Corpus text and source identities remain in the offline evidence receipt.
[TestClass, DoNotParallelize]
public sealed class FutureReflexivePackageTests
{
    private const string Agreement = "productive_morphology_agreement_mismatch";
    private const string Lane = "clause_fusion_agreement.v1";
    private static BulgarianLanguageSession session = null!;
    [ClassInitialize] public static void Open(TestContext _) => session = FinitePredicateAcceptance.Open();
    [ClassCleanup] public static void Close() => session.Dispose();

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Аз ще се върна на работа.", "върна")]
    [DataRow("Вие ще се върнете вкъщи.", "върнете")]
    [DataRow("Образците ще се изпращат утре.", "изпращат")]
    [DataRow("Той ще се учи във Франция.", "учи")]
    [DataRow("И то ще се превърне.", "превърне")]
    [DataRow("Той ще ми се обади.", "обади")]
    public void Future_and_reflexive_groups_cite_the_same_original_personal_carrier(string text, string carrierText)
    {
        TextAnalysis actual = Analyze(text);
        FinitePredicateAcceptance.ConserveInventory(actual);
        int future = Token(actual, "ще"), reflexive = Token(actual, "се"), carrier = Token(actual, carrierText);
        TextPredicateLink[] links = Links(actual);
        TextPredicateLink[] futureLinks = links.Where(l => l.Kind == "future_marker" && l.Left.TokenIndex == future && l.Right.TokenIndex == carrier).ToArray();
        Assert.IsNotEmpty(futureLinks, text);
        foreach (TextPredicateLink link in futureLinks)
        {
            Assert.IsTrue(Rows(actual, link.Right).All(r => r.Features is
                { VerbForm: MorphologicalVerbForm.Finite, Tense: MorphologicalTense.Present, Person: not null, Number: not null }), text);
            Assert.IsTrue(link.Markers.Any(m => m.TokenIndex == reflexive), text);
            Assert.IsTrue(link.Markers.All(m => m.Readings.Count + (m.GraphReadings?.Count ?? 0) == 1), text);
        }
        Assert.IsTrue(links.Any(l => l.Kind == "reflexive_clitic" && l.Left.TokenIndex == reflexive && l.Right.TokenIndex == carrier), text);
        Assert.IsFalse(links.Any(l => l.Kind == "reflexive_clitic" && l.Left.TokenIndex == future), text);
        Assert.IsEmpty(AgreementWarnings(actual), text);
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Ти ще носи книгата.", "носи", true)]
    [DataRow("Ти ще носиш книгата.", "носиш", false)]
    [DataRow("Ти ще се моли.", "моли", true)]
    [DataRow("Ти ще се молиш.", "молиш", false)]
    [DataRow("Ти ще се върнем.", "върнем", true)]
    [DataRow("Ти ще се върнеш.", "върнеш", false)]
    [DataRow("Вие ще се върна.", "върна", true)]
    [DataRow("Вие ще се върнете.", "върнете", false)]
    [DataRow("Аз ще се върнеш.", "върнеш", true)]
    [DataRow("Аз ще се върна.", "върна", false)]
    [DataRow("Ние ще се върне.", "върне", true)]
    [DataRow("Ние ще се върнем.", "върнем", false)]
    [DataRow("Те ще се върна.", "върна", true)]
    [DataRow("Те ще се върнат.", "върнат", false)]
    [DataRow("То ще се превърнат.", "превърнат", true)]
    [DataRow("То ще се превърне.", "превърне", false)]
    [DataRow("То се връщаме.", "връщаме", true)]
    [DataRow("То се връща.", "връща", false)]
    public void Future_agreement_uses_the_shared_rule_and_preserves_imperative_alternatives(string text, string carrierText, bool mismatch)
    {
        TextAnalysis actual = Analyze(text);
        FinitePredicateAcceptance.ConserveInventory(actual);
        TextDiagnostic[] warnings = AgreementWarnings(actual);
        Assert.HasCount(mismatch ? 1 : 0, warnings, text);
        if (!mismatch) return;
        int carrier = Token(actual, carrierText);
        TextDiagnostic warning = warnings.Single();
        Assert.AreEqual(Lane, warning.RuleId, text);
        Assert.AreEqual(actual.Tokens[carrier].Start, warning.Start, text);
        Assert.AreEqual(carrierText.Length, warning.Length, text);
        Assert.IsNull(warning.ProjectedText, text);
        Assert.IsTrue(warning.Readings!.Any(r => r.TokenIndex == carrier), text);
        Assert.IsTrue(actual.Context!.Sentences.SelectMany(s => s.Candidates).Any(c =>
            c.AuthorityLane == Lane && c.PredicateTokenIndex == carrier && !c.Selectable), text);
        Assert.HasCount(1, actual.Grammar!.Matches.Where(m => m.RuleId == Lane &&
            m.Assessment == TextGrammarAssessment.Mismatch && m.DependentTokenIndices.Contains(carrier)).ToArray(), text);
        if (carrierText is "носи" or "моли")
            Assert.IsTrue(CompleteRows(actual, carrier).Any(r => r.Features.VerbForm == MorphologicalVerbForm.Imperative), text);
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Ти носи книгата!")]
    [DataRow("Ти се моли!")]
    [DataRow("Върнете се!")]
    [DataRow("И то ще се върна.")]
    [DataRow("Тя каза, че ти ще се върнеш.")]
    [DataRow("Вие ще се върнете, а аз ще се върна.")]
    [DataRow("„Ти ще се моли.“")]
    public void Imperative_discourse_quote_and_separate_clause_controls_remain_conservative(string text)
    {
        TextAnalysis actual = Analyze(text);
        FinitePredicateAcceptance.ConserveInventory(actual);
        Assert.IsEmpty(AgreementWarnings(actual), text);
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Старае се да работи.", "старае", "се")]
    [DataRow("Той се беше върнал.", "се", "върнал")]
    [DataRow("Той беше се върнал.", "се", "върнал")]
    [DataRow("Той ще се е върнал.", "се", "върнал")]
    [DataRow("Аз бих се върнал.", "се", "върнал")]
    [DataRow("Върна се вкъщи.", "върна", "се")]
    public void Reflexive_membership_is_local_to_one_finite_carrier_in_each_tuple(string text, string leftText, string rightText)
    {
        TextAnalysis actual = Analyze(text);
        FinitePredicateAcceptance.ConserveInventory(actual);
        int left = Token(actual, leftText), right = Token(actual, rightText);
        TextPredicateLink[] reflexive = Links(actual).Where(l => l.Kind == "reflexive_clitic").ToArray();
        Assert.IsNotEmpty(reflexive, text);
        Assert.IsTrue(reflexive.All(l => l.Left.TokenIndex == left && l.Right.TokenIndex == right), text);
        Assert.IsEmpty(AgreementWarnings(actual), text);
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Тя се е усмихвала.", "е", "усмихвала", true)]
    [DataRow("Ти си се усмихвала.", "си", "усмихвала", true)]
    [DataRow("Тя се е върнала.", "е", "върнала", false)]
    [DataRow("Ти си се върнала.", "си", "върнала", false)]
    public void Perfect_reflexives_bind_to_clean_and_benign_legacy_participle_heads(
        string text, string auxiliaryText, string participleText, bool benignLegacySuffix)
    {
        TextAnalysis actual = Analyze(text);
        FinitePredicateAcceptance.ConserveInventory(actual);
        int auxiliary = Token(actual, auxiliaryText), reflexive = Token(actual, "се"), head = Token(actual, participleText);
        TextPredicateLink[] links = Links(actual);
        Assert.IsTrue(links.Any(l => l.Kind == "auxiliary_participle" &&
            l.Left.TokenIndex == auxiliary && l.Right.TokenIndex == head), text);
        TextPredicateLink[] reflexives = links.Where(l => l.Kind == "reflexive_clitic").ToArray();
        Assert.IsNotEmpty(reflexives, text);
        Assert.IsTrue(reflexives.All(l => l.Left.TokenIndex == reflexive && l.Right.TokenIndex == head), text);
        foreach (TextPredicateLink link in reflexives)
        {
            Assert.IsNotEmpty(link.Right.Readings, text);
            Assert.IsTrue(link.Markers.Any(m => m.TokenIndex == auxiliary && m.Readings.Count == 1), text);
            foreach (TextReadingSnapshot row in Rows(actual, link.Right))
            {
                Assert.AreEqual(MorphologicalVerbForm.Participle, row.Features.VerbForm, text);
                Assert.AreEqual(benignLegacySuffix, row.Features.HasUnrecognizedFeatures, text);
                Assert.IsFalse(row.Features.HasConflictingFeatures, text);
                Assert.AreEqual(MorphologicalFeatures.FromTag(row.Raw.PartOfSpeech), row.Features, text);
                if (benignLegacySuffix)
                    Assert.IsTrue(row.Raw.PartOfSpeech is { } rawTag &&
                        (rawTag.Contains(",+се", StringComparison.Ordinal) || rawTag.Contains(";+се", StringComparison.Ordinal)), text);
            }
        }
        CollectionAssert.AreEqual(session.Lookup(participleText, LookupMode.Exact, 256).ToArray(),
            CompleteRows(actual, head).Select(r => r.Raw).ToArray(), text);
        Assert.IsEmpty(AgreementWarnings(actual), text);
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Тя се е ксбгх.")]
    [DataRow("Ти си се ксбгх.")]
    public void An_unknown_lexical_head_does_not_gain_an_auxiliary_participle_group(string text)
    {
        TextAnalysis actual = Analyze(text);
        FinitePredicateAcceptance.ConserveInventory(actual);
        int unknown = Token(actual, "ксбгх");
        Assert.IsFalse(CompleteRows(actual, unknown).Any(r => r.Features.VerbForm == MorphologicalVerbForm.Participle), text);
        Assert.IsEmpty(Links(actual).Where(l => l.Kind is "auxiliary_participle" or "conditional_auxiliary_participle").ToArray(), text);
        Assert.IsFalse(Links(actual).Any(l => l.Kind == "reflexive_clitic" && l.Right.TokenIndex == unknown), text);
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Усмихвала се.")]
    [DataRow("Тя усмихвала се.")]
    public void An_unanchored_participle_does_not_gain_a_predicate_group(string text)
    {
        TextAnalysis actual = Analyze(text);
        FinitePredicateAcceptance.ConserveInventory(actual);
        Assert.IsEmpty(Links(actual).Where(l => l.Kind is "reflexive_clitic" or "auxiliary_participle" or
            "conditional_auxiliary_participle").ToArray(), text);
    }

    [TestMethod, TestCategory("FullPackage")]
    public void A_participial_modifier_without_an_auxiliary_does_not_gain_a_predicate_group()
    {
        TextAnalysis actual = Analyze("Върналият се човек работи.");
        FinitePredicateAcceptance.ConserveInventory(actual);
        Assert.IsEmpty(Links(actual).Where(l => l.Kind == "reflexive_clitic").ToArray());
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Разбира се, той ще се върне.")]
    [DataRow("Той, разбира се, ще се върне.")]
    [DataRow("Той ще се върне, разбира се.")]
    public void Parenthetical_reflexive_links_preserve_local_heads_without_becoming_matrix_subjects(string text)
    {
        TextAnalysis actual = Analyze(text);
        FinitePredicateAcceptance.ConserveInventory(actual);
        int lexical = Token(actual, "върне"), parenthetical = Token(actual, "разбира"), subject = Token(actual, "той");
        TextPredicateLink[] reflexives = Links(actual).Where(l => l.Kind == "reflexive_clitic").ToArray();
        Assert.IsTrue(reflexives.Any(l => l.Left.TokenIndex == parenthetical), text);
        Assert.IsTrue(reflexives.Any(l => l.Right.TokenIndex == lexical), text);
        Assert.IsTrue(reflexives.All(l => l.Left.TokenIndex == parenthetical || l.Right.TokenIndex == lexical), text);
        Assert.IsFalse(actual.Context!.Sentences.SelectMany(s => s.SelectedRoles)
            .Any(r => r.Role == TextContextRole.Subject && r.TokenIndex != subject), text);
        Assert.IsFalse(actual.Context.Sentences.Any(s => s.SelectedPredicateTokenIndex == parenthetical), text);
        Assert.IsEmpty(AgreementWarnings(actual), text);
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Ще, се върна.")]
    [DataRow("Ще се, върна.")]
    [DataRow("Ще се. Върна се.")]
    [DataRow("Ще се\nвърна.")]
    public void Future_links_do_not_jump_punctuation_sentence_or_line_boundaries(string text)
    {
        TextAnalysis actual = Analyze(text);
        FinitePredicateAcceptance.ConserveInventory(actual);
        Assert.IsEmpty(Links(actual).Where(l => l.Kind == "future_marker").ToArray(), text);
        foreach (TextPredicateLink link in Links(actual).Where(l => l.Kind == "reflexive_clitic"))
        {
            TokenAnalysis left = actual.Tokens[link.Left.TokenIndex], right = actual.Tokens[link.Right.TokenIndex];
            Assert.IsTrue(text[(left.Start + left.Length)..right.Start].All(c => c is ' ' or '\t'), text);
        }
    }

    private static TextAnalysis Analyze(string text) => session.AnalyzeText(text, false,
        new() { IncludeGrammarEvidence = true, IncludeAdditionalRoleHeuristics = true }, default);
    private static TextPredicateLink[] Links(TextAnalysis actual) => actual.PredicateLinks!.Sentences.SelectMany(s => s.Links).ToArray();
    private static TextDiagnostic[] AgreementWarnings(TextAnalysis actual) => actual.Diagnostics
        .Where(d => d.Code is Agreement or "pronoun_verb_agreement_mismatch").ToArray();
    private static int Token(TextAnalysis actual, string text) => actual.Tokens.Select((token, index) => (token, index))
        .Single(t => t.token.Kind == TokenKind.Word && string.Equals(t.token.Text, text, StringComparison.OrdinalIgnoreCase)).index;
    private static IReadOnlyList<TextReadingSnapshot> CompleteRows(TextAnalysis actual, int token) =>
        actual.ReadingInventory!.Sets[actual.ReadingInventory.Occurrences.Single(o => o.TokenIndex == token).ReadingSetIndex].ResolvedReadings;
    private static IEnumerable<TextReadingSnapshot> Rows(TextAnalysis actual, TextSyntaxSupport support) =>
        support.Readings.Select(r => CompleteRows(actual, r.TokenIndex)[r.CompleteReadingIndex]);
}
