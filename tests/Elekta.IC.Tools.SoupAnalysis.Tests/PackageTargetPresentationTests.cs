using Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using NuGet.Versioning;

namespace Elekta.IC.Tools.SoupAnalysis.Tests;

[TestClass]
public sealed class PackageTargetPresentationTests
{
    [TestMethod]
    public void InternalCustomTargetEnablesReviewAndLocalDowngrade()
    {
        var row = Internal();
        var repository = row.Decision.Repositories.Single();
        var review = new ReviewRow(repository, row, repository.Usages[0].Repository);
        Assert.AreEqual("NotApplicable", row.Action);
        Assert.IsFalse(review.Eligible);
        Assert.AreEqual("N/A", row.ICMax);
        Assert.AreEqual("N/A", row.NuGetMax);
        row.CustomVersionText = "0.5.0-rc.1";
        Assert.IsTrue(row.IsCustomTargetValid);
        Assert.IsNull(row.TargetError);
        Assert.AreEqual(TargetSource.Custom, row.SelectedTarget!.Source);
        Assert.AreEqual("0.5.0-rc.1", row.Target);
        Assert.AreEqual("Downgrade", row.Action);
        Assert.AreEqual("Downgrade", review.Action);
        Assert.IsTrue(review.Eligible);
    }

    [TestMethod]
    [DataRow("[1.0,2.0)")]
    [DataRow("1.*")]
    [DataRow("not-a-version")]
    [DataRow("")]
    public void InvalidOrEmptyCustomInputClearsPreviousTarget(string input)
    {
        var row = Internal();
        row.CustomVersionText = "2.0";
        row.CustomVersionText = input;
        Assert.IsNull(row.SelectedTarget);
        Assert.IsFalse(row.IsCustomTargetValid);
        Assert.AreEqual(input.Length == 0 ? "NotApplicable" : "Unresolved", row.Action);
        if (input.Length > 0) Assert.IsFalse(string.IsNullOrEmpty(row.TargetError));
    }

    [TestMethod]
    public void ScopeChangesPreserveCustomTargetButMakeNonICViewsReadOnly()
    {
        var row = Internal();
        row.CustomVersionText = "999.0-preview.2";
        var target = row.SelectedTarget;
        foreach (var category in new[] { RepositoryCategory.Platform, RepositoryCategory.ICEmulator, RepositoryCategory.IC })
        {
            row.SetRepositoryScope(TestData.Repo("scope", category), [], true);
            Assert.AreSame(target, row.SelectedTarget);
            Assert.AreEqual(category == RepositoryCategory.IC, row.IsCustomTargetEditable);
            Assert.IsFalse(row.IsSoupTargetEditable);
        }
        row.SetRepositoryScope(null, null, true);
        Assert.IsTrue(row.IsCustomTargetEditable);
    }

    [TestMethod]
    public void SharedTargetIsLockedAndComparisonMaximaAreNotSoup()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        var analysis = TestData.Analyze(TestData.Inventory(platform, TestData.Usage(platform, "shared", "2.0")),
            TestData.Inventory(ic, TestData.Usage(ic, "shared", "1.0")));
        var row = new PackageRow(analysis.Packages.Single());
        Assert.AreEqual("N/A", row.ICMax);
        Assert.AreEqual("N/A", row.NuGetMax);
        Assert.IsFalse(row.IsCustomTargetEditable);
        Assert.Throws<ArgumentException>(() => row.SelectedTarget = new(TargetSource.Custom, NuGetVersion.Parse("3.0")));
        Assert.AreEqual(TargetSource.PlatformMax, row.SelectedTarget!.Source);
    }

    [TestMethod]
    public void SoupRejectsPrereleaseNuGetAndCustomButAllowsPrereleaseICMax()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        var decision = TestData.Analyze(TestData.Inventory(platform),
            TestData.Inventory(ic, TestData.Usage(ic, "soup", "2.0-rc.1"))).Packages.Single();
        var row = new PackageRow(decision with
        {
            NuGetCandidate = new("soup", NuGetVersion.Parse("9.0-preview"), CandidateStatus.Available, "fixture", DateTimeOffset.UtcNow)
        });
        Assert.AreEqual("2.0.0-rc.1", row.ICMax);
        Assert.AreEqual("N/A", row.NuGetMax);
        Assert.HasCount(1, row.Choices);
        Assert.AreEqual(TargetSource.ICMax, row.Choices.Single().Source);
        Assert.Throws<ArgumentException>(() => row.SelectedTarget = new(TargetSource.NuGetCandidate, NuGetVersion.Parse("9.0-preview")));
        Assert.Throws<ArgumentException>(() => row.SelectedTarget = new(TargetSource.Custom, NuGetVersion.Parse("3.0")));
        row.CustomVersionText = "3.0";
        Assert.IsNull(row.SelectedTarget);
    }

    [TestMethod]
    public void CompleteLocalInventoryWithIncompleteICBaselineAndFailedMetadataOffersExplicitDraft()
    {
        var row = UnavailableSoup(CandidateStatus.Failed, "HTTP Found (302)");
        var repository = row.Decision.Repositories.Single();
        row.SetRepositoryScope(repository.Usages[0].Repository, repository.Usages, true);
        Assert.HasCount(1, row.Choices);
        Assert.IsNull(row.SelectedTarget);
        Assert.AreEqual("Awaiting selection", row.Target);
        Assert.AreEqual("10.0.12 (provisional)", row.ICMax);
        Assert.AreEqual("AwaitingSelection", row.Action);
        Assert.IsTrue(row.IsSoupTargetEditable);
        Assert.IsFalse(row.IsTargetUnavailable);
        Assert.Throws<ArgumentException>(() => row.SelectedTarget = new(TargetSource.ICMax, NuGetVersion.Parse("10.0.12")));
        row.SelectedTarget = row.Choices.Single();
        Assert.IsTrue(row.SelectedTarget.IsProvisional);
        StringAssert.Contains(row.TargetAvailability, "Preview only");
        StringAssert.Contains(row.TargetTooltip, "Approval and execution");
        Assert.AreEqual("Aligned", row.Action);
        Assert.IsTrue(new ReviewRow(repository, row, repository.Usages[0].Repository).PreviewEligible);
        StringAssert.Contains(row.CandidateStatus, "HTTP Found (302)");
    }

    [TestMethod]
    [DataRow(CandidateStatus.NotFound, "No listed formal version is available.")]
    [DataRow(CandidateStatus.NotRequested, "Not requested")]
    public void MissingMetadataNeverPretendsSelectionIsPossible(CandidateStatus status, string detail)
    {
        var row = new PackageRow(UnavailableSoup(status, detail).Decision with { ICMax = null });
        Assert.AreEqual("No confirmed target", row.Target);
        Assert.AreEqual("Unresolved", row.Action);
        StringAssert.Contains(row.TargetUnavailableReason, status == CandidateStatus.NotFound ? "No formal NuGet version" : "NuGet query not completed");
    }

    [TestMethod]
    public void PrereleaseOnlyMetadataCannotBecomeFormalChoice()
    {
        var row = new PackageRow(UnavailableSoup(CandidateStatus.Available, "Only prereleases").Decision with
        {
            ICMax = null, NuGetCandidate = new("signalr", NuGetVersion.Parse("11.0-preview"), CandidateStatus.Available, "fixture", DateTimeOffset.UtcNow)
        });
        Assert.HasCount(0, row.Choices);
        Assert.AreEqual("No confirmed target", row.Target);
        Assert.AreEqual("N/A", row.NuGetMax);
        StringAssert.Contains(row.TargetUnavailableReason, "No formal NuGet version");
    }

    [TestMethod]
    public void UnconfirmedOwnershipHasNoConfirmedTargetAndUnknownMaximumIsNotProvisional()
    {
        var row = new PackageRow(UnavailableSoup(CandidateStatus.NotRequested, "").Decision with
        {
            Ownership = PackageOwnership.Unconfirmed, IsPlatformMaxComplete = false, ICMax = null
        });
        Assert.AreEqual("No confirmed target", row.Target);
        Assert.AreEqual("Unresolved", row.Action);
        Assert.AreEqual("N/A", row.ICMax);
        StringAssert.Contains(row.TargetUnavailableReason, "Ownership unconfirmed");
        var soup = new PackageRow(UnavailableSoup(CandidateStatus.NotRequested, "").Decision with { ICMax = null });
        Assert.AreEqual("Unknown", soup.ICMax);
        foreach (var category in new[] { RepositoryCategory.Platform, RepositoryCategory.ICEmulator })
        {
            row.SetRepositoryScope(TestData.Repo("readonly", category), [], false);
            Assert.AreEqual("NotApplicable", row.Action);
            Assert.IsFalse(row.IsTargetEditable);
        }
    }

    [TestMethod]
    public void CompleteICBaselineRemainsSelectableDespiteMetadataFailure()
    {
        var row = new PackageRow(UnavailableSoup(CandidateStatus.Failed, "HTTP Found (302)").Decision with { IsICMaxComplete = true });
        Assert.AreEqual("10.0.12", row.ICMax);
        Assert.AreEqual("Awaiting selection", row.Target);
        Assert.AreEqual("AwaitingSelection", row.Action);
        Assert.IsFalse(row.IsTargetUnavailable);
        Assert.AreEqual("Available", row.TargetAvailability);
        Assert.AreEqual("", row.TargetUnavailableReason);
        row.SelectedTarget = row.Choices.Single();
        Assert.AreEqual("Aligned", row.Action);
    }

    [TestMethod]
    public void FormalNuGetRemainsSelectableWithIncompleteICBaselineButCannotWrite()
    {
        var row = new PackageRow(UnavailableSoup(CandidateStatus.Failed, "").Decision with
        {
            NuGetCandidate = new("signalr", NuGetVersion.Parse("10.0.13"), CandidateStatus.Available, "fixture", DateTimeOffset.UtcNow)
        });
        Assert.HasCount(2, row.Choices);
        Assert.IsTrue(row.IsSoupTargetEditable);
        Assert.AreEqual("Available", row.TargetAvailability);
        row.SelectedTarget = row.Choices.Single(choice => choice.Source == TargetSource.NuGetCandidate);
        Assert.AreEqual("Upgrade", row.Action);
        var repository = row.Decision.Repositories.Single();
        Assert.IsTrue(new ReviewRow(repository, row, repository.Usages[0].Repository).PreviewEligible);
    }

    [TestMethod]
    public void LockedSharedAndOptionalInternalHaveExplicitAvailability()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        var row = new PackageRow(TestData.Analyze(TestData.Inventory(platform, TestData.Usage(platform, "shared", "2.0")),
            TestData.Inventory(ic, TestData.Usage(ic, "shared", "1.0"))).Packages.Single());
        Assert.AreEqual("Locked to PlatformMax", row.TargetAvailability);
        StringAssert.Contains(row.TargetTooltip, "Locked to PlatformMax");
        Assert.AreEqual("Manual optional", Internal().TargetAvailability);
        row.SetRepositoryScope(platform, [], true);
        Assert.AreEqual("Read-only", row.TargetAvailability);
        Assert.AreEqual("NotApplicable", row.Action);
    }

    [TestMethod]
    public void NotFoundPackageAndMissingMaximumExplainUnavailableCandidates()
    {
        var row = new PackageRow(UnavailableSoup(CandidateStatus.NotFound, "Package was not found.").Decision with { ICMax = null });
        StringAssert.Contains(row.TargetUnavailableReason, "NuGet package not found");
        row = new PackageRow(row.Decision with { IsICMaxComplete = true, ICMax = null, NuGetCandidate = null });
        Assert.AreEqual("Unknown", row.ICMax);
        Assert.AreEqual("No confirmed target", row.Target);
        StringAssert.Contains(row.TargetUnavailableReason, "ICMax unknown");
        StringAssert.Contains(row.TargetUnavailableReason, "NuGet query not completed");
    }

    private static PackageRow UnavailableSoup(CandidateStatus status, string detail)
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("local-complete");
        var failed = TestData.Repo("other-incomplete");
        var analysis = TestData.Analyze(TestData.Inventory(platform),
            TestData.Inventory(ic, TestData.Usage(ic, "signalr", "10.0.12")),
            TestData.Inventory(failed) with { Status = InventoryStatus.Failed, Snapshot = null });
        return new(analysis.Packages.Single() with
        {
            NuGetCandidate = new("signalr", null, status, "fixture", DateTimeOffset.UtcNow, detail)
        });
    }

    private static PackageRow Internal()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        return new(TestData.Analyze(TestData.Inventory(platform),
            TestData.Inventory(ic, TestData.Usage(ic, "elekta.internal", "1.0"))).Packages.Single());
    }
}