using System.Text.Json;
using BgLang.Contracts;
using BgLang.Tests.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

[TestClass, DoNotParallelize]
public sealed class GraphStressSuccessorPackageTests
{
    // Source-declared UNEXECUTED; the same independent unknown case runs against explicit Baseline or canonical successor.
    [TestMethod]
    public void Selected_graph_stress_profile_preserves_unknown_owner_path_and_anchor_only_evidence()
    {
        using var session = GraphStressAcceptance.Open();
        GraphStressAcceptance.AssertAll(session);
    }

    [TestMethod]
    [DataRow("колажист", 8, DisplayName = "GraphStress_nominal_plain")]
    [DataRow("колажи́ст", 9, DisplayName = "GraphStress_nominal_combining_acute")]
    public void Nominal_opt_in_keeps_plain_and_combining_acute_graph_support_in_original_coordinates(string surface, int length)
    {
        using var session = GraphStressAcceptance.Open();
        string text = "😀 " + surface + "!";
        var before = session.AnalyzeText(text);
        var actual = session.AnalyzeText(text, false, new TextContextOptions { IncludeNominalConstructions = true }, default);
        GraphStressAcceptance.AssertNominal(actual, surface, length);
        Assert.AreEqual(JsonSerializer.Serialize(before), JsonSerializer.Serialize(actual with { NominalConstructions = null }));
    }
}
