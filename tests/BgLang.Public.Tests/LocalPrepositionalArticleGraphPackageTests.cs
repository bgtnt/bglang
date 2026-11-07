using BgLang.Api;
using BgLang.Contracts;
using BgLang.Tests.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

[TestClass, DoNotParallelize]
public sealed class LocalPrepositionalArticleGraphPackageTests
{
    private const string Warning = "short_article_expected_after_preposition_candidate";
    private const string LocalFact = "article_prepositional_group_locally_assessed";
    private const string Gap = "full_short_article_check_not_ready";
    private static BulgarianLanguageSession session = null!;

    [ClassInitialize]
    public static void Open(TestContext _) => session = GraphStressAcceptance.Open();

    [ClassCleanup]
    public static void Close() => session.Dispose();

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Говоря {0}.", "с", "учителят", "учителя", "")]
    [DataRow("{0} говоря.", "С", "учителят", "учителя", "")]
    [DataRow("{0}.", "С", "учителят", "учителя", "")]
    [DataRow("Говоря {0} и пиша.", "с", "учителят", "учителя", "")]
    [DataRow("В задънена улица говорех {0}.", "с", "учителят", "учителя", "")]
    [DataRow("Говоря {0}, а той чете „Под игото“.", "с", "учителят", "учителя", "")]
    [DataRow("„Под игото“ прочетох и после говорих {0}.", "с", "учителят", "учителя", "")]
    [DataRow("🙂 Говоря {0} Иван Петров.", "с", "учителят", "учителя", "")]
    [DataRow("Помагам {0}.", "на", "ученикът", "ученика", "")]
    [DataRow("{0} помагам.", "На", "ученикът", "ученика", "")]
    [DataRow("Говоря {0}.", "с", "добрият", "добрия", " учител")]
    [DataRow("Говоря {0}.", "с", "добрият", "добрия", " млад учител")]
    [DataRow("В задънена улица попадна и делото за убийството {0} Георги Сивков.", "на", "наркодилърът", "наркодилъра", "")]
    public void Local_result_is_independent_of_predicates_and_unrelated_text(
        string frame, string preposition, string full, string shortForm, string tail) =>
        CheckLocalPair(frame, preposition, full, shortForm, tail);

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Боята се разля {0}.", "по", " стол")]
    [DataRow("Стоях {0}.", "до", ", изоставен стол")]
    public void Measured_common_head_keeps_simple_and_comma_bridged_modifiers(string frame, string preposition, string tail) =>
        CheckLocalPair(frame, preposition, "старият", "стария", tail);

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Минах по старият мост.")]
    [DataRow("Стоях до старият, изоставен мост.")]
    public void Original_place_name_alternative_at_the_head_preserves_abstention(string text)
    {
        TextAnalysis analysis = session.AnalyzeText(text);
        TextReadingInventory inventory = analysis.ReadingInventory!;
        var occurrence = inventory.Occurrences.Single(row => row.Text == "мост");
        var rows = inventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings;
        Assert.IsTrue(rows.Any(row => row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun));
        Assert.IsTrue(rows.Any(row => row.Features is { PartOfSpeech: MorphologicalPartOfSpeech.ProperNoun,
            NameKind: MorphologicalNameKind.PlaceName }));
        Assert.IsFalse(analysis.Diagnostics.Any(row => row.Code == Warning), text);
        Assert.IsFalse(analysis.Validation!.Diagnostics.Any(row => row.Code == LocalFact), text);
    }

    private static void CheckLocalPair(string frame, string preposition, string full, string shortForm, string tail)
    {
        Check(full, expectedWarning: true);
        Check(shortForm, expectedWarning: false);

        void Check(string carrier, bool expectedWarning)
        {
            string pp = preposition + " " + carrier + tail;
            string text = frame.Replace("{0}", pp, StringComparison.Ordinal);
            int ppStart = text.IndexOf(pp, StringComparison.Ordinal);
            int start = ppStart + preposition.Length + 1;
            TextAnalysis analysis = session.AnalyzeText(text);
            var warnings = analysis.Diagnostics.Where(row => row.Code == Warning).ToArray();
            var facts = analysis.Validation!.Diagnostics.Where(row => row.Code == LocalFact).ToArray();
            IReadOnlyList<TextReadingReference> supports;
            if (expectedWarning)
            {
                Assert.HasCount(1, warnings, text);
                Assert.IsEmpty(facts, text);
                TextDiagnostic warning = warnings[0];
                Assert.AreEqual((start, carrier.Length, shortForm), (warning.Start, warning.Length, warning.ProjectedText));
                Assert.IsNull(warning.GraphReadings);
                supports = warning.Readings!;
                var mirror = analysis.Validation.Diagnostics.Single(row => row.Code == Warning);
                CollectionAssert.AreEqual(supports.ToArray(), mirror.Supports.ToArray());
            }
            else
            {
                Assert.IsEmpty(warnings, text);
                Assert.HasCount(1, facts, text);
                Assert.AreEqual((start, carrier.Length), (facts[0].Start, facts[0].Length));
                StringAssert.Contains(facts[0].Basis, "exact_inverse_full_counterpart_roundtrip");
                supports = facts[0].Supports;
            }
            Assert.AreEqual(text, analysis.Text);
            Assert.IsFalse(analysis.Diagnostics.Any(row => row.Code == Gap && row.Start == start));
            TextReadingInventory inventory = analysis.ReadingInventory!;
            var occurrences = inventory.Occurrences.ToDictionary(row => row.TokenIndex);
            int[] ppTokens = inventory.Occurrences.Where(row => row.Start >= ppStart && row.Start + row.Length <= ppStart + pp.Length)
                .Select(row => row.TokenIndex).ToArray();
            CollectionAssert.AreEqual(ppTokens, supports.Select(row => row.TokenIndex).Distinct().ToArray());
            foreach (var reference in supports)
            {
                var occurrence = occurrences[reference.TokenIndex];
                var row = inventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings[reference.CompleteReadingIndex];
                if (occurrence.Start == ppStart)
                    Assert.AreEqual(MorphologicalPartOfSpeech.Preposition, row.Features.PartOfSpeech);
            }
        }
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Надписът „Говоря с учителят“ е дословен.")]
    [DataRow("„Пример. Говоря с учителя. Край.“")]
    [DataRow("„Пример. Говоря с учителят. Край.")]
    [DataRow("Говоря с „учителят“.")]
    [DataRow("`Говоря с учителят`")]
    [DataRow("Говоря с Учителят.")]
    [DataRow("Говоря с добрият учителят.")]
    [DataRow("Говоря с добрият учители.")]
    [DataRow("Говоря с добрият и млад учител.")]
    [DataRow("Стоях до два стола.")]
    [DataRow("Добрият учител дойде.")]
    public void Quoted_or_locally_unproved_groups_do_not_gain_article_advice(string text)
    {
        TextAnalysis analysis = session.AnalyzeText(text);
        Assert.IsFalse(analysis.Diagnostics.Any(row => row.Code == Warning), text);
        Assert.IsFalse(analysis.Validation!.Diagnostics.Any(row => row.Code is Warning or LocalFact), text);
    }

    [TestMethod, TestCategory("FullPackage")]
    public void Repeated_targets_use_distinct_original_utf16_occurrences()
    {
        const string text = "🙂 Говоря с учителят и после пак с учителят.";
        TextAnalysis analysis = session.AnalyzeText(text);
        var warnings = analysis.Diagnostics.Where(row => row.Code == Warning).ToArray();
        CollectionAssert.AreEqual(new[] { text.IndexOf("учителят", StringComparison.Ordinal), text.LastIndexOf("учителят", StringComparison.Ordinal) },
            warnings.Select(row => row.Start).ToArray());
        foreach (var warning in warnings)
        {
            Assert.AreEqual("учителят", text.Substring(warning.Start, warning.Length));
            Assert.AreEqual("учителя", warning.ProjectedText);
            Assert.AreEqual(warning.Start, analysis.Tokens[warning.Readings![^1].TokenIndex].Start);
        }
    }
}
