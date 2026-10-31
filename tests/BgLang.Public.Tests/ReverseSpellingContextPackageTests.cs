using System.Text.Json;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

public sealed partial class PublicPackageTests
{
    // Report 7.4 supplies the noun example; raw candidate preservation is checked independently.
    [TestMethod]
    [DataRow("Нова кнгиа.", DisplayName = "reverse-package-01")][DataRow("Нова кнгиа", DisplayName = "reverse-package-02")][DataRow("„Нова кнгиа.“", DisplayName = "reverse-package-03")][DataRow("😀 Нова кнгиа.", DisplayName = "reverse-package-04")]
    public void Reverse_spelling_package_context_keeps_original_pool_and_typed_noun_support(string text)
    {
        Assert.AreEqual("bglang-neutral:36b0b69685d6d3e9", session.Provenance.PackageId);
        Assert.AreEqual("451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7", session.Provenance.ManifestSha256.ToLowerInvariant());
        IReadOnlyList<FuzzyMatch> before = session.FindSimilar("кнгиа", 2, 10);
        foreach (string expected in new[] { "книга" })
            Assert.IsTrue(before.Any(a => StringComparer.Ordinal.Equals(a.Form, expected)), "Preserve a failure if the reported noun candidate cannot be reproduced: " + expected);
        TextAnalysis actual = session.AnalyzeText(text);
        SpellingWordEvidence word = actual.Spelling!.Words.Single(w => w.NormalizedQuery == "кнгиа");
        Assert.AreEqual(SpellingSearchDisposition.Completed, word.Search);
        Assert.AreEqual(text, actual.Text); Assert.AreEqual("кнгиа", text.Substring(word.Start, word.Length));
        Assert.AreEqual(JsonSerializer.Serialize(before), JsonSerializer.Serialize(word.Alternatives));
        Assert.IsNotNull(word.ContextualCandidates); Assert.HasCount(before.Count, word.ContextualCandidates);
        CollectionAssert.AreEquivalent(Enumerable.Range(0, before.Count).ToArray(), word.ContextualCandidates.Select(c => c.AlternativeIndex).ToArray());
        foreach (string expected in new[] { "книга" })
        {
            int index = Enumerable.Range(0, before.Count).Single(i => StringComparer.Ordinal.Equals(before[i].Form, expected));
            SpellingContextCandidate candidate = word.ContextualCandidates.Single(c => c.AlternativeIndex == index);
            Assert.AreEqual(SpellingContextAssessment.Supported, candidate.Assessment, expected);
            Assert.IsEmpty(candidate.Matches, "No preceding adjective may be labelled FollowingNounReading.");
            Assert.IsNotNull(candidate.PreviousModifierMatches); Assert.IsTrue(candidate.PreviousModifierMatches.Count > 0);
            foreach (SpellingPreviousModifierMatch match in candidate.PreviousModifierMatches)
            {
                Assert.AreEqual(word.TokenIndex - 1, match.PreviousModifierReading.TokenIndex);
                TokenAnalysis modifier = actual.Tokens[match.PreviousModifierReading.TokenIndex]; Assert.AreEqual("Нова", modifier.Text);
                TextReadingInventory inventory = actual.ReadingInventory!;
                TextReadingSet set = inventory.Sets[inventory.Occurrences.Single(o => o.TokenIndex == match.PreviousModifierReading.TokenIndex).ReadingSetIndex];
                MorphologicalFeatures adjective = set.ResolvedReadings[match.PreviousModifierReading.CompleteReadingIndex].Features;
                MorphologicalFeatures noun = word.Alternatives[index].Readings[match.CandidateReadingIndex].Features;
                Assert.AreEqual(MorphologicalPartOfSpeech.Adjective, adjective.PartOfSpeech);
                Assert.AreEqual(MorphologicalPartOfSpeech.Noun, noun.PartOfSpeech);
                Assert.AreEqual(MorphologicalNumber.Singular, adjective.Number); Assert.AreEqual(adjective.Number, noun.Number);
                Assert.AreEqual(MorphologicalGender.Feminine, adjective.Gender); Assert.AreEqual(adjective.Gender, noun.Gender);
            }
        }
        Assert.AreEqual(JsonSerializer.Serialize(before), JsonSerializer.Serialize(session.FindSimilar("кнгиа", 2, 10)));
        Assert.IsTrue(word.ContextualCandidates.Zip(word.ContextualCandidates.Skip(1)).All(pair =>
            word.Alternatives[pair.First.AlternativeIndex].Distance <= word.Alternatives[pair.Second.AlternativeIndex].Distance));
    }

    [TestMethod]
    public void Reverse_spelling_package_keeps_known_words_and_boundary_abstention()
    {
        TextAnalysis known = session.AnalyzeText("Нова книга.");
        Assert.IsTrue(known.Spelling!.Words.All(w => w.Search == SpellingSearchDisposition.NotRequestedKnown &&
            w.Alternatives.Count == 0 && w.ContextualCandidates is null));
        foreach (string text in new[] { "Нова, кнгиа.", "Нова. Кнгиа." })
        {
            TextAnalysis actual = session.AnalyzeText(text);
            SpellingWordEvidence word = actual.Spelling!.Words.Single(w => w.NormalizedQuery == "кнгиа");
            Assert.IsNull(word.ContextualCandidates); Assert.AreEqual(text, actual.Text);
        }
    }
}
