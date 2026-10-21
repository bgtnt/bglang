using System.Text.Json;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BgLang.Web.Tests.TextCompositionHttpFixture;

namespace BgLang.Web.Tests;

// Wire-only independent literal input: no construction composer or feature parser supplies expected output.
[TestClass]
public sealed class ConstructionResponseTests
{
    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public void All_formats_preserve_shared_family_candidates_partial_owner_state_and_complete_anchor35(bool supportRoute)
    {
        const string tag = "Глагол:159ti,Минало_свършено_деятелно,Мъжки,1л.,Сегашно";
        WordReading raw = new("писал", "пиша", tag, .8125, 159, 100, 101, "wire-only");
        MorphologicalFeatures features = new(tag, MorphologicalPartOfSpeech.Verb, "159ti", null,
            MorphologicalNumber.Singular, MorphologicalGender.Masculine, MorphologicalTense.Past,
            MorphologicalVerbForm.Participle, MorphologicalArticle.Indefinite, false, false)
            { ParticipleKind = MorphologicalParticipleKind.PastAoristActive };
        TextReadingSnapshot basis = new(raw, features);
        TextReadingSnapshot[] ownForms = Enumerable.Repeat(basis, 36).ToArray();
        WordConstructionCandidate[] candidates =
        [
            new("typed-construction-candidate.past-indefinite.v1", "owner:A", "key:A", 35,
                basis, ["писал", "съм"], 0, "мин.неопр.вр., 1л., ед.ч., м.р."),
            new("typed-construction-candidate.renarrative-past-aorist.v1", "owner:A", "key:A", 35,
                basis, ["писал", "съм"], 0, "пр.накл., мин.св.вр., 1л., ед.ч., м.р.")
        ];
        TextReadingSnapshot auxiliary = new(new("съм", "съм", null, 1, null, null, null, "wire-only"),
            new(null, MorphologicalPartOfSpeech.Unknown, null, null, null, null, null, null, null, false, false));
        WordAnalysis displayed = new("писал", "писал", Array.AsReadOnly(ownForms.Take(32).Select(row => row.Raw).ToArray()), null);
        WordAnalysis auxDisplay = new("съм", "съм", [auxiliary.Raw], null);
        TextAnalysis text = new("😀 писал съм.",
            [new("😀", TokenKind.Symbol, 0, 2, null), new("писал", TokenKind.Word, 3, 5, displayed),
             new("съм", TokenKind.Word, 9, 3, auxDisplay), new(".", TokenKind.Punctuation, 12, 1, null)])
        {
            Sentences = [new(0, 13)],
            ReadingInventory = new([new("писал", "писал", supportRoute ? [] : ownForms,
                supportRoute ? "писал" : null, supportRoute ? ownForms : null, 32),
                new("съм", "съм", [auxiliary], null, null, 1)],
                [new(1, "писал", 3, 5, 0), new(2, "съм", 9, 3, 1)]),
            WordConstructions = new("word-constructions.v1", false,
                [new("owner:A", "key:A", WordConstructionStatus.Ready, ownForms),
                 new(null, "key:B", WordConstructionStatus.OwnFormsUnavailable, null)],
                candidates, [new(0, 3, 9, new(1, 35)), new(1, 3, 9, new(1, 35))], [new(2, 0)])
        };
        foreach (string version in new[] { "1", "2" })
        foreach (string format in new[] { HttpContract.RawFormat, SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat })
        {
            using JsonDocument encoded = JsonDocument.Parse(Encode(Envelope(text, version), format));
            using JsonDocument restored = Restore(encoded);
            JsonElement data = restored.RootElement.GetProperty("data");
            Assert.IsTrue(SharedDonorResponseTests.SameJson(JsonSerializer.SerializeToElement(text, HttpContract.Json), data));
            JsonElement evidence = data.GetProperty("wordConstructions");
            Assert.IsFalse(evidence.GetProperty("complete").GetBoolean());
            Assert.IsFalse(evidence.GetProperty("productiveReplayAvailable").GetBoolean());
            Assert.AreEqual("OriginalGenerationSeedNotRetained", evidence.GetProperty("productiveReplayUnavailableReason").GetString());
            JsonElement owners = evidence.GetProperty("owners");
            Assert.AreEqual("Ready", owners[0].GetProperty("status").GetString()); Assert.AreEqual(36, owners[0].GetProperty("ownForms").GetArrayLength());
            Assert.AreEqual("OwnFormsUnavailable", owners[1].GetProperty("status").GetString());
            Assert.AreEqual(JsonValueKind.Null, owners[1].GetProperty("entryId").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, owners[1].GetProperty("ownForms").ValueKind);
            Assert.AreEqual(0, evidence.GetProperty("unavailableTokenIndices").GetArrayLength());
            JsonElement unavailable = evidence.GetProperty("unavailableReadings")[0];
            Assert.AreEqual(2, unavailable.GetProperty("tokenIndex").GetInt32()); Assert.AreEqual(0, unavailable.GetProperty("completeReadingIndex").GetInt32());
            JsonElement set = data.GetProperty("readingInventory").GetProperty("sets")[0];
            JsonElement rows = set.GetProperty(supportRoute ? "supportReadings" : "canonicalReadings");
            Assert.AreEqual(36, rows.GetArrayLength()); Assert.IsTrue(set.GetProperty("displayTruncated").GetBoolean());
            Assert.AreEqual(32, set.GetProperty("displayedReadingCount").GetInt32());
            Assert.AreEqual(supportRoute ? JsonValueKind.Array : JsonValueKind.Null, set.GetProperty("supportReadings").ValueKind);
            JsonElement[] actualCandidates = evidence.GetProperty("candidates").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(candidates.Select(candidate => candidate.FamilyId).ToArray(),
                actualCandidates.Select(candidate => candidate.GetProperty("familyId").GetString()).ToArray());
            CollectionAssert.AreEqual(candidates.Select(candidate => candidate.TargetConstructionTag).ToArray(),
                actualCandidates.Select(candidate => candidate.GetProperty("targetConstructionTag").GetString()).ToArray());
            foreach (JsonElement candidate in actualCandidates)
            {
                Assert.AreEqual("owner:A", candidate.GetProperty("entryId").GetString()); Assert.AreEqual("key:A", candidate.GetProperty("lexemeKey").GetString());
                Assert.AreEqual(35, candidate.GetProperty("ownFormIndex").GetInt32()); Assert.AreEqual(0, candidate.GetProperty("basePartIndex").GetInt32());
                Assert.IsFalse(candidate.GetProperty("acceptedWordForm").GetBoolean()); Assert.IsFalse(candidate.GetProperty("grammarAuthority").GetBoolean());
                Assert.IsTrue(SharedDonorResponseTests.SameJson(rows[35], candidate.GetProperty("baseForm")));
                Assert.IsTrue(SharedDonorResponseTests.SameJson(owners[0].GetProperty("ownForms")[35], candidate.GetProperty("baseForm")));
                CollectionAssert.AreEqual(new[] { "писал", "съм" }, candidate.GetProperty("parts").EnumerateArray().Select(value => value.GetString()).ToArray());
                Assert.AreEqual(.8125, candidate.GetProperty("baseForm").GetProperty("raw").GetProperty("score").GetDouble());
                Assert.IsFalse(candidate.TryGetProperty("ruleId", out _));
            }
            JsonElement[] matches = evidence.GetProperty("matches").EnumerateArray().ToArray();
            Assert.AreEqual(2, matches.Length);
            CollectionAssert.AreEqual(new[] { 0, 1 }, matches.Select(match => match.GetProperty("candidateIndex").GetInt32()).ToArray());
            foreach (JsonElement match in matches)
            {
                Assert.AreEqual(3, match.GetProperty("start").GetInt32()); Assert.AreEqual(9, match.GetProperty("length").GetInt32());
                Assert.AreEqual("писал съм", text.Text.Substring(3, 9));
                Assert.AreEqual(1, match.GetProperty("anchor").GetProperty("tokenIndex").GetInt32());
                Assert.AreEqual(35, match.GetProperty("anchor").GetProperty("completeReadingIndex").GetInt32());
            }
            Assert.AreNotEqual(JsonValueKind.True, encoded.RootElement.GetProperty("coverage").GetProperty("complete").ValueKind);
        }
    }
}
