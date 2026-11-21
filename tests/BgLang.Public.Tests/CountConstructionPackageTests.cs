using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

public sealed partial class PublicPackageTests
{
    // UNEXECUTED proposal. Must run against the strictly bound complete package after integration.
    [TestMethod]
    [DataRow("‚Купих два столове.", "", DisplayName = "CountQuote.Public.LowSingleFinite")]
    [DataRow("‚Цитат. Купих два столове.", "", DisplayName = "CountQuote.Public.LowSingleContinuation")]
    [DataRow("Купих два столове.", "quantified_noun_form_mismatch|10|7|стола", DisplayName = "CountQuote.Public.UnquotedControl")]
    [DataRow("‚два столове.", "", DisplayName = "CountQuote.Public.NPStartControl")]
    public void Low_single_quote_preserves_document_count_abstention_and_unquoted_control(string text, string expected)
    {
        TextAnalysis result = session.AnalyzeText(text);
        CollectionAssert.AreEqual(expected.Length == 0 ? Array.Empty<string>() : new[] { expected },
            result.Diagnostics.Where(CountDiagnostic).Select(d => d.Code + "|" + d.Start + "|" + d.Length + "|" + d.ProjectedText).ToArray());
        CollectionAssert.AreEqual(result.Diagnostics.Where(CountDiagnostic).Select(d => (d.Code, d.Start, d.Length)).ToArray(),
            result.Validation!.Diagnostics.Where(d => d.Code is "personal_numeral_selection_mismatch" or "quantified_noun_form_mismatch")
                .Select(d => (d.Code, d.Start, d.Length)).ToArray());
        Assert.AreEqual(text, result.Text);
    }

    [TestMethod]
    [DataRow("Купих два столове.", "столове", "стола")]
    [DataRow("Купих три компютри.", "компютри", "компютъра")]
    [DataRow("Видях трима стола.", "трима", "три")]
    [DataRow("Купих три нови компютри.", "компютри", "компютъра")]
    [DataRow("Двата столове.", "столове", "стола")]
    [DataRow("Тримата стола.", "Тримата", "Трите")]
    public void Reviewed_count_construction_returns_exact_independent_counterpart(string text, string target, string counterpart)
    {
        TextAnalysis result = session.AnalyzeText(text);
        TextDiagnostic diagnostic = result.Diagnostics.Single(d => CountDiagnostic(d));
        Assert.AreEqual((text.IndexOf(target, StringComparison.Ordinal), target.Length, counterpart),
            (diagnostic.Start, diagnostic.Length, diagnostic.ProjectedText));
        Assert.IsNotNull(diagnostic.Readings); Assert.IsNotEmpty(diagnostic.Readings); Assert.IsNull(diagnostic.GraphReadings);
        Assert.IsNotEmpty(session.Lookup(counterpart, LookupMode.Exact, 256));
        string repaired = text.Remove(diagnostic.Start, diagnostic.Length).Insert(diagnostic.Start, counterpart);
        Assert.IsFalse(session.AnalyzeText(repaired).Diagnostics.Any(CountDiagnostic));
    }

    [TestMethod]
    public void Person_count_reports_both_targets_and_joint_repair_is_clean()
    {
        const string text = "Дойдоха два ученика.";
        var diagnostics = session.AnalyzeText(text).Diagnostics.Where(CountDiagnostic).ToArray();
        CollectionAssert.AreEqual(new[] { (8, 3, "двама"), (12, 7, "ученици") },
            diagnostics.Select(d => (d.Start, d.Length, d.ProjectedText!)).ToArray());
        Assert.IsFalse(session.AnalyzeText("Дойдоха двама ученици.").Diagnostics.Any(CountDiagnostic));
    }

    [TestMethod]
    [DataRow("Купих три нови компютъра.")][DataRow("Купих два от столовете.")]
    [DataRow("Дойдоха трийсет и три ученици.")][DataRow("Дойдоха трийсет и трима ученици.")]
    [DataRow("Тук има три офицера.")][DataRow("На шахматната дъска има три офицера.")]
    [DataRow("Тук има трима офицери от армията.")]
    [DataRow("„Купих два столове.“")][DataRow("„Цитат. Купих два столове.“")]
    [DataRow("Купих два непознати.")]
    public void Count_controls_preserve_valid_ambiguous_and_unavailable_readings(string text) =>
        Assert.IsFalse(session.AnalyzeText(text).Diagnostics.Any(CountDiagnostic));

    private static bool CountDiagnostic(TextDiagnostic diagnostic) => diagnostic.Code is
        "personal_numeral_selection_mismatch" or "quantified_noun_form_mismatch";
}
