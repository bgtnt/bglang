using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

public sealed partial class PublicPackageTests
{
    [TestMethod]
    [DataRow("Книгата е върху.", "върху", 10, 5, DisplayName = "PrepReportPackage.literal.vurhu")]
    [DataRow("Минах покрай.", "покрай", 6, 6, DisplayName = "PrepReportPackage.literal.pokrai")]
    [DataRow("Застанах срещу.", "срещу", 9, 5, DisplayName = "PrepReportPackage.literal.sreshtu")]
    public void Report_terminal_preposition_returns_exact_advice_from_complete_package(
        string text, string surface, int start, int length)
    {
        RequirePrepositionReportBaseline();
        string before = System.Text.Json.JsonSerializer.Serialize(session.Lookup(surface, LookupMode.Exact, 256));
        TextAnalysis result = session.AnalyzeText(text);
        TextDiagnostic[] diagnostics = result.Diagnostics.Where(d => d.Code == "preposition_missing_complement").ToArray();
        Assert.HasCount(1, diagnostics, text);
        Assert.AreEqual((start, length, "preposition_complement"),
            (diagnostics[0].Start, diagnostics[0].Length, diagnostics[0].RuleId), text);
        Assert.IsNull(diagnostics[0].ProjectedText, text);
        Assert.AreEqual(text, result.Text);
        Assert.AreEqual(before, System.Text.Json.JsonSerializer.Serialize(session.Lookup(surface, LookupMode.Exact, 256)));
    }

    [TestMethod]
    [DataRow("Книгата е върху масата.", DisplayName = "PrepReportPackage.control.vurhu-complement")]
    [DataRow("Минах покрай къщата.", DisplayName = "PrepReportPackage.control.pokrai-complement")]
    [DataRow("Застанах срещу Иван.", DisplayName = "PrepReportPackage.control.sreshtu-complement")]
    [DataRow("Думата „върху“ е предлог.", DisplayName = "PrepReportPackage.control.vurhu-quoted-term")]
    [DataRow("Думата „покрай“ е предлог.", DisplayName = "PrepReportPackage.control.pokrai-quoted-term")]
    [DataRow("Думата „срещу“ е предлог.", DisplayName = "PrepReportPackage.control.sreshtu-quoted-term")]
    [DataRow("Книгата е върху...", DisplayName = "PrepReportPackage.control.vurhu-dots")]
    [DataRow("Минах покрай...", DisplayName = "PrepReportPackage.control.pokrai-dots")]
    [DataRow("Застанах срещу...", DisplayName = "PrepReportPackage.control.sreshtu-dots")]
    [DataRow("Под или върху?", DisplayName = "PrepReportPackage.control.vurhu-choice")]
    [DataRow("До или покрай?", DisplayName = "PrepReportPackage.control.pokrai-choice")]
    [DataRow("За или срещу?", DisplayName = "PrepReportPackage.control.sreshtu-choice")]
    public void Report_preposition_controls_preserve_complements_quotes_ellipses_and_choices(string text)
    {
        RequirePrepositionReportBaseline();
        TextAnalysis result = session.AnalyzeText(text);
        Assert.AreEqual(text, result.Text);
        Assert.IsFalse(result.Diagnostics.Any(d => d.Code == "preposition_missing_complement"), text);
    }

    private static void RequirePrepositionReportBaseline()
    {
        Assert.AreEqual("bglang-neutral:36b0b69685d6d3e9", candidate.GetProperty("packageId").GetString());
        Assert.AreEqual("bglang-neutral:36b0b69685d6d3e9", session.Provenance.PackageId);
        Assert.AreEqual("451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7",
            session.Provenance.ManifestSha256.ToLowerInvariant());
        string? selected = Environment.GetEnvironmentVariable("BGLANG_PUBLIC_PACKAGE_MANIFEST");
        Assert.IsFalse(string.IsNullOrWhiteSpace(selected), "The strictly bound complete Baseline package is required; this gate never skips.");
        Assert.AreEqual(Path.GetFullPath(selected!), manifest, true);
    }
}
