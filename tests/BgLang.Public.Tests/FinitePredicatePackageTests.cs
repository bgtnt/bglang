using BgLang.Contracts;
using BgLang.Api;
using BgLang.Tests.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

[TestClass, DoNotParallelize]
public sealed class FinitePredicatePackageTests
{
    private static BulgarianLanguageSession session = null!;
    [ClassInitialize] public static void Open(TestContext _) => session = FinitePredicateAcceptance.Open();
    [ClassCleanup] public static void Close() => session.Dispose();

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("FP01", "Аз пея пътувам.", "P", DisplayName = "FP01")]
    [DataRow("FP02", "Ние четем пишем.", "P", DisplayName = "FP02")]
    [DataRow("FP03", "Аз пея и пътувам.", "N", DisplayName = "FP03")]
    [DataRow("FP04", "Аз пея, пътувам.", "N", DisplayName = "FP04")]
    [DataRow("FP05", "Искам да пея.", "N", DisplayName = "FP05")]
    [DataRow("FP06", "Мога да пея и да пътувам.", "N", DisplayName = "FP06")]
    [DataRow("FP07", "Аз съм пял и ще пътувам.", "N", DisplayName = "FP07")]
    [DataRow("FP08", "Иди гледай хората в очите сега!", "N", DisplayName = "FP08")]
    [DataRow("FP09", "Той носи коси.", "U", DisplayName = "FP09")]
    [DataRow("FP10", "Тя каза: „Пея.“", "N", DisplayName = "FP10")]
    [DataRow("FP12", "Малкият пея пътува с мен.", "U", DisplayName = "FP12")]
    [DataRow("FP13", "Малкият пътува с мен.", "N", DisplayName = "FP13")]
    [DataRow("FP14", "Малкият пее и пътува с мен.", "N", DisplayName = "FP14")]
    [DataRow("FP15", "Малкият певец пътува с мен.", "N", DisplayName = "FP15")]
    public void Independent_finite_families_keep_readings_links_spans_and_uncertainty(string id, string text, string expected)
    {
        FinitePredicateAcceptance.AssertIndependent(session, id, text, expected, FinitePredicateAcceptance.Analyze(session, text));
    }

    private const string PrefixRule = "licensed_subordinate_pronoun_prefix_agreement.v1";
    private const string Agreement = "productive_morphology_agreement_mismatch";

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Тя уточнява, че аз работя.", -1)]
    [DataRow("Тя уточнява, че аз работим.", 19)]
    [DataRow("Тя уточнява, че аз работя ксбгх.", -1)]
    [DataRow("Тя уточнява, че аз работим ксбгх.", 19)]
    [DataRow("Вратата скърца, защото аз работя.", -1)]
    [DataRow("Вратата скърца, защото аз работим.", 26)]
    [DataRow("Вратата скърца, защото аз работя ксбгх.", -1)]
    [DataRow("Вратата скърца, защото аз работим ксбгх.", 26)]
    [DataRow("Тя уточнява, аз работим.", -1)]
    [DataRow("Тя уточнява, „че аз работим“.", -1)]
    [DataRow("Тя уточнява (тук, че аз работим).", -1)]
    [DataRow("Тя уточнява, че аз ксбгх работим.", -1)]
    [DataRow("Тя уточнява, че аз в салона работим.", -1)]
    [DataRow("Тя уточнява, че аз ще работим.", -1)]
    [DataRow("Тя уточнява, че аз да работим.", -1)]
    [DataRow("„(“ Тя уточнява, че аз работим.", 23)]
    [DataRow("Тя уточнява, че аз работим по 5 кв. м.", 19)]
    public void Licensed_subordinate_prefix_keeps_original_carrier_and_scoped_warnings(string text, int start)
    {
        TextAnalysis actual = session.AnalyzeText(text);
        (string Code, string Rule, int Start, int Length)[] expected = start < 0 ? [] : [(Agreement, PrefixRule, start, 7)];
        var scoped = actual.Diagnostics.Where(d => d.Code is Agreement or "pronoun_verb_agreement_mismatch"
            or "productive_attribute_agreement_mismatch" or "unseparated_finite_predications"
            or "full_article_expected_for_subject_candidate" or "short_article_expected_after_preposition_candidate").ToArray();
        CollectionAssert.AreEqual(expected, scoped.Select(d => (d.Code, d.RuleId, d.Start, d.Length)).ToArray());
        var proofs = actual.Context!.Sentences.SelectMany(s => s.Candidates).Where(c => c.AuthorityLane == PrefixRule).ToArray();
        Assert.HasCount(expected.Length, proofs);
        if (start < 0) return;

        var warning = scoped.Single();
        Assert.IsNull(warning.ProjectedText);
        Assert.IsNull(warning.GraphReadings);
        int carrier = actual.Tokens.Select((token, index) => (token, index)).Single(pair => pair.token.Start == start).index;
        int subject = carrier - 1;
        CollectionAssert.AreEqual(new[] { new TextReadingReference(subject, 0), new TextReadingReference(carrier, 0) }, warning.Readings!.ToArray());
        Assert.AreEqual("работим", actual.Tokens[carrier].Text);
        Assert.AreEqual("аз", actual.Tokens[subject].Text);
        var proof = proofs.Single();
        Assert.IsFalse(proof.Selectable);
        Assert.AreEqual(carrier, proof.PredicateTokenIndex);
        CollectionAssert.AreEqual(new[] { (TextContextRole.Predicate, carrier), (TextContextRole.Subject, subject) },
            proof.Roles.Select(r => (r.Role, r.TokenIndex)).ToArray());
        Assert.IsTrue(proof.EvidenceCodes.Contains("connector_readings=0"));
        Assert.IsTrue(proof.EvidenceCodes.Contains($"consumed_prefix={start - 3}:10"));
        var connector = actual.ReadingInventory!.Occurrences.Single(o => o.TokenIndex == subject - 1);
        var readings = actual.ReadingInventory.Sets[connector.ReadingSetIndex].ResolvedReadings;
        var expectedPos = connector.Text == "че"
            ? new[] { MorphologicalPartOfSpeech.Conjunction, MorphologicalPartOfSpeech.Particle }
            : new[] { MorphologicalPartOfSpeech.Adverb };
        CollectionAssert.AreEqual(expectedPos, readings.Select(r => r.Features.PartOfSpeech).ToArray());
    }

}
