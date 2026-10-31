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
}
