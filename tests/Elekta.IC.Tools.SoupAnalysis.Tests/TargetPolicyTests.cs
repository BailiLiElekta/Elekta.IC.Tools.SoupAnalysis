using System.Text.Json;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Configuration;

namespace Elekta.IC.Tools.SoupAnalysis.Tests;

[TestClass]
public sealed class TargetPolicyTests
{
    [TestMethod]
    public void PartialBaselineHasSeparateNonApprovablePreviewWithKnownICMax()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        var analysis = TestData.Analyze(TestData.Inventory(platform),
            TestData.Inventory(ic, TestData.Usage(ic, "soup", "1.0")));
        analysis = analysis with { CompleteIC = false, Packages = [analysis.Packages[0] with
            { IsICMaxComplete = false, ICMax = PackageVersions.ParseExact("10.0.12") }] };
        var selections = System.Collections.Immutable.ImmutableArray.Create(new RepositoryPackageSelection(ic.Id, "soup"));
        var targets = System.Collections.Immutable.ImmutableArray.Create(new PackageTargetSelection("soup", TargetSource.ICMax));
        var planner = new ChangePlanner();
        Assert.ThrowsExactly<InvalidOperationException>(() => planner.Create(analysis, selections, targets));
        var preview = planner.CreatePreview(analysis, selections, targets);
        Assert.AreEqual("PreviewOnly", preview.Status.ToString());
        Assert.IsFalse(preview.CanExecute);
        Assert.IsNull(preview.Approval);
        Assert.AreEqual("10.0.12", preview.Changes[0].Target.ToNormalizedString());
        Assert.ThrowsExactly<InvalidOperationException>(() => preview.Approve(analysis, "reviewer", DateTimeOffset.UtcNow));
        Assert.ThrowsExactly<InvalidOperationException>(() => preview.Approve(analysis with { CompleteIC = true }, "reviewer", DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void AvailablePrereleaseNuGetTargetsAreRejectedByPlanningAndReports()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        var analysis = TestData.Analyze(TestData.Inventory(platform),
            TestData.Inventory(ic, TestData.Usage(ic, "soup", "1.0")));
        analysis = analysis with { Packages = [analysis.Packages[0] with
            { NuGetCandidate = new("soup", PackageVersions.ParseExact("9.0.0-preview.1"),
                CandidateStatus.Available, "nuget.org", DateTimeOffset.UtcNow) }] };
        var report = new ReportSelection(analysis.Id, [new("soup", TargetSource.NuGetCandidate)], [new(ic.Id, "soup")]);
        Assert.ThrowsExactly<InvalidOperationException>(() => new ChangePlanner().Create(analysis, report.Selections, report.Targets));
        Assert.ThrowsExactly<InvalidOperationException>(() => report.Validate(analysis));
        Assert.ThrowsExactly<InvalidOperationException>(() => report.ResolveTarget(analysis.Packages[0]));
        Assert.ThrowsExactly<InvalidOperationException>(() => (report with { Selections = [] }).Validate(analysis));
    }

    [TestMethod]
    public void PartialReadOnlyReportsResolveKnownDraftTargetsWithoutExecutableApproval()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        var analysis = TestData.Analyze(TestData.Inventory(platform), TestData.Inventory(ic,
            TestData.Usage(ic, "soup", "1.0"), TestData.Usage(ic, "elekta.extra", "1.0")));
        analysis = analysis with { CompleteIC = false, Packages = [.. analysis.Packages.Select(package => package with
            { IsICMaxComplete = false, ICMax = PackageVersions.ParseExact("10.0.12") })] };
        var targets = System.Collections.Immutable.ImmutableArray.Create(new PackageTargetSelection("soup", TargetSource.ICMax));
        var selections = System.Collections.Immutable.ImmutableArray.Create(new RepositoryPackageSelection(ic.Id, "soup"));
        var preview = new ChangePlanner().CreatePreview(analysis, selections, targets);
        var report = new ReportSelection(analysis.Id, targets.Add(new("elekta.extra", TargetSource.Custom, "25.1.3")), selections, preview);
        report.Validate(analysis);
        Assert.AreEqual("10.0.12", report.ResolveTarget(analysis.Packages.Single(package => package.PackageId == "soup"))!.ToNormalizedString());
        Assert.IsFalse(report.ReviewPlan!.CanExecute);
        Assert.ThrowsExactly<InvalidOperationException>(() => report.Validate(analysis with { ManifestHash = "changed" }));
        Assert.ThrowsExactly<InvalidOperationException>(() => (report with { Targets = [new("soup", TargetSource.NuGetCandidate), report.Targets[1]] }).Validate(analysis));
        Assert.ThrowsExactly<ArgumentException>(() => (report with { Targets = [targets[0], new("elekta.extra", TargetSource.ICMax)] }).Validate(analysis));
    }

    [TestMethod]
    [DataRow("platform")]
    [DataRow("source")]
    [DataRow("sha")]
    [DataRow("file")]
    [DataRow("locator")]
    [DataRow("owner")]
    [DataRow("missingMaximum")]
    [DataRow("local")]
    [DataRow("version")]
    [DataRow("manifest")]
    [DataRow("branch")]
    [DataRow("classification")]
    public void PreviewRejectsUnsafeOrUnknownLocalEvidence(string failure)
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        var analysis = TestData.Analyze(TestData.Inventory(platform), TestData.Inventory(ic, TestData.Usage(ic, "soup", "1.0")));
        var inventory = analysis.Inventories[1];
        var usage = inventory.Usages[0];
        analysis = analysis with { CompleteIC = false, Packages = [analysis.Packages[0] with { IsICMaxComplete = false }] };
        switch (failure)
        {
            case "platform": analysis = analysis with { CompletePlatform = false }; break;
            case "source": inventory = inventory with { Snapshot = inventory.Snapshot! with { Source = ic with { Ref = "different" } } }; break;
            case "sha": inventory = inventory with { Snapshot = inventory.Snapshot! with { Commit = "" } }; break;
            case "file": usage = usage with { Declaration = usage.Declaration with { VersionOwner = usage.Declaration.VersionOwner with { SourceFile = "" } } }; break;
            case "locator": usage = usage with { Declaration = usage.Declaration with { VersionOwner = usage.Declaration.VersionOwner with { ElementPath = "" } } }; break;
            case "owner": usage = usage with { Declaration = usage.Declaration with { IsUniquelyOwned = false } }; break;
            case "missingMaximum": analysis = analysis with { Packages = [analysis.Packages[0] with { ICMax = null }] }; break;
            case "local": inventory = inventory with { Status = InventoryStatus.Failed }; break;
            case "version": usage = usage with { RawVersion = "[1.0,2.0)" }; break;
            case "manifest": analysis = analysis with { ManifestHash = "" }; break;
            case "branch": inventory = inventory with { Source = ic with { RefKind = RefKind.Tag } }; break;
            case "classification": analysis = analysis with { Packages = [analysis.Packages[0] with { Ownership = PackageOwnership.Unconfirmed }] }; break;
        }
        inventory = inventory with { Usages = [usage] };
        analysis = analysis with { Inventories = [analysis.Inventories[0], inventory] };
        Assert.Throws<Exception>(() => new ChangePlanner().CreatePreview(analysis, [new(ic.Id, "soup")], [new("soup", TargetSource.ICMax)]));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReviewedAlignedSelectionsRetainExactTargetSourceProvenance(bool previewOnly)
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        var analysis = TestData.Analyze(TestData.Inventory(platform, TestData.Usage(platform, "shared", "2.0")),
            TestData.Inventory(ic, TestData.Usage(ic, "shared", "1.0"), TestData.Usage(ic, "soup", "1.0")));
        analysis = analysis with { CompleteIC = !previewOnly, Packages = [.. analysis.Packages.Select(package => package with
            { IsICMaxComplete = !previewOnly, NuGetCandidate = new(package.PackageId, PackageVersions.ParseExact("1.0"),
                CandidateStatus.Available, "nuget.org", DateTimeOffset.UtcNow) })] };
        var selections = System.Collections.Immutable.ImmutableArray.Create(new RepositoryPackageSelection(ic.Id, "shared"), new(ic.Id, "soup"));
        var targets = System.Collections.Immutable.ImmutableArray.Create(new PackageTargetSelection("shared", TargetSource.PlatformMax), new("soup", TargetSource.ICMax));
        var planner = new ChangePlanner();
        var plan = previewOnly ? planner.CreatePreview(analysis, selections, targets)
            : planner.Create(analysis, selections, targets).Approve(analysis, "reviewer", DateTimeOffset.UtcNow);
        Assert.HasCount(1, plan.Changes);
        var report = new ReportSelection(analysis.Id, targets, selections, plan);
        report.Validate(analysis);
        Assert.ThrowsExactly<InvalidOperationException>(() => (report with
            { Targets = [targets[0], new("soup", TargetSource.NuGetCandidate)] }).Validate(analysis));
        Assert.ThrowsExactly<InvalidOperationException>(() => (report with { Selections = [selections[0]] }).Validate(analysis));
    }

    [TestMethod]
    public void SerializedInternalCustomTargetIsExplicitAndDoesNotAlignToICMax()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        var analysis = TestData.Analyze(TestData.Inventory(platform),
            TestData.Inventory(ic, TestData.Usage(ic, "elekta.Internal", "5.0")));
        var target = JsonSerializer.Deserialize<PackageTargetSelection>(
            """{"PackageId":"elekta.Internal","Source":3,"CustomVersion":"1.2.3-rc.4+build.5"}""")!;
        var selection = new ReportSelection(analysis.Id, [target], [new(ic.Id, target.PackageId)]);
        selection.Validate(analysis);
        Assert.AreEqual("1.2.3-rc.4", selection.ResolveTarget(analysis.Packages[0])!.ToNormalizedString());
        var plan = new ChangePlanner().Create(analysis, selection.Selections, selection.Targets);
        Assert.HasCount(1, plan.Changes);
        Assert.AreEqual("Custom", plan.Changes[0].TargetSource.ToString());
        Assert.AreEqual("1.2.3-rc.4", plan.Changes[0].Target.ToNormalizedString());
        Assert.AreEqual(AlignmentAction.Downgrade, plan.Changes[0].Usages[0].Action);
        Assert.AreEqual(PackageOwnership.Internal, analysis.Packages[0].Ownership);
        Assert.IsFalse(analysis.Packages[0].IsSoup);
    }

    [TestMethod]
    [DataRow("0.1", AlignmentAction.Downgrade)]
    [DataRow("6.0.0-rc.1", AlignmentAction.Upgrade)]
    [DataRow("5.0.0+build.1", AlignmentAction.Aligned)]
    public void InternalCustomTargetsAllowAnyExactVersionAndSkipNoOps(string version, AlignmentAction expected)
    {
        var (analysis, ic) = InternalAnalysis();
        var plan = new ChangePlanner().Create(analysis, [new(ic.Id, "elekta.Internal")],
            [new("elekta.Internal", TargetSource.Custom, version)]);
        if (expected == AlignmentAction.Aligned)
        {
            Assert.AreEqual(ChangePlanStatus.NoChanges, plan.Status);
            Assert.IsEmpty(plan.Changes);
            Assert.IsFalse(plan.CanExecute);
        }
        else
        {
            Assert.HasCount(1, plan.Changes);
            Assert.AreEqual(expected, plan.Changes[0].Usages[0].Action);
            Assert.IsFalse(plan.CanExecute);
        }
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("[1.0]")]
    [DataRow("[1.0,2.0)")]
    [DataRow("1.*")]
    [DataRow("1.0-*")]
    [DataRow("latest")]
    [DataRow("$(Version)")]
    [DataRow("1.0.0-rc..1")]
    public void InvalidCustomVersionsAreRejectedByReportsAndPlanning(string? version)
    {
        var (analysis, ic) = InternalAnalysis();
        var report = new ReportSelection(analysis.Id, [new("elekta.Internal", TargetSource.Custom, version)],
            [new(ic.Id, "elekta.Internal")]);
        Assert.ThrowsExactly<ArgumentException>(() => report.Validate(analysis));
        Assert.ThrowsExactly<ArgumentException>(() => report.ResolveTarget(analysis.Packages[0]));
        Assert.ThrowsExactly<ArgumentException>(() => new ChangePlanner().Create(analysis, report.Selections, report.Targets));
    }

    [TestMethod]
    [DataRow(TargetSource.PlatformMax)]
    [DataRow(TargetSource.ICMax)]
    [DataRow(TargetSource.NuGetCandidate)]
    [DataRow((TargetSource)99)]
    public void InternalMaxSourcesRemainRejected(TargetSource source)
    {
        var (analysis, ic) = InternalAnalysis();
        var report = new ReportSelection(analysis.Id, [new("elekta.Internal", source)], [new(ic.Id, "elekta.Internal")]);
        Assert.ThrowsExactly<ArgumentException>(() => report.Validate(analysis));
        Assert.ThrowsExactly<ArgumentException>(() => new ChangePlanner().Create(analysis, report.Selections, report.Targets));
    }

    [TestMethod]
    [DataRow(PackageOwnership.SharedWithPlatform)]
    [DataRow(PackageOwnership.Soup)]
    [DataRow(PackageOwnership.Unconfirmed)]
    public void CustomIsRejectedForEveryNonInternalOwnership(PackageOwnership ownership)
    {
        var (analysis, ic) = InternalAnalysis();
        analysis = analysis with { Packages = [analysis.Packages[0] with { Ownership = ownership }] };
        var report = new ReportSelection(analysis.Id, [new("elekta.Internal", TargetSource.Custom, "2.0")],
            [new(ic.Id, "elekta.Internal")]);
        Assert.ThrowsExactly<ArgumentException>(() => report.Validate(analysis));
        Assert.ThrowsExactly<ArgumentException>(() => new ChangePlanner().Create(analysis, report.Selections, report.Targets));
    }

    [TestMethod]
    [DataRow(PackageOwnership.SharedWithPlatform)]
    [DataRow(PackageOwnership.Internal)]
    [DataRow(PackageOwnership.Soup)]
    [DataRow(PackageOwnership.Unconfirmed)]
    public void UndefinedSourcesAreRejectedForEveryOwnership(PackageOwnership ownership)
    {
        var (analysis, ic) = InternalAnalysis();
        analysis = analysis with { Packages = [analysis.Packages[0] with { Ownership = ownership }] };
        var report = new ReportSelection(analysis.Id, [new("elekta.Internal", (TargetSource)(-1), "1.0")],
            [new(ic.Id, "elekta.Internal")]);
        Assert.ThrowsExactly<ArgumentException>(() => report.Validate(analysis));
        Assert.ThrowsExactly<ArgumentException>(() => report.ResolveTarget(analysis.Packages[0]));
        Assert.ThrowsExactly<ArgumentException>(() => new ChangePlanner().Create(analysis, report.Selections, report.Targets));
    }

    [TestMethod]
    public void SharedAndSoupOnlyAcceptTheirOwnSourcesAndNeverAcceptCustomVersionData()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        var analysis = TestData.Analyze(TestData.Inventory(platform, TestData.Usage(platform, "shared", "2.0-rc.10")),
            TestData.Inventory(ic, TestData.Usage(ic, "shared", "1.0"), TestData.Usage(ic, "soup", "3.0-rc.10")));
        analysis = analysis with { Packages = [.. analysis.Packages.Select(package => package with
            { NuGetCandidate = new(package.PackageId, PackageVersions.ParseExact("2.0"), CandidateStatus.Available, "nuget.org", DateTimeOffset.UtcNow) })] };
        foreach (var package in analysis.Packages)
        {
            foreach (var source in new[] { TargetSource.PlatformMax, TargetSource.ICMax, TargetSource.NuGetCandidate, (TargetSource)99 })
            {
                var report = new ReportSelection(analysis.Id, [new(package.PackageId, source)], [new(ic.Id, package.PackageId)]);
                var allowed = package.Ownership == PackageOwnership.SharedWithPlatform
                    ? source == TargetSource.PlatformMax : source is TargetSource.ICMax or TargetSource.NuGetCandidate;
                if (allowed)
                {
                    report.Validate(analysis);
                    _ = new ChangePlanner().Create(analysis, report.Selections, report.Targets);
                    if (source != TargetSource.NuGetCandidate)
                        Assert.IsTrue(report.ResolveTarget(package)!.IsPrerelease);
                }
                else
                {
                    Assert.ThrowsExactly<ArgumentException>(() => report.Validate(analysis));
                    Assert.ThrowsExactly<ArgumentException>(() => new ChangePlanner().Create(analysis, report.Selections, report.Targets));
                }
                foreach (var custom in new[] { "", "2.0" })
                {
                    var polluted = report with { Targets = [new(package.PackageId, source, custom)] };
                    Assert.ThrowsExactly<ArgumentException>(() => polluted.Validate(analysis));
                    Assert.ThrowsExactly<ArgumentException>(() => new ChangePlanner().Create(analysis, polluted.Selections, polluted.Targets));
                }
            }
        }
    }

    [TestMethod]
    public void ManualInternalPlanRetainsCompletenessBranchAndOwnershipSafety()
    {
        var (analysis, ic) = InternalAnalysis();
        var planner = new ChangePlanner();
        var selections = System.Collections.Immutable.ImmutableArray.Create(new RepositoryPackageSelection(ic.Id, "elekta.Internal"));
        var targets = System.Collections.Immutable.ImmutableArray.Create(new PackageTargetSelection("elekta.Internal", TargetSource.Custom, "6.0"));
        foreach (var incomplete in new[] { analysis with { CompleteIC = false }, analysis with { CompletePlatform = false } })
            Assert.ThrowsExactly<InvalidOperationException>(() => planner.Create(incomplete, selections, targets));
        var inventory = analysis.Inventories.Single(item => item.Source.Id == ic.Id);
        var tag = inventory with { Source = ic with { RefKind = RefKind.Tag } };
        Assert.ThrowsExactly<InvalidOperationException>(() => planner.Create(analysis with { Inventories = [analysis.Inventories[0], tag] }, selections, targets));
        foreach (var category in new[] { RepositoryCategory.Platform, RepositoryCategory.ICEmulator })
            Assert.ThrowsExactly<ArgumentException>(() => planner.Create(analysis with
                { Inventories = [analysis.Inventories[0], inventory with { Source = ic with { Category = category } }] }, selections, targets));
        var ambiguous = inventory.Usages[0] with { Declaration = inventory.Usages[0].Declaration with { IsUniquelyOwned = false } };
        Assert.ThrowsExactly<InvalidOperationException>(() => planner.Create(analysis with
            { Inventories = [analysis.Inventories[0], inventory with { Usages = [ambiguous] }] }, selections, targets));
        Assert.ThrowsExactly<ArgumentException>(() => planner.Create(analysis, selections, [targets[0], targets[0]]));
    }

    [TestMethod]
    public void ReviewedPlanAllowsExtraReportTargetsButValidatesEveryTargetAndExactSelectedChanges()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        var analysis = TestData.Analyze(TestData.Inventory(platform, TestData.Usage(platform, "shared", "2.0")),
            TestData.Inventory(ic, TestData.Usage(ic, "shared", "1.0"), TestData.Usage(ic, "elekta.Internal", "5.0"),
                TestData.Usage(ic, "soup", "1.0")));
        analysis = analysis with { Packages = [.. analysis.Packages.Select(package => package.PackageId == "soup"
            ? package with { NuGetCandidate = new("soup", PackageVersions.ParseExact("9.0-preview.1"),
                CandidateStatus.Available, "nuget.org", DateTimeOffset.UtcNow) } : package)] };
        var selectedTargets = System.Collections.Immutable.ImmutableArray.Create(
            new PackageTargetSelection("shared", TargetSource.PlatformMax), new("elekta.Internal", TargetSource.Custom, "6.0-rc.1"));
        var selections = System.Collections.Immutable.ImmutableArray.Create(
            new RepositoryPackageSelection(ic.Id, "shared"), new(ic.Id, "elekta.Internal"));
        var plan = new ChangePlanner().Create(analysis, selections, selectedTargets).Approve(analysis, "reviewer", DateTimeOffset.UtcNow);
        var report = new ReportSelection(analysis.Id, selectedTargets.Add(new("soup", TargetSource.ICMax)), selections, plan);
        report.Validate(analysis);
        Assert.AreSame(plan, report.ReviewPlan);
        Assert.HasCount(2, plan.Changes);
        Assert.ThrowsExactly<InvalidOperationException>(() => (report with
            { Targets = [selectedTargets[0], new("elekta.Internal", TargetSource.Custom, "7.0"), report.Targets[2]] }).Validate(analysis));
        Assert.ThrowsExactly<ArgumentException>(() => (report with
            { Targets = [.. selectedTargets, new("soup", TargetSource.Custom, "2.0")] }).Validate(analysis));
        Assert.ThrowsExactly<InvalidOperationException>(() => (report with
            { Targets = [.. selectedTargets, new("soup", TargetSource.NuGetCandidate)] }).Validate(analysis));
        Assert.ThrowsExactly<ArgumentException>(() => (report with
            { Targets = [selectedTargets[0], report.Targets[2]] }).Validate(analysis));
        Assert.ThrowsExactly<InvalidOperationException>(() => (report with { Selections = [selections[0]] }).Validate(analysis));
        Assert.ThrowsExactly<ArgumentException>(() => (report with { Selections = [] }).Validate(analysis));
    }

    [TestMethod]
    public void ReviewedSoupSourceCannotChangeEvenWhenVersionsMatchAndReportHasExtraTargets()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        var other = TestData.Repo("other");
        var analysis = TestData.Analyze(TestData.Inventory(platform),
            TestData.Inventory(ic, TestData.Usage(ic, "soup", "1.0"), TestData.Usage(ic, "elekta.Internal", "1.0")),
            TestData.Inventory(other, TestData.Usage(other, "soup", "2.0")));
        analysis = analysis with { Packages = [.. analysis.Packages.Select(package => package.PackageId == "soup"
            ? package with { NuGetCandidate = new("soup", PackageVersions.ParseExact("2.0"), CandidateStatus.Available,
                "nuget.org", DateTimeOffset.UtcNow) } : package)] };
        var selections = System.Collections.Immutable.ImmutableArray.Create(new RepositoryPackageSelection(ic.Id, "soup"));
        var targets = System.Collections.Immutable.ImmutableArray.Create(new PackageTargetSelection("soup", TargetSource.ICMax));
        var plan = new ChangePlanner().Create(analysis, selections, targets).Approve(analysis, "reviewer", DateTimeOffset.UtcNow);
        var report = new ReportSelection(analysis.Id, targets.Add(new("elekta.Internal", TargetSource.Custom, "3.0-preview.1")), selections, plan);
        report.Validate(analysis);
        (report with { ReviewPlan = null }).Validate(analysis);
        Assert.ThrowsExactly<InvalidOperationException>(() => (report with
            { Targets = [new("soup", TargetSource.NuGetCandidate), report.Targets[1]] }).Validate(analysis));
        foreach (var reviewed in new ChangePlan?[] { plan, null })
            Assert.ThrowsExactly<ArgumentException>(() => (report with { ReviewPlan = reviewed,
                Targets = [targets[0], new("elekta.Internal", TargetSource.ICMax)] }).Validate(analysis));
    }

    [TestMethod]
    public void ReviewedCustomTargetsCannotBeChangedOrSubstituted()
    {
        var (analysis, ic) = InternalAnalysis();
        var report = new ReportSelection(analysis.Id, [new("elekta.Internal", TargetSource.Custom, "6.0-rc.1")],
            [new(ic.Id, "elekta.Internal")]);
        var plan = new ChangePlanner().Create(analysis, report.Selections, report.Targets);
        var reviewed = report with { ReviewPlan = plan.Approve(analysis, "reviewer", DateTimeOffset.UtcNow) };
        reviewed.Validate(analysis);
        Assert.ThrowsExactly<InvalidOperationException>(() => (reviewed with
            { Targets = [new("elekta.Internal", TargetSource.Custom, "7.0")] }).Validate(analysis));
        Assert.ThrowsExactly<ArgumentException>(() => (reviewed with
            { Targets = [new("elekta.Internal", TargetSource.ICMax)] }).Validate(analysis));
        Assert.ThrowsExactly<InvalidOperationException>(() => reviewed.Validate(analysis with { Id = Guid.NewGuid() }));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("1.*")]
    [DataRow("[1.0]")]
    public async Task InvalidCustomPreferencesCannotBeSaved(string? version)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new SettingsStore(path).SaveAsync(
                UserSettings.Default with { CandidatePreferences = [new("elekta.Internal", TargetSource.Custom, version)] }, default));
            Assert.IsFalse(File.Exists(path));
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [TestMethod]
    public async Task CustomPreferenceRoundTripsAsStringWithoutChangingOldTwoArgumentDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var preference = new CandidatePreference("elekta.Internal", TargetSource.Custom, "0.1.0-rc.1+build.2");
            await store.SaveAsync(UserSettings.Default with { CandidatePreferences = [preference] }, default);
            Assert.AreEqual(preference, (await store.LoadAsync(default)).CandidatePreferences[0]);
            StringAssert.Contains(await File.ReadAllTextAsync(path), "\"Custom\"");
            Assert.IsNull(new CandidatePreference("soup", TargetSource.ICMax).CustomVersion);
            Assert.IsNull(new PackageTargetSelection("soup", TargetSource.ICMax).CustomVersion);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.SaveAsync(UserSettings.Default with
                { CandidatePreferences = [new("soup", TargetSource.ICMax, "1.0")] }, default));
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    private static (AnalysisResult Analysis, RepositorySource IC) InternalAnalysis()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        return (TestData.Analyze(TestData.Inventory(platform),
            TestData.Inventory(ic, TestData.Usage(ic, "elekta.Internal", "5.0"))), ic);
    }
}