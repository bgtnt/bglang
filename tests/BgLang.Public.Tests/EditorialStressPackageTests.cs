using BgLang.Api;
using BgLang.Tests.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

// Legacy PublicPackageTests keep their exact Baseline binding; this separate mandatory gate validates the recipe target.
[TestClass, DoNotParallelize]
public sealed class EditorialStressPackageTests
{
    private static BulgarianLanguageSession session = null!;
    [ClassInitialize]
    public static void OpenExactEditorialTarget(TestContext _) => session = EditorialStressAcceptance.Open();
    [ClassCleanup]
    public static void DisposeTarget() => session.Dispose();

    [TestMethod]
    public void Complete_editorial_target_preserves_52_normative_cases_five_unreviewed_cells_generation_and_selection_controls() =>
        EditorialStressAcceptance.AssertAll(session);

    [TestMethod]
    public void Editorial_target_disposal_rejects_calls_without_relabeling_a_package_free_result()
    {
        BulgarianLanguageSession disposed = EditorialStressAcceptance.Open();
        disposed.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => disposed.GetStressEvidence("чета"));
        Assert.ThrowsExactly<ObjectDisposedException>(() => disposed.GenerateStressForms(EditorialStressAcceptance.Owner, "чета"));
    }
}
