using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

public sealed partial class PublicPackageTests
{
    [TestMethod]
    [DataRow("Говоря с учителят.", "учителят", "учителя")]
    [DataRow("Говоря с добрият учител.", "добрият", "добрия")]
    [DataRow("Помагам на ученикът.", "ученикът", "ученика")]
    [DataRow("🙂 Говоря с учителят. После говоря с учителя.", "учителят", "учителя")]
    public void Prepositional_article_has_exact_candidate_and_preserves_other_occurrences(string text, string carrier, string replacement)
    {
        TextAnalysis actual = session.AnalyzeText(text);
        TextDiagnostic[] warnings = actual.Diagnostics.Where(d => d.Code == "short_article_expected_after_preposition_candidate").ToArray();
        string candidates = string.Join(";", actual.Syntax!.Sentences.SelectMany(s => s.Candidates).Select(c =>
            $"{c.Role}:{c.RuleId}:{c.Start}+{c.Length}:context={string.Join(',', c.Context.Select(r => r.TokenIndex))}"));
        string syntaxState = $"available={actual.Syntax.Available};reason={actual.Syntax.UnavailableReason};sentences=" +
            string.Join(";", actual.Syntax.Sentences.Select(s => $"{s.Status}/{s.Route}/{s.Candidates.Count}"));
        string readings = string.Join(";", actual.Tokens.Where(t => t.Kind == TokenKind.Word).Select(t =>
            t.Text + "=[" + string.Join('|', t.Analysis!.Readings.Select(r =>
                $"{r.Lemma}/{r.PartOfSpeech}/{r.Features.PartOfSpeech}/{r.Features.VerbForm}/{r.Features.Tense}/{r.Features.Person}/{r.Features.Number}")) + "]"));
        string counterpart = string.Join("|", session.Lookup("ученика", LookupMode.Exact, 256).Select(r =>
            $"{r.Lemma}/{r.PartOfSpeech}/{r.Features.PartOfSpeech}/{r.Features.Article}/{r.Features.Number}/{r.Features.Gender}/{r.Features.ParadigmId}/{r.LegacyTypeId}/{r.LegacyBaseId}/{r.Source}"));
        Assert.HasCount(1, warnings, $"{text}; {syntaxState}; candidates={candidates}; readings={readings}; counterpart={counterpart}; diagnostics={string.Join(',', actual.Diagnostics.Select(d => d.Code))}");
        TextDiagnostic warning = warnings[0];
        Assert.AreEqual(text.IndexOf(carrier, StringComparison.Ordinal), warning.Start);
        Assert.AreEqual(carrier.Length, warning.Length); Assert.AreEqual(replacement, warning.ProjectedText);
        Assert.IsNotNull(warning.Readings); Assert.IsNull(warning.GraphReadings);
        Assert.IsFalse(actual.Diagnostics.Any(d => d.Code == "full_short_article_check_not_ready" && d.Start == warning.Start && d.Length == warning.Length));
        CollectionAssert.AreEqual(warning.Readings.ToArray(), actual.Validation!.Diagnostics.Single(d => d.Code == warning.Code).Supports.ToArray());
        if (text.StartsWith("🙂", StringComparison.Ordinal))
        {
            int validStart = text.LastIndexOf("учителя", StringComparison.Ordinal);
            Assert.IsTrue(actual.Validation.Diagnostics.Any(d => d.Code == "article_prepositional_group_locally_assessed" &&
                d.Start == validStart && d.Length == "учителя".Length),
                "The exact short/full counterpart locally assesses the separate valid occurrence.");
            Assert.IsFalse(actual.Diagnostics.Any(d => d.Code == "full_short_article_check_not_ready" && d.Start == validStart));
        }
    }

    [TestMethod]
    [DataRow("Говоря с учителя.", "учителя")]
    [DataRow("Говоря с добрия учител.", "добрия")]
    [DataRow("Учителят пристигна.", "Учителят")]
    [DataRow("Учителя го видях.", "Учителя")]
    [DataRow("Два стола стоят.", "стола")]
    [DataRow("Търся учител.", "учител")]
    [DataRow("Малкият, ела тук!", "Малкият")]
    [DataRow("Апостола пристигна.", "Апостола")]
    [DataRow("Надписът „Говоря с учителят“ е преписан дословно.", "учителят")]
    [DataRow("Надписът „Говоря с учителят“ е преписан дословно.", "Надписът")]
    [DataRow("Говоря с", "Говоря с")]
    public void Prepositional_article_does_not_invent_rewrites_outside_declared_scope(string text, string protectedSurface)
    {
        TextAnalysis actual = session.AnalyzeText(text);
        Assert.IsFalse(actual.Diagnostics.Any(d => d.Code == "short_article_expected_after_preposition_candidate"));
        int protectedStart = text.IndexOf(protectedSurface, StringComparison.Ordinal);
        Assert.IsTrue(protectedStart >= 0);
        int protectedEnd = protectedStart + protectedSurface.Length;
        static bool IsArticleMismatch(string code) => code is
            "short_article_expected_after_preposition_candidate" or "full_article_expected_for_subject_candidate";
        Assert.IsFalse(actual.Diagnostics.Any(d => IsArticleMismatch(d.Code) &&
            d.Start < protectedEnd && protectedStart < d.Start + d.Length), text);
        Assert.IsNotNull(actual.Validation);
        Assert.IsTrue(actual.Validation.Available);
        Assert.IsFalse(actual.Validation.Diagnostics.Any(d => IsArticleMismatch(d.Code) &&
            d.Start < protectedEnd && protectedStart < d.Start + d.Length), text);
    }
}
