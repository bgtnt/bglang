using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

public sealed partial class PublicPackageTests
{
    // Prospective literal oracle; execute through the existing strict complete-package initializer.
    [TestMethod]
    [DataRow("Спирам пред сипя къща.", 1)]
    [DataRow("Пред сипя къща спирам.", 1)]
    [DataRow("Спирам пред сипя, стара, каменна, тиха, просторна, висока и красива къща.", 7)]
    public void Prepositional_known_word_advice_preserves_raw_order_and_really_reranks(string text, int modifiers)
    {
        TextAnalysis before = session.AnalyzeText(text);
        TextAnalysis actual = session.AnalyzeText(text, false, NominalOptions, default);
        NominalRepairReview review = actual.NominalConstructions!.Reviews.Single();
        Assert.AreEqual("preposition+NP", review.Pattern);
        Assert.IsTrue(review.NoChangeRetained); Assert.IsFalse(review.AllPackageAlternativesExamined);
        Assert.AreEqual(text.IndexOf("сипя", StringComparison.Ordinal), review.Start); Assert.AreEqual(4, review.Length);
        string[] rawForms = review.Alternatives.Select(a => a.Alternative.Form).ToArray();
        CollectionAssert.AreEqual(new[] { "сипя", "кипя", "сивя", "силя", "синя", "сип", "сипа", "сипе", "сипея", "сипи" }, rawForms);
        int blue = Array.IndexOf(rawForms, "синя"), boil = Array.IndexOf(rawForms, "кипя");
        Assert.IsTrue(boil < blue, "The supported form must initially follow an equal-distance alternative.");
        Assert.AreEqual(1, review.Alternatives[blue].Alternative.Distance);
        Assert.AreEqual(1, review.Alternatives[boil].Alternative.Distance);
        int[] contextual = review.ContextualOrder!.ToArray();
        CollectionAssert.AreEqual(Enumerable.Range(0, rawForms.Length).ToArray(), contextual.Order().ToArray());
        Assert.AreEqual(0, contextual[0], "The original distance-zero candidate is retained.");
        Assert.IsTrue(Array.IndexOf(contextual, blue) < Array.IndexOf(contextual, boil));
        for (int i = 1; i < contextual.Length; i++)
            Assert.IsTrue(review.Alternatives[contextual[i - 1]].Alternative.Distance <= review.Alternatives[contextual[i]].Alternative.Distance);
        Assert.IsNull(review.Alternatives[boil].Phrase);
        TextNominalPhrase phrase = review.Alternatives[blue].Phrase!;
        Assert.AreEqual(NominalAgreementAssessment.Compatible, phrase.Assessment);
        Assert.HasCount(modifiers, phrase.ModifierTokenIndices);
        Assert.AreEqual(text.IndexOf("къща", StringComparison.Ordinal), actual.Tokens[phrase.HeadTokenIndex].Start);
        Assert.IsTrue(phrase.HeadSupports.Any(h => h.Members.All(m => m.Assessment == NominalAgreementAssessment.Compatible)));
        foreach (NominalHeadSupport head in phrase.HeadSupports.Where(h => h.Members.All(m => m.Assessment == NominalAgreementAssessment.Compatible)))
        {
            Assert.AreEqual(phrase.HeadTokenIndex, head.Reading!.Value.TokenIndex);
            NominalMemberSupport target = head.Members.Single(m => m.TokenIndex == review.TokenIndex);
            Assert.IsEmpty(target.Readings); Assert.IsEmpty(target.GraphReadings);
            Assert.IsNotNull(target.ReplacementReadings); Assert.IsTrue(target.ReplacementReadings.Count > 0);
            foreach (int index in target.ReplacementReadings)
                Assert.AreEqual(MorphologicalPartOfSpeech.Adjective, review.Alternatives[blue].Alternative.Readings[index].Features.PartOfSpeech);
        }
        int prepStart = text.IndexOf("пред", StringComparison.OrdinalIgnoreCase);
        int prepToken = Enumerable.Range(0, actual.Tokens.Count).Single(i => actual.Tokens[i].Start == prepStart);
        TextReadingOccurrence occurrence = actual.ReadingInventory!.Occurrences.Single(o => o.TokenIndex == prepToken);
        var rows = actual.ReadingInventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings;
        Assert.IsTrue(rows.Count > 0 && rows.All(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Preposition));
        CollectionAssert.AreEqual(Enumerable.Range(0, rows.Count).Select(i => new TextReadingReference(prepToken, i)).ToArray(),
            review.ContextReadings!.ToArray());
        SpellingWordEvidence spelling = actual.Spelling!.Words.Single(w => w.TokenIndex == review.TokenIndex);
        Assert.AreEqual(SpellingSearchDisposition.NotRequestedKnown, spelling.Search); Assert.IsEmpty(spelling.Alternatives);
        SameOldText(before, actual);
    }

    [TestMethod]
    [DataRow("'Спирам пред сипя къща.'", DisplayName = "PP_QuoteGuard_Public_AsciiSingle")]
    [DataRow("\"Спирам пред сипя къща.\"", DisplayName = "PP_QuoteGuard_Public_AsciiDouble")]
    [DataRow("‘Спирам пред сипя къща.’", DisplayName = "PP_QuoteGuard_Public_CurlySingle")]
    [DataRow("‚Спирам пред сипя къща.", DisplayName = "PP_QuoteGuard_Public_LowSingleUnclosed")]
    [DataRow("“Спирам пред сипя къща.”", DisplayName = "PP_QuoteGuard_Public_CurlyDouble")]
    [DataRow("«Спирам пред сипя къща.»", DisplayName = "PP_QuoteGuard_Public_AngleDouble")]
    [DataRow("`Спирам пред сипя къща.`", DisplayName = "PP_QuoteGuard_Public_Backtick")]
    [DataRow("„Цитат. Спирам пред сипя къща. Край.“", DisplayName = "PP_QuoteGuard_Public_MiddleSentence")]
    [DataRow("„Цитат. Спирам пред сипя къща.", DisplayName = "PP_QuoteGuard_Public_UnclosedContinuation")]
    [DataRow("Спирам пред сипя къща.“", DisplayName = "PP_QuoteGuard_Public_UnmatchedCloser")]
    [DataRow("„Цитат «думи». Спирам пред сипя къща. Край.“", DisplayName = "PP_QuoteGuard_Public_Nested")]
    [DataRow("„Цитат.“ Спирам пред сипя къща.", DisplayName = "PP_QuoteGuard_Public_UnquotedAfterClosedQuote")]
    [DataRow("Спирам пред сипя къща. „Цитат.“", DisplayName = "PP_QuoteGuard_Public_UnquotedBeforeQuote")]
    public void Quotation_markers_protect_the_entire_document_from_prepositional_advice(string text)
    {
        TextAnalysis before = session.AnalyzeText(text);
        TextAnalysis actual = session.AnalyzeText(text, false, NominalOptions, default);
        Assert.IsEmpty(actual.NominalConstructions!.Reviews);
        Assert.IsTrue(actual.NominalConstructions.Phrases.Any(p => actual.Tokens[p.HeadTokenIndex].Text == "къща"));
        SameOldText(before, actual);
    }

    [TestMethod]
    [DataRow("Спирам пред сипя къща.", "preposition+NP", DisplayName = "PP_QuoteGuard_Public_UnquotedPredicateInitial")]
    [DataRow("Пред сипя къща спирам.", "preposition+NP", DisplayName = "PP_QuoteGuard_Public_UnquotedPrepositionInitial")]
    [DataRow("Рисувам сипя река.", "finite+NP", DisplayName = "PP_QuoteGuard_Public_ExistingFinitePlain")]
    [DataRow("„Цитат.“ Рисувам сипя река.", "finite+NP", DisplayName = "PP_QuoteGuard_Public_ExistingFiniteAfterClosedQuote")]
    public void Document_quote_veto_preserves_unquoted_prepositional_and_existing_finite_advice(string text, string pattern)
    {
        TextAnalysis before = session.AnalyzeText(text);
        TextAnalysis actual = session.AnalyzeText(text, false, NominalOptions, default);
        NominalRepairReview review = actual.NominalConstructions!.Reviews.Single();
        Assert.AreEqual(pattern, review.Pattern); Assert.IsTrue(review.NoChangeRetained);
        Assert.AreEqual(text.IndexOf("сипя", StringComparison.Ordinal), review.Start); Assert.AreEqual(4, review.Length);
        CollectionAssert.AreEqual(new[] { "сипя", "кипя", "сивя", "силя", "синя", "сип", "сипа", "сипе", "сипея", "сипи" },
            review.Alternatives.Select(a => a.Alternative.Form).ToArray());
        NominalRepairAlternative supported = review.Alternatives.Single(a => a.Alternative.Form == "синя");
        Assert.AreEqual(NominalAgreementAssessment.Compatible, supported.Phrase!.Assessment);
        SameOldText(before, actual);
    }

    [TestMethod]
    [DataRow("Спирам пред сипя село.")]
    [DataRow("Спирам пред сипя, старо къща.")]
    [DataRow("Спирам пред сипя село и синя къща.")]
    [DataRow("Спирам пред синя къща.")]
    [DataRow("Спирам и сипя вода.")]
    [DataRow("Спирам, сипя вода.")]
    [DataRow("Искам да сипя вода.")]
    [DataRow("Спирам пред къщата, сипя вода.")]
    [DataRow("Спирам пред, сипя къща.")]
    [DataRow("Спирам пред. Сипя къща.")]
    [DataRow("Спирам пред „сипя“ къща.")]
    [DataRow("„Спирам пред сипя къща.“")]
    public void Prepositional_advice_preserves_contrasts_valid_clauses_and_protected_boundaries(string text)
    {
        TextAnalysis before = session.AnalyzeText(text);
        TextAnalysis actual = session.AnalyzeText(text, false, NominalOptions, default);
        Assert.IsEmpty(actual.NominalConstructions!.Reviews);
        SameOldText(before, actual);
    }

    [TestMethod]
    public void Prepositional_repair_keeps_coordinated_heads_separate()
    {
        const string text = "Спирам пред сипя къща и синя ограда.";
        TextAnalysis before = session.AnalyzeText(text);
        TextAnalysis actual = session.AnalyzeText(text, false, NominalOptions, default);
        TextNominalPhrase[] cores = actual.NominalConstructions!.Phrases.Where(p =>
            actual.Tokens[p.HeadTokenIndex].Text is "къща" or "ограда").ToArray();
        Assert.HasCount(2, cores); Assert.AreNotEqual(cores[0].HeadTokenIndex, cores[1].HeadTokenIndex);
        NominalRepairReview review = actual.NominalConstructions.Reviews.Single();
        TextNominalPhrase repair = review.Alternatives.Single(a => a.Alternative.Form == "синя").Phrase!;
        Assert.AreEqual(cores[0].HeadTokenIndex, repair.HeadTokenIndex);
        Assert.IsTrue(repair.Start + repair.Length <= actual.Tokens[cores[0].HeadTokenIndex].Start + actual.Tokens[cores[0].HeadTokenIndex].Length);
        Assert.IsTrue(repair.HeadSupports.All(h => h.Reading!.Value.TokenIndex == cores[0].HeadTokenIndex));
        CollectionAssert.AreEqual(new[] { review.TokenIndex }, repair.ModifierTokenIndices.ToArray());
        SameOldText(before, actual);
    }

    [TestMethod]
    public void Prepositional_repair_is_occurrence_local_with_utf16_prefix_and_shared_lexical_query()
    {
        const string text = "🙂 Спирам пред сипя къща. Спирам пред сипя село.";
        TextAnalysis before = session.AnalyzeText(text);
        TextAnalysis actual = session.AnalyzeText(text, false, NominalOptions, default);
        NominalRepairReview review = actual.NominalConstructions!.Reviews.Single();
        Assert.AreEqual(15, review.Start); Assert.AreEqual(4, review.Length);
        Assert.AreEqual("сипя", actual.Tokens[review.TokenIndex].Text);
        Assert.AreEqual("🙂 Спирам пред синя къща. Спирам пред сипя село.", text.Remove(review.Start, review.Length).Insert(review.Start, "синя"));
        Assert.IsTrue(review.ContextReadings!.All(r => actual.Tokens[r.TokenIndex].Start < review.Start));
        Assert.IsFalse(actual.NominalConstructions.Reviews.Any(r => r.Start == text.LastIndexOf("сипя", StringComparison.Ordinal)));
        SameOldText(before, actual);
    }
}
