using System.Text.Json;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

public sealed partial class PublicPackageTests
{
    private static readonly TextContextOptions NominalOptions = new() { IncludeNominalConstructions = true };

    // Independent human examples, frozen before measurement. Gold does not come from the new projection.
    [TestMethod]
    [DataRow("Стара къща пустее.", "къща", 1, 0, NominalAgreementAssessment.Compatible)]
    [DataRow("Голяма стара каменна къща пустее.", "къща", 3, 0, NominalAgreementAssessment.Compatible)]
    [DataRow("Стара, изоставена, порутена, синя къща пустее.", "къща", 4, 3, NominalAgreementAssessment.Compatible)]
    [DataRow("Стара, изоставена и порутена къща пустее.", "къща", 3, 2, NominalAgreementAssessment.Compatible)]
    [DataRow("Старо, изоставена къща пустее.", "къща", 2, 1, NominalAgreementAssessment.Mismatch)]
    [DataRow("Старата, изоставена къща пустее.", "къща", 2, 1, NominalAgreementAssessment.Compatible)]
    [DataRow("Стара река пресъхна.", "река", 1, 0, NominalAgreementAssessment.Compatible)]
    public void Nominal_core_keeps_one_external_slot_and_all_original_members(string text, string head,
        int modifiers, int connectors, NominalAgreementAssessment assessment)
    {
        TextAnalysis before = session.AnalyzeText(text);
        TextAnalysis actual = session.AnalyzeText(text, false, NominalOptions, default);
        TextNominalConstructionEvidence evidence = actual.NominalConstructions!;
        Assert.IsTrue(evidence.Available); Assert.IsNull(evidence.UnavailableReason);
        Assert.AreEqual("nominal_constructions.v1", evidence.Version);
        TextNominalPhrase phrase = evidence.Phrases.Single(p => actual.Tokens[p.HeadTokenIndex].Text == head);
        Assert.AreEqual("NP", phrase.ConstructionKey); Assert.AreEqual(assessment, phrase.Assessment);
        Assert.HasCount(modifiers, phrase.ModifierTokenIndices); Assert.HasCount(connectors, phrase.ConnectorTokenIndices);
        Assert.AreEqual(text.IndexOf(head, StringComparison.Ordinal) + head.Length - phrase.Start, phrase.Length);
        Assert.AreEqual(phrase.Start, actual.Tokens[phrase.ModifierTokenIndices[0]].Start);
        Assert.IsTrue(phrase.HeadSupports.Count > 0);
        foreach (NominalHeadSupport support in phrase.HeadSupports)
        {
            Assert.HasCount(modifiers, support.Members);
            CollectionAssert.AreEqual(phrase.ModifierTokenIndices.ToArray(), support.Members.Select(m => m.TokenIndex).ToArray());
            Assert.IsNotNull(support.Reading); Assert.IsNull(support.GraphReading);
            Assert.AreEqual(phrase.HeadTokenIndex, support.Reading.Value.TokenIndex);
            foreach (NominalMemberSupport member in support.Members)
            { Assert.IsNull(member.ReplacementReadings); Assert.IsEmpty(member.GraphReadings); }
        }
        if (assessment == NominalAgreementAssessment.Mismatch)
            Assert.IsTrue(phrase.HeadSupports.All(h => h.Members[0].Assessment == NominalAgreementAssessment.Mismatch));
        SameOldText(before, actual);
    }

    [TestMethod]
    [DataRow("Стара къща и синя ограда се рушат.")]
    [DataRow("Стара къща пустее, а синя ограда пада.")]
    public void Separate_heads_keep_separate_nominal_cores(string text)
    {
        TextAnalysis actual = session.AnalyzeText(text, false, NominalOptions, default);
        TextNominalPhrase[] cores = actual.NominalConstructions!.Phrases.Where(p =>
            actual.Tokens[p.HeadTokenIndex].Text is "къща" or "ограда").ToArray();
        Assert.HasCount(2, cores);
        foreach (TextNominalPhrase phrase in cores)
        { Assert.HasCount(1, phrase.ModifierTokenIndices); Assert.IsEmpty(phrase.ConnectorTokenIndices); }
        Assert.IsTrue(cores[0].Start + cores[0].Length < cores[1].Start);
    }

    [TestMethod]
    public void Nominal_prefix_and_prepositional_attachment_do_not_invent_members()
    {
        Assert.IsEmpty(session.AnalyzeText("Стара, изоставена", false, NominalOptions, default).NominalConstructions!.Phrases);
        TextAnalysis actual = session.AnalyzeText("Старата къща на Иван пустее.", false, NominalOptions, default);
        TextNominalPhrase core = actual.NominalConstructions!.Phrases.Single(p => actual.Tokens[p.HeadTokenIndex].Text == "къща");
        CollectionAssert.AreEqual(new[] { 0 }, core.ModifierTokenIndices.ToArray());
        Assert.AreEqual(0, core.Start); Assert.AreEqual(12, core.Length);
    }

    [TestMethod]
    public void Known_word_review_reprojects_candidate_readings_and_retains_original_no_change()
    {
        const string text = "Рисувам сипя река.";
        TextAnalysis before = session.AnalyzeText(text);
        TextAnalysis actual = session.AnalyzeText(text, false, NominalOptions, default);
        NominalRepairReview review = actual.NominalConstructions!.Reviews.Single();
        Assert.IsTrue(review.NoChangeRetained); Assert.IsFalse(review.AllPackageAlternativesExamined);
        Assert.AreEqual((1, 8, 4, "finite+NP", 1, 10), (review.TokenIndex, review.Start, review.Length,
            review.Pattern, review.MaxDistance, review.Limit));
        CollectionAssert.AreEqual(new[] { "сипя", "кипя", "сивя", "силя", "синя", "сип", "сипа", "сипе", "сипея", "сипи" },
            review.Alternatives.Select(a => a.Alternative.Form).ToArray());
        NominalRepairAlternative blue = review.Alternatives[4];
        Assert.AreEqual(1, blue.Alternative.Distance); Assert.HasCount(4, blue.Alternative.Readings);
        TextNominalPhrase phrase = blue.Phrase!; Assert.IsNotNull(phrase);
        Assert.AreEqual(NominalAgreementAssessment.Compatible, phrase.Assessment);
        NominalHeadSupport support = phrase.HeadSupports.Single();
        Assert.AreEqual(new TextReadingReference(2, 2), support.Reading);
        NominalMemberSupport modifier = support.Members.Single();
        Assert.IsEmpty(modifier.Readings); Assert.IsEmpty(modifier.GraphReadings);
        CollectionAssert.AreEqual(new[] { 0 }, modifier.ReplacementReadings!.ToArray());
        Assert.AreEqual(MorphologicalPartOfSpeech.Adjective, blue.Alternative.Readings[0].Features.PartOfSpeech);
        Assert.IsTrue(actual.ReadingInventory!.Sets[1].ResolvedReadings.All(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Verb));
        SpellingWordEvidence original = actual.Spelling!.Words.Single(w => w.TokenIndex == 1);
        Assert.AreEqual(SpellingSearchDisposition.NotRequestedKnown, original.Search);
        Assert.IsEmpty(original.Alternatives); Assert.IsNull(original.ContextualCandidates);
        SameOldText(before, actual);
    }

    [TestMethod]
    [DataRow("Рисувам синя река.")][DataRow("Сипя вода.")][DataRow("Рисувам река.")]
    [DataRow("Искам да сипя вода.")][DataRow("Рисувам сипя море.")]
    [DataRow("Рисувам, сипя река.")][DataRow("„Рисувам сипя река.“")]
    [DataRow("42Рисувам сипя река.")][DataRow("#Рисувам сипя река.")][DataRow("`Рисувам сипя река.")]
    public void Valid_or_unsupported_contrasts_do_not_gain_known_word_advice(string text) =>
        Assert.IsEmpty(session.AnalyzeText(text, false, NominalOptions, default).NominalConstructions!.Reviews);

    [TestMethod]
    public void Duplicate_known_words_keep_occurrence_local_utf16_ranges()
    {
        const string text = "🙂 Рисувам сипя река. Рисувам сипя река.";
        TextAnalysis actual = session.AnalyzeText(text, false, NominalOptions, default);
        var reviews = actual.NominalConstructions!.Reviews;
        Assert.HasCount(2, reviews);
        CollectionAssert.AreEqual(new[] { 11, 30 }, reviews.Select(r => r.Start).ToArray());
        Assert.IsTrue(reviews.All(r => r.Length == 4 && text.Substring(r.Start, r.Length) == "сипя"));
        Assert.AreNotEqual(reviews[0].TokenIndex, reviews[1].TokenIndex);
        NominalRepairReview second = reviews[1];
        Assert.AreEqual("🙂 Рисувам сипя река. Рисувам синя река.", text.Remove(second.Start, second.Length).Insert(second.Start, "синя"));
    }

    [TestMethod]
    [DataRow("Суперхубав супертрактор работи.", true, true)]
    [DataRow("Новият супертрактор работи.", false, true)]
    [DataRow("Суперхубав трактор работи.", true, false)]
    public void Licensed_graph_and_mixed_nominal_cores_keep_each_original_origin(string text, bool graphModifier, bool graphHead)
    {
        TextAnalysis before = session.AnalyzeText(text);
        TextAnalysis actual = session.AnalyzeText(text, false, NominalOptions, default);
        TextNominalPhrase phrase = actual.NominalConstructions!.Phrases.Single(p => p.HeadTokenIndex == 1);
        Assert.AreEqual(NominalAgreementAssessment.Compatible, phrase.Assessment);
        CollectionAssert.AreEqual(new[] { 0 }, phrase.ModifierTokenIndices.ToArray());
        Assert.IsTrue(phrase.HeadSupports.Count > 0);
        foreach (NominalHeadSupport support in phrase.HeadSupports)
        {
            Assert.AreEqual(graphHead, support.GraphReading.HasValue);
            Assert.AreEqual(!graphHead, support.Reading.HasValue);
            if (support.GraphReading is { } head) CheckGraph(head);
            NominalMemberSupport modifier = support.Members.Single();
            Assert.AreEqual(graphModifier, modifier.GraphReadings.Count > 0);
            Assert.AreEqual(!graphModifier, modifier.Readings.Count > 0);
            Assert.IsNull(modifier.ReplacementReadings);
            foreach (TextGraphReadingReference reference in modifier.GraphReadings) CheckGraph(reference);
        }
        SameOldText(before, actual);

        void CheckGraph(TextGraphReadingReference reference)
        {
            WordAnalysis word = actual.Tokens[reference.TokenIndex].Analysis!;
            TextReadingOccurrence occurrence = actual.ReadingInventory!.Occurrences.Single(o => o.TokenIndex == reference.TokenIndex);
            Assert.IsEmpty(actual.ReadingInventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings);
            Assert.IsFalse(word.GraphSearchIncomplete); Assert.IsTrue(word.GraphExactRootCandidatesComplete);
            WordDerivation path = word.GraphCandidates[reference.GraphCandidateIndex];
            Assert.IsFalse(path.IsConditional); Assert.IsFalse(path.UnmetExactSelfReadingRequirements?.Count > 0);
            Assert.AreEqual(path.FullTags![reference.FullTagIndex], path.Features[reference.FullTagIndex].RawTag);
        }
    }

    private static void SameOldText(TextAnalysis before, TextAnalysis actual)
    {
        using JsonDocument left = JsonDocument.Parse(JsonSerializer.Serialize(before));
        using JsonDocument right = JsonDocument.Parse(JsonSerializer.Serialize(actual with { NominalConstructions = null }));
        Assert.AreEqual(left.RootElement.GetRawText(), right.RootElement.GetRawText(),
            "Opt-in advice must preserve every existing reading, context/syntax role, warning, spelling and ordering.");
        Assert.IsNull(before.NominalConstructions);
    }
}
