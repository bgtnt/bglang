using BgLang.Api;
using BgLang.Contracts;
using BgLang.Tests.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

[TestClass, DoNotParallelize]
public sealed class ConditionalCopulaPackageTests
{
    private static BulgarianLanguageSession session = null!;

    [ClassInitialize]
    public static void Open(TestContext _) => session = GraphStressAcceptance.Open();

    [ClassCleanup]
    public static void Close() => session.Dispose();

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Те биха били готов.", "готов", "Те|биха|били|готов")]
    [DataRow("Ние бихме били готов.", "готов", "Ние|бихме|били|готов")]
    [DataRow("Те не биха били готов.", "готов", "Те|биха|били|готов")]
    [DataRow("Знам, че те биха били готов.", "готов", "те|биха|били|готов")]
    [DataRow("Той би бил готова.", "готова", "Той|би|бил|готова")]
    [DataRow("Той не би бил готова.", "готова", "Той|би|бил|готова")]
    [DataRow("Той не би бил невинна.", "невинна", "Той|би|бил|невинна")]
    [DataRow("Ученикът би бил готова.", "готова", "Ученикът|би|бил|готова")]
    [DataRow("Новите деца биха били готов.", "готов", "Новите|деца|биха|били|готов")]
    [DataRow("Те биха били много готов.", "готов", "Те|биха|били|готов")]
    public void Conditional_copula_warns_on_disjoint_predicative_adjectives_with_original_supports(
        string text, string target, string expectedSupports)
    {
        BulgarianLanguageSession language = session;
        foreach (TextContextOptions options in new TextContextOptions[]
                 { default, new() { IncludeGrammarEvidence = true, IncludeAdditionalRoleHeuristics = true } })
        {
            TextAnalysis analysis = language.AnalyzeText(text, false, options, default);
            TextDiagnostic[] warnings = analysis.Diagnostics.Where(d => IsActionablePackageWarning(d.Code)).ToArray();
            Assert.HasCount(1, warnings, $"{text}: [{string.Join(',', warnings.Select(d => d.Code))}]");
            TextDiagnostic warning = warnings[0];
            Assert.AreEqual("productive_predicative_agreement_mismatch", warning.Code, text);
            Assert.AreEqual("clause_fusion_agreement.v1", warning.RuleId, text);
            Assert.AreEqual(text.IndexOf(target, StringComparison.Ordinal), warning.Start, text);
            Assert.AreEqual(target.Length, warning.Length, text);
            Assert.IsNull(warning.ProjectedText, text);
            Assert.IsNull(warning.GraphReadings, text);
            Assert.IsNotNull(warning.Readings, text);
            CollectionAssert.AreEqual(expectedSupports.Split('|'), warning.Readings
                .Select(r => r.TokenIndex).Distinct().Select(i => analysis.Tokens[i].Text).ToArray(), text);

            Assert.IsNotNull(analysis.ReadingInventory, text);
            foreach (TextReadingReference support in warning.Readings)
            {
                TextReadingOccurrence occurrence = analysis.ReadingInventory.Occurrences.Single(o => o.TokenIndex == support.TokenIndex);
                TextReadingSnapshot row = analysis.ReadingInventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings[support.CompleteReadingIndex];
                if (analysis.Tokens[support.TokenIndex].Text is "би" or "биха" or "бихме" or "бил" or "били")
                    Assert.IsTrue(row.Raw.Lemma is "съм" or "бъда", "Lexical бия must remain in the inventory, outside copular proof.");
            }
            if (options.IncludeGrammarEvidence)
                Assert.IsTrue(analysis.Grammar!.Matches.Any(m =>
                    m.RuleId == "grammar.agreement.copular_adjective.v1" && m.Assessment == TextGrammarAssessment.Mismatch &&
                    m.DependentTokenIndices.Any(i => analysis.Tokens[i].Start == warning.Start)), text);
            AssertConditionalCopulaInventory(language, analysis, text);
        }
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Те биха били готови.")]
    [DataRow("Те никога не биха били невинен.")]
    [DataRow("Те биха били невинен човек.")]
    [DataRow("Ние бихме били готови.")]
    [DataRow("Те не биха били готови.")]
    [DataRow("Знам, че те биха били готови.")]
    [DataRow("Той би бил готов.")]
    [DataRow("Тя би била готова.")]
    [DataRow("Ученикът би бил готов.")]
    [DataRow("Новите деца биха били готови.")]
    [DataRow("Болните биха били готови.")]
    [DataRow("Те биха били много готови.")]
    [DataRow("Вие бихте били готова.")]
    [DataRow("Те биха го били беззащитен.")]
    [DataRow("Те биха били готовия работник.")]
    [DataRow("Те биха били добър отбор.")]
    [DataRow("Тя би била добър учител.")]
    [DataRow("Те биха били победените.")]
    [DataRow("Ние бихме били добър за работата екип.")]
    [DataRow("Те биха се били за свободата си.")]
    [DataRow("Знам, „те биха били готов“.")]
    public void Conditional_copula_keeps_compatible_nominal_lexical_and_protected_alternatives(string text)
    {
        BulgarianLanguageSession language = session;
        foreach (TextContextOptions options in new TextContextOptions[]
                 { default, new() { IncludeGrammarEvidence = true, IncludeAdditionalRoleHeuristics = true } })
        {
            TextAnalysis analysis = language.AnalyzeText(text, false, options, default);
            Assert.IsEmpty(analysis.Diagnostics.Where(d => IsActionablePackageWarning(d.Code)).ToArray(), text);
            AssertConditionalCopulaInventory(language, analysis, text);
        }
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("невинен", "невинен", MorphologicalGender.Masculine)]
    [DataRow("виновен", "виновен", MorphologicalGender.Masculine)]
    [DataRow("болен", "болен", MorphologicalGender.Masculine)]
    [DataRow("болна", "болен", MorphologicalGender.Feminine)]
    public void Conditional_substantive_objects_need_a_real_lexical_verb_alternative(
        string form, string lemma, MorphologicalGender gender)
    {
        foreach (TextContextOptions options in new TextContextOptions[]
                 { default, new() { IncludeGrammarEvidence = true, IncludeAdditionalRoleHeuristics = true } })
        {
            string conditionalText = $"Те биха били {form}.";
            TextAnalysis conditional = session.AnalyzeText(conditionalText, false, options, default);
            Assert.IsEmpty(conditional.Diagnostics.Where(d => IsActionablePackageWarning(d.Code)).ToArray(), conditionalText);
            TextReadingOccurrence adjective = conditional.ReadingInventory!.Occurrences.Single(o => o.Text == form);
            TextReadingSnapshot[] rows = conditional.ReadingInventory.Sets[adjective.ReadingSetIndex].ResolvedReadings.ToArray();
            Assert.IsNotEmpty(rows);
            Assert.IsTrue(rows.All(row => row.Raw.Lemma == lemma && row.Features is
                { PartOfSpeech: MorphologicalPartOfSpeech.Adjective, Number: MorphologicalNumber.Singular } &&
                row.Features.Gender == gender), "Substantive use is positive lexical evidence, not an injected noun reading.");
            TextReadingOccurrence participle = conditional.ReadingInventory.Occurrences.Single(o => o.Text == "били");
            var tails = conditional.ReadingInventory.Sets[participle.ReadingSetIndex].ResolvedReadings;
            Assert.IsTrue(tails.Any(row => row.Raw.Lemma == "бия" && row.Features is
                { VerbForm: MorphologicalVerbForm.Participle, ParticipleKind: MorphologicalParticipleKind.PastAoristActive,
                  DeclaredTransitivity: MorphologicalVerbTransitivity.Transitive }));
            Assert.IsTrue(tails.Any(row => row.Raw.Lemma == "съм" && row.Features.VerbForm == MorphologicalVerbForm.Participle));
            if (options.IncludeGrammarEvidence)
                Assert.IsFalse(conditional.Grammar!.Matches.Any(m => m.RuleId == "grammar.agreement.copular_adjective.v1" &&
                    m.Assessment == TextGrammarAssessment.Mismatch), conditionalText);
            AssertConditionalCopulaInventory(session, conditional, conditionalText);

            string plainText = $"Те са {form}.";
            TextAnalysis plain = session.AnalyzeText(plainText, false, options, default);
            Assert.IsTrue(plain.Diagnostics.Any(d => d.Code == "productive_predicative_agreement_mismatch" &&
                d.Start == plainText.IndexOf(form, StringComparison.Ordinal) && d.Length == form.Length), plainText);
            if (options.IncludeGrammarEvidence)
                Assert.IsTrue(plain.Grammar!.Matches.Any(m => m.RuleId == "grammar.agreement.copular_adjective.v1" &&
                    m.Assessment == TextGrammarAssessment.Mismatch), plainText);
            AssertConditionalCopulaInventory(session, plain, plainText);
        }
    }

    private static bool IsActionablePackageWarning(string code) => code is not
        ("full_short_article_check_not_ready" or
         "article_prepositional_group_locally_assessed" or
         "hyphenated_degree_morphology_evidence" or
         "reviewed_word_formation_evidence" or
         "style_repetition_evidence" or
         "contextual_morphology_reading_evidence" or
         "proper_name_exact_morphology_given_name_evidence" or
         "proper_name_exact_morphology_family_name_evidence" or
         "proper_name_person_candidate");

    private static void AssertConditionalCopulaInventory(BulgarianLanguageSession language, TextAnalysis analysis, string text)
    {
        Assert.AreEqual(text, analysis.Text, "Analysis never rewrites the source.");
        Assert.IsNotNull(analysis.ReadingInventory);
        foreach (TextReadingOccurrence occurrence in analysis.ReadingInventory.Occurrences)
        {
            TextReadingSet set = analysis.ReadingInventory.Sets[occurrence.ReadingSetIndex];
            CollectionAssert.AreEqual(language.Lookup(occurrence.Text, LookupMode.Exact, 256).ToArray(),
                set.ResolvedReadings.Select(r => r.Raw).ToArray(), occurrence.Text);
            CollectionAssert.AreEqual(set.ResolvedReadings.Take(set.DisplayedReadingCount).Select(r => r.Raw).ToArray(),
                analysis.Tokens[occurrence.TokenIndex].Analysis!.Readings.ToArray(), occurrence.Text);
            Assert.AreEqual(occurrence.Text, analysis.Text.Substring(occurrence.Start, occurrence.Length));
        }
        foreach (TokenAnalysis token in analysis.Tokens.Where(t => t.Analysis?.NormalizedWord is "би" or "биха" or "бихме" or "бил" or "били"))
            Assert.IsTrue(token.Analysis!.Readings.Any(r => r.Lemma == "бия"), "The competing lexical reading remains publicly visible.");
    }
}
