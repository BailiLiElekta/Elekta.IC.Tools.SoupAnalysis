using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Desktop.Presentation;
using Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels;
using Elekta.IC.Tools.SoupAnalysis.Domain;

namespace Elekta.IC.Tools.SoupAnalysis.Tests;

[TestClass]
public sealed class ChangeSelectionTests
{
    [TestMethod]
    public Task LegacySettingsUseManifestRefsAndKeepPersonalSettingsAtStartup() => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        var ports = new Ports(analysis);
        ports.Configuration = ports.Configuration with
        {
            Manifest = ports.Configuration.Manifest with { Version = "2", AnalysisFor = "IC Berlin Release" }
        };
        ports.Settings = ports.Settings with
        {
            PlatformRefOverrides = [new("platform", RefKind.Branch, "release/old")],
            OutputDirectory = "personal-output"
        };
        using var model = CreateModel(ports);
        await model.InitializeCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.AreEqual("2", model.PlatformConfigVersion);
        Assert.AreEqual("IC Berlin Release", model.AnalysisFor);
        Assert.AreEqual("main", model.Repositories.Single(row => row.Source.Id == "platform").Ref);
        await model.SaveSettingsCommand.ExecuteAsync(null);
        Assert.AreEqual(new PlatformConfigurationIdentity("2", "IC Berlin Release"), ports.Settings.PlatformConfiguration);
        Assert.AreEqual("personal-output", ports.Settings.OutputDirectory);
        Assert.HasCount(3, ports.Settings.Repositories);
    });

    [TestMethod]
    [DataRow("version")]
    [DataRow("release")]
    [DataRow("same")]
    public Task ScanReloadsMetadataAndUsesReleaseScopedRefs(string change) => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        var ports = new Ports(analysis) { AllowScan = true };
        ports.Configuration = ports.Configuration with
        {
            Manifest = ports.Configuration.Manifest with { Version = "2", AnalysisFor = "IC Berlin Release" }
        };
        using var model = CreateModel(ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(analysis);
        model.ReviewRows.Single(row => row.Repository.Id == "first" && row.PackageId == "upgrade").IsSelected = true;
        await model.ReviewCommand.ExecuteAsync(null);
        Assert.IsTrue(model.CanExecute);
        model.Repositories.Single(row => row.Source.Id == "platform").Ref = "custom";
        var manifest = ports.Configuration.Manifest;
        ports.Configuration = ports.Configuration with
        {
            ManifestHash = "new-hash",
            Manifest = manifest with
            {
                Version = change == "version" ? "3" : "2",
                AnalysisFor = change == "release" ? "IC Next Release" : "IC Berlin Release",
                Repositories = [.. manifest.Repositories.Select(source => source with { Ref = "release/new" })]
            }
        };
        var notifications = new List<string?>();
        model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        await model.ScanCommand.ExecuteAsync(null);

        Assert.IsNull(model.UserError);
        Assert.AreEqual(change == "version" ? "3" : "2", model.PlatformConfigVersion);
        Assert.AreEqual(change == "release" ? "IC Next Release" : "IC Berlin Release", model.AnalysisFor);
        var expectedRef = change == "same" ? "custom" : "release/new";
        Assert.AreEqual(expectedRef, model.Repositories.Single(row => row.Source.Id == "platform").Ref);
        Assert.AreEqual(expectedRef, model.Analysis!.OriginalRefs.Single(source => source.Id == "platform").Ref);
        Assert.AreEqual(expectedRef, ports.Settings.PlatformRefOverrides.Single(item => item.RepositoryId == "platform").Ref);
        Assert.AreEqual(new PlatformConfigurationIdentity(model.PlatformConfigVersion, model.AnalysisFor), ports.Settings.PlatformConfiguration);
        Assert.AreSame(analysis, model.PreviousAnalysis);
        Assert.IsFalse(model.CanExecute);
        CollectionAssert.Contains(notifications, nameof(model.PlatformConfigVersion));
        CollectionAssert.Contains(notifications, nameof(model.AnalysisFor));
    });

    [TestMethod]
    public Task LegacyManifestShowsReleaseAsNotSpecified() => OnDispatcherAsync(async () =>
    {
        using var model = CreateModel(new Ports(Fixture()));
        await model.InitializeCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.AreEqual("Not specified", model.AnalysisFor);
    });

    [TestMethod]
    [DataRow("load")]
    [DataRow("save")]
    public Task FailedScanConfigurationReloadOrSaveInvalidatesApproval(string failure) => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        var ports = new Ports(analysis);
        using var model = CreateModel(ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(analysis);
        model.ReviewRows.Single(row => row.Repository.Id == "first" && row.PackageId == "upgrade").IsSelected = true;
        await model.ReviewCommand.ExecuteAsync(null);
        Assert.IsTrue(model.CanExecute);
        ports.FailConfigurationLoad = failure == "load";
        ports.FailSettingsSave = failure == "save";

        await model.ScanCommand.ExecuteAsync(null);

        Assert.IsNotNull(model.UserError);
        Assert.IsTrue(model.IsAnalysisStale);
        Assert.IsFalse(model.CanExecute);
        Assert.AreSame(analysis, model.Analysis);
        Assert.AreEqual(0, ports.Executions);
    });

    [TestMethod]
    [DataRow(UpdateStatus.PartiallyFailed)]
    [DataRow(UpdateStatus.Cancelled)]
    [DataRow(UpdateStatus.Failed)]
    public Task ExplicitRetryUsesOriginalPlanAndStillRequiresApproval(UpdateStatus status) => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        var ports = new Ports(analysis) { ExecutionStatus = status };
        using var model = CreateModel(ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(analysis);
        model.ReviewRows.Single(row => row.Repository.Id == "first" && row.PackageId == "upgrade").IsSelected = true;
        await model.ReviewCommand.ExecuteAsync(null);
        await model.ExecuteCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.IsFalse(model.CanExecute, "A failed attempt must require fresh explicit approval.");
        var id = ports.ExecutedPlans.Single().Id;
        await model.ReviewCommand.ExecuteAsync(null);
        Assert.AreEqual(id, ports.Approved!.Id, "Reapproval must reuse the journal/branch identity.");
        Assert.AreEqual(2, ports.Approvals);
        ports.ExecutionStatus = UpdateStatus.Completed;
        await model.ExecuteCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        CollectionAssert.AreEqual(new[] { id, id }, ports.ExecutedPlans.Select(plan => plan.Id).ToArray());
        Assert.IsFalse(model.CanExecute);
        Assert.IsTrue(model.IsAnalysisStale);
    });

    [TestMethod]
    [DataRow("selection")]
    [DataRow("target")]
    [DataRow("repository")]
    public Task ChangedReviewInputsDiscardRetryIdentity(string change) => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        var ports = new Ports(analysis) { ExecutionStatus = UpdateStatus.PartiallyFailed };
        using var model = CreateModel(ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(analysis);
        var row = model.ReviewRows.Single(row => row.Repository.Id == "first" && row.PackageId == "upgrade");
        row.IsSelected = true;
        await model.ReviewCommand.ExecuteAsync(null);
        await model.ExecuteCommand.ExecuteAsync(null);
        var id = ports.ExecutedPlans.Single().Id;
        if (change == "selection") { row.IsSelected = false; row.IsSelected = true; }
        else if (change == "target") model.Packages.Single(row => row.PackageId == "elekta.custom").CustomVersionText = "2.0";
        else
        {
            model.SelectedChangeRepository = model.RepositoryChoices.Single(choice => choice.Source.Id == "second");
            model.SelectedChangeRepository = model.RepositoryChoices.Single(choice => choice.Source.Id == "first");
        }
        await model.ReviewCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.AreNotEqual(id, ports.Approved!.Id);
    });

    [TestMethod]
    public Task CompleteLocalICCanSelectAndReviewPartialBaselineWithoutApproval() => OnDispatcherAsync(async () =>
    {
        var baseline = Fixture();
        var analysis = TestData.Analyze([.. baseline.Inventories.Select(inventory => inventory.Source.Id == "second"
            ? inventory with { Status = InventoryStatus.Failed } : inventory)]);
        analysis = analysis with { Packages = [.. analysis.Packages.Select(package => package.PackageId == "soup"
            ? package with { ICMax = PackageVersions.ParseExact("10.0.12"), NuGetCandidate = new("soup", null,
                CandidateStatus.Failed, "nuget.org", DateTimeOffset.UtcNow, "HTTP 302") } : package)] };
        var ports = new Ports(analysis);
        using var model = CreateModel(ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(analysis);
        Assert.IsFalse(analysis.CanUpdate);
        Assert.IsTrue(model.CanSelectChangePackages, "A complete local IC permits read-only selection despite another failed IC.");
        var soup = model.Packages.Single(row => row.PackageId == "soup");
        Assert.IsNull(soup.SelectedTarget, "Known provisional ICMax must not be chosen automatically.");
        soup.SelectedTarget = soup.Choices.Single(choice => choice.Source == TargetSource.ICMax);
        StringAssert.Contains(soup.SelectedTarget.Label, "provisional - preview only");
        StringAssert.Contains(soup.TargetAvailability, "Preview only");
        Assert.AreEqual("Upgrade", soup.Action);
        var custom = model.Packages.Single(row => row.PackageId == "elekta.custom");
        custom.CustomVersionText = "25.";
        Assert.IsNull(custom.SelectedTarget);
        Assert.IsNotNull(custom.TargetError);
        Assert.AreEqual("Unresolved", custom.Action);
        custom.CustomVersionText = "25.1.3";
        Assert.AreEqual("Upgrade", model.ReviewRows.Single(row => row.Repository.Id == "first" && row.PackageId == custom.PackageId).Action);
        model.SelectUpgradesCommand.Execute(null);
        CollectionAssert.AreEquivalent(new[] { "upgrade", "partial", "soup", "elekta.custom" }, Selected(model));
        Assert.IsTrue(model.CanReview);
        await model.ReviewCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.AreEqual(0, ports.Approvals);
        Assert.AreEqual(1, ports.Previews);
        Assert.AreEqual("PreviewOnly", ports.Preview!.Status.ToString());
        Assert.IsFalse(model.CanExecute);
        StringAssert.Contains(model.ReviewStatus, "Preview ready; not approved");
        await model.ExportCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.AreSame(ports.Preview, ports.ExportedSelection!.ReviewPlan);
        ports.ExportedSelection.Validate(analysis);
        await model.ExecuteCommand.ExecuteAsync(null);
        Assert.AreEqual(0, ports.Executions);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new UpdateWorkflow(ports).ExecuteAsync(ports.Preview, analysis, null, default));
        Assert.AreEqual(0, ports.Executions);
        custom.CustomVersionText = "25.";
        Assert.IsNull(custom.SelectedTarget);
        Assert.IsFalse(model.CanReview);
        await model.ExportCommand.ExecuteAsync(null);
        Assert.IsNull(ports.ExportedSelection!.ReviewPlan, "Target edits invalidate preview provenance too.");
    });

    [TestMethod]
    public Task DefaultRepositoryPackageSelectionNeedsNoRepositoryCheckbox() => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        var ports = new Ports(analysis);
        using var model = CreateModel(ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(analysis);
        model.ReviewRows.Single(row => row.Repository.Id == "first" && row.PackageId == "upgrade").IsSelected = true;
        Assert.IsTrue(model.CanReview, "The default IC repository is the review scope, without any repository checkbox gate.");
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task TargetEditDuringReviewCannotRestoreInvalidatedPlan(bool previewOnly) => OnDispatcherAsync(async () =>
    {
        var baseline = Fixture();
        var analysis = previewOnly ? TestData.Analyze([.. baseline.Inventories.Select(inventory => inventory.Source.Id == "second"
            ? inventory with { Status = InventoryStatus.Failed } : inventory)]) : baseline;
        var ports = new Ports(analysis);
        using var model = CreateModel(ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(analysis);
        var custom = model.Packages.Single(row => row.PackageId == "elekta.custom");
        custom.CustomVersionText = "25.1.3";
        model.ReviewRows.Single(row => row.Repository.Id == "first" && row.PackageId == custom.PackageId).IsSelected = true;
        ports.ConfigurationWait = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = model.ReviewCommand.ExecuteAsync(null);
        Assert.IsTrue(model.IsBusy);
        await ports.ConfigurationEntered.Task;
        custom.CustomVersionText = "25.1.4";
        ports.ConfigurationWait.SetResult();
        await pending;
        Assert.IsNotNull(model.UserError, "A target edit invalidates even a plan still being prepared.");
        Assert.AreEqual(0, ports.Approvals);
        Assert.AreEqual(0, ports.Previews);
        Assert.IsFalse(model.CanExecute);
        await model.ExportCommand.ExecuteAsync(null);
        Assert.IsNull(ports.ExportedSelection!.ReviewPlan);
    });

    [TestMethod]
    public Task RepositoryNameFiltersOnlyChangesAndLegacyFlagsCannotChangeScope() => OnDispatcherAsync(async () =>
    {
        var ports = new Ports(Fixture());
        using var model = CreateModel(ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(Fixture());
        Assert.AreEqual("first", model.SelectedChangeRepository?.Source.Id);
        CollectionAssert.AreEqual(new[] { "first", "second" }, model.RepositoryChoices.Select(choice => choice.Source.Id).ToArray());
        Assert.HasCount(8, model.VisibleReviewRows.Cast<ReviewRow>());
        model.RepositoryChoices.Single(choice => choice.Source.Id == "second").IsSelected = true;
        Assert.AreEqual("first", model.SelectedChangeRepository?.Source.Id);
        var analysisScope = model.SelectedAnalysisRepository;
        model.SelectedChangeRepository = model.RepositoryChoices.Single(choice => choice.Source.Id == "second");
        Assert.AreSame(analysisScope, model.SelectedAnalysisRepository);
        var secondRow = model.VisibleReviewRows.Cast<ReviewRow>().Single();
        Assert.AreEqual("second", secondRow.Repository.Id);
        Assert.AreEqual("Downgrade", secondRow.Action);
        Assert.IsTrue(model.ChangeRepositorySummary.Contains("second | main", StringComparison.Ordinal));
    });

    [TestMethod]
    public Task BulkCommandsReplaceCurrentSelectionAndNeverApproveOrExecute() => OnDispatcherAsync(async () =>
    {
        var ports = new Ports(Fixture());
        using var model = CreateModel(ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(Fixture());
        var custom = model.Packages.Single(row => row.PackageId == "elekta.custom");
        custom.CustomVersionText = "2.0";
        var second = model.ReviewRows.Single(row => row.Repository.Id == "second");
        second.IsSelected = true;
        model.SelectAllPackagesCommand.Execute(null);
        CollectionAssert.AreEquivalent(new[] { "upgrade", "downgrade", "aligned", "elekta.custom", "mixed", "partial" }, Selected(model));
        Assert.IsTrue(model.ChangeTableTitle.Contains("6 selected", StringComparison.Ordinal));
        Assert.IsTrue(model.ReviewStatus.Contains("2 target missing", StringComparison.Ordinal));
        var statusChanges = 0;
        model.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(MainViewModel.ReviewStatus)) statusChanges++; };
        model.SelectUpgradesCommand.Execute(null);
        CollectionAssert.AreEquivalent(new[] { "upgrade", "elekta.custom", "partial" }, Selected(model));
        Assert.AreEqual("Upgrade", model.VisibleReviewRows.Cast<ReviewRow>().Single(row => row.PackageId == "partial").Action,
            "Aligned + Upgrade usages count as Upgrade.");
        Assert.AreEqual(1, statusChanges, "A batch invalidates approval once.");
        Assert.IsTrue(second.IsSelected, "Stored selections in another repository are untouched.");
        Assert.IsNull(model.Packages.Single(row => row.PackageId == "soup").SelectedTarget);
        Assert.IsNull(model.Packages.Single(row => row.PackageId == "elekta.empty").SelectedTarget);
        model.ClearPackageSelectionCommand.Execute(null);
        Assert.HasCount(0, Selected(model));
        Assert.IsTrue(second.IsSelected);
        Assert.AreEqual(0, ports.Approvals);
        Assert.AreEqual(0, ports.Executions);
    });

    [TestMethod]
    public Task ReviewExportAndApprovalStayInCurrentRepositoryWithHiddenSelections() => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        var ports = new Ports(analysis);
        using var model = CreateModel(ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(analysis);
        model.Packages.Single(row => row.PackageId == "elekta.custom").CustomVersionText = "2.0";
        var first = model.ReviewRows.Single(row => row.Repository.Id == "first" && row.PackageId == "upgrade");
        var second = model.ReviewRows.Single(row => row.Repository.Id == "second");
        first.IsSelected = true;
        second.IsSelected = true;
        await model.ReviewCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.IsTrue(model.CanExecute);
        Assert.AreEqual("first", ports.Approved!.Changes.Single().RepositoryId);
        model.SelectedAnalysisRepository = model.AnalysisRepositoryChoices.Single(choice => choice.Id == "emulator");
        Assert.IsTrue(model.CanExecute, "Read-only Analysis browsing preserves approval.");
        var approvalStatus = model.ReviewStatus;
        model.RepositoryChoices.Single(choice => choice.Source.Id == "second").IsSelected = true;
        Assert.IsTrue(model.CanExecute);
        Assert.AreEqual(approvalStatus, model.ReviewStatus);
        await model.ExportCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.AreSame(analysis, ports.ExportedAnalysis);
        var approvedPlan = ports.ExportedSelection!.ReviewPlan;
        Assert.IsNotNull(approvedPlan);
        Assert.IsTrue(approvedPlan.CanExecute);
        Assert.AreEqual("first", ports.ExportedSelection.Selections.Single().RepositoryId);
        Assert.IsTrue(ports.ExportedSelection.Targets.Any(target => target.PackageId == "elekta.custom"));
        ports.ExportedSelection.Validate(analysis);
        await model.ExportCommand.ExecuteAsync(null);
        Assert.AreSame(approvedPlan, ports.ExportedSelection!.ReviewPlan);
        model.SelectedChangeRepository = model.RepositoryChoices.Single(choice => choice.Source.Id == "second");
        Assert.IsTrue(first.IsSelected);
        Assert.IsTrue(second.IsSelected);
        Assert.IsFalse(model.CanExecute, "Changing Changes scope invalidates hidden approval.");
        await model.ExecuteCommand.ExecuteAsync(null);
        Assert.AreEqual(0, ports.Executions, "Even direct invocation cannot execute hidden approval.");
        await model.ExportCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.IsNull(ports.ExportedSelection!.ReviewPlan);
        Assert.AreEqual("second", ports.ExportedSelection.Selections.Single().RepositoryId);
        await model.ReviewCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.IsTrue(model.CanExecute);
        Assert.AreEqual("second", ports.Approved!.Changes.Single().RepositoryId);
        Assert.AreEqual(AlignmentAction.Downgrade, ports.Approved.Changes.Single().Usages.Single().Action);
        await model.ExportCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.AreEqual("second", ports.ExportedSelection!.ReviewPlan!.Changes.Single().RepositoryId);
        ports.ExportedSelection.Validate(analysis);
    });

    [TestMethod]
    public Task ChangesScopeIsLockedDuringReviewButAnalysisRemainsBrowsable() => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        var ports = new Ports(analysis);
        using var model = CreateModel(ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(analysis);
        model.ReviewRows.Single(row => row.Repository.Id == "first" && row.PackageId == "upgrade").IsSelected = true;
        ports.ConfigurationWait = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = model.ReviewCommand.ExecuteAsync(null);
        Assert.IsTrue(model.IsBusy);
        Assert.IsFalse(model.CanEdit);
        Assert.IsFalse(model.SelectUpgradesCommand.CanExecute(null));
        Assert.IsFalse(model.SelectAllPackagesCommand.CanExecute(null));
        Assert.IsFalse(model.ClearPackageSelectionCommand.CanExecute(null));
        model.SelectedChangeRepository = model.RepositoryChoices.Single(choice => choice.Source.Id == "second");
        model.SelectedAnalysisRepository = model.AnalysisRepositoryChoices.Single(choice => choice.Id == "second");
        Assert.AreEqual("first", model.SelectedChangeRepository?.Source.Id);
        Assert.AreEqual("second", model.SelectedAnalysisRepository?.Id);
        model.SelectAllPackagesCommand.Execute(null);
        CollectionAssert.AreEqual(new[] { "upgrade" }, Selected(model));
        ports.ConfigurationWait.SetResult();
        await pending;
        Assert.IsNull(model.UserError);
        Assert.IsTrue(model.CanExecute);
        Assert.AreEqual("first", ports.Approved!.Changes.Single().RepositoryId);
    });

    [TestMethod]
    public Task InvalidCustomTargetAndUnresolvedVersionsCannotBeBulkSelected() => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        var ports = new Ports(analysis);
        using var model = CreateModel(ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(analysis);
        var custom = model.Packages.Single(row => row.PackageId == "elekta.custom");
        custom.CustomVersionText = "2.*";
        model.SelectAllPackagesCommand.Execute(null);
        Assert.IsFalse(Selected(model).Contains("elekta.custom"));
        Assert.IsTrue(model.ReviewStatus.Contains("3 target missing", StringComparison.Ordinal));
        await model.SetAnalysisAsync(Fixture(unresolved: true));
        Assert.IsFalse(model.SelectAllPackagesCommand.CanExecute(null));
        model.SelectAllPackagesCommand.Execute(null);
        Assert.HasCount(0, Selected(model));
        Assert.IsFalse(model.ReviewRows.Single(row => row.Repository.Id == "first" && row.PackageId == "partial").Eligible);
        Assert.IsFalse(model.CanReview);
        Assert.IsTrue(model.ChangeRepositorySummary.Contains("review blocked", StringComparison.Ordinal));
    });

    [TestMethod]
    [DataRow(VersionDeclarationKind.Unresolved, true)]
    [DataRow(VersionDeclarationKind.Property, false)]
    public Task KnownUnsafeVersionOwnershipIsSkipped(VersionDeclarationKind kind, bool unique) => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        var inventory = analysis.Inventories.Single(item => item.Source.Id == "first");
        inventory = inventory with { Usages = [.. inventory.Usages.Select(usage => usage.PackageId == "upgrade"
            ? usage with { Declaration = usage.Declaration with { Kind = kind, IsUniquelyOwned = unique } } : usage)] };
        analysis = TestData.Analyze([.. analysis.Inventories.Select(item => item.Source.Id == "first" ? inventory : item)]);
        var ports = new Ports(analysis);
        using var model = CreateModel(ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(analysis);
        model.SelectUpgradesCommand.Execute(null);
        Assert.IsFalse(Selected(model).Contains("upgrade"));
        Assert.IsTrue(model.ReviewStatus.Contains("1 unsafe ownership", StringComparison.Ordinal));
    });

    [TestMethod]
    public Task TargetsRefreshCountsAndInvalidateApprovalWithoutChangingOtherRepositorySelection() => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        var ports = new Ports(analysis);
        using var model = CreateModel(ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(analysis);
        model.ReviewRows.Single(row => row.Repository.Id == "first" && row.PackageId == "upgrade").IsSelected = true;
        await model.ReviewCommand.ExecuteAsync(null);
        Assert.IsTrue(model.CanExecute);
        Assert.IsTrue(model.ChangeRepositorySummary.Contains("3 awaiting targets", StringComparison.Ordinal));
        var notifications = 0;
        model.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(MainViewModel.ChangeRepositorySummary)) notifications++; };
        model.Packages.Single(row => row.PackageId == "elekta.custom").CustomVersionText = "2.0";
        Assert.IsFalse(model.CanExecute);
        Assert.IsTrue(notifications > 0);
        Assert.IsTrue(model.ChangeRepositorySummary.Contains("2 awaiting targets", StringComparison.Ordinal));
        Assert.HasCount(1, Selected(model));
        Assert.HasCount(0, model.ReviewRows.Where(row => row.Repository.Id == "second" && row.IsSelected));
    });

    [TestMethod]
    public Task ImmutableScannedChoicesPreserveValidIdAndFallBackWhenRemoved() => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        using var model = CreateModel(new Ports(analysis));
        await model.SetAnalysisAsync(analysis);
        model.SelectedChangeRepository = model.RepositoryChoices.Single(choice => choice.Source.Id == "second");
        model.Repositories = [new(TestData.Repo("editable-only"))];
        await model.SetAnalysisAsync(analysis with { Id = Guid.NewGuid() });
        Assert.AreEqual("second", model.SelectedChangeRepository?.Source.Id);
        Assert.IsFalse(model.RepositoryChoices.Any(choice => choice.Source.Id == "editable-only"));
        await model.SetAnalysisAsync(TestData.Analyze([.. analysis.Inventories.Where(item => item.Source.Id != "second")]));
        Assert.AreEqual("first", model.SelectedChangeRepository?.Source.Id);
        Assert.HasCount(8, model.VisibleReviewRows.Cast<ReviewRow>());
        await model.SetAnalysisAsync(TestData.Analyze(analysis.Inventories[0]));
        Assert.IsNull(model.SelectedChangeRepository);
        Assert.HasCount(0, model.VisibleReviewRows.Cast<ReviewRow>());
        Assert.IsFalse(model.CanReview);
    });

    [TestMethod]
    [DataRow(InventoryStatus.Complete)]
    [DataRow(InventoryStatus.Failed)]
    public Task EmptyAndFailedICScopesStayVisibleButCannotReview(InventoryStatus status) => OnDispatcherAsync(async () =>
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var first = TestData.Repo("empty");
        var inventory = TestData.Inventory(first) with { Status = status };
        var analysis = TestData.Analyze(TestData.Inventory(platform), inventory);
        using var model = CreateModel(new Ports(analysis));
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(analysis);
        Assert.AreEqual("empty", model.SelectedChangeRepository?.Source.Id);
        Assert.HasCount(0, model.VisibleReviewRows.Cast<ReviewRow>());
        Assert.IsFalse(model.CanReview);
        Assert.IsFalse(model.SelectAllPackagesCommand.CanExecute(null));
        Assert.IsTrue(model.ChangeRepositorySummary.Contains(status == InventoryStatus.Complete ? "Empty inventory" : "Failed", StringComparison.Ordinal));
        await model.ReviewCommand.ExecuteAsync(null);
        Assert.IsNotNull(model.UserError);
    });

    [TestMethod]
    public Task AlignedOnlyScopeSelectsAllAndReviewRemainsNoOp() => OnDispatcherAsync(async () =>
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var first = TestData.Repo("first");
        var analysis = TestData.Analyze(TestData.Inventory(platform, TestData.Usage(platform, "aligned", "2.0")),
            TestData.Inventory(first, TestData.Usage(first, "aligned", "2.0")));
        var ports = new Ports(analysis);
        using var model = CreateModel(ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(analysis);
        model.SelectUpgradesCommand.Execute(null);
        Assert.HasCount(0, Selected(model));
        Assert.IsTrue(model.ReviewStatus.Contains("0 selected", StringComparison.Ordinal));
        model.SelectAllPackagesCommand.Execute(null);
        Assert.HasCount(1, Selected(model));
        await model.ReviewCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.IsFalse(model.CanExecute);
        Assert.IsTrue(model.ReviewStatus.Contains("no changes", StringComparison.Ordinal));
        Assert.AreEqual(0, ports.Approvals);
        Assert.AreEqual(0, ports.Executions);
    });

    [TestMethod]
    public Task StaleScopeAndNoTargetScopeNeverPerformBulkOrReviewWork() => OnDispatcherAsync(async () =>
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var first = TestData.Repo("first");
        var analysis = TestData.Analyze(TestData.Inventory(platform), TestData.Inventory(first, TestData.Usage(first, "soup", "1.0")));
        var ports = new Ports(analysis);
        using var model = CreateModel(ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(analysis);
        model.SelectAllPackagesCommand.Execute(null);
        Assert.HasCount(0, Selected(model));
        Assert.IsTrue(model.ReviewStatus.Contains("1 target missing", StringComparison.Ordinal));
        Assert.IsFalse(model.CanReview);
        model.Repositories.Single(row => row.Source.Id == "first").Ref = "changed";
        Assert.IsFalse(model.SelectUpgradesCommand.CanExecute(null));
        Assert.IsFalse(model.SelectAllPackagesCommand.CanExecute(null));
        Assert.IsFalse(model.ClearPackageSelectionCommand.CanExecute(null));
        await model.ReviewCommand.ExecuteAsync(null);
        Assert.IsNotNull(model.UserError);
        Assert.AreEqual(0, ports.Approvals);
        Assert.AreEqual(0, ports.Executions);
    });

    private static string[] Selected(MainViewModel model) => model.VisibleReviewRows.Cast<ReviewRow>().Where(row => row.IsSelected).Select(row => row.PackageId).ToArray();

    [TestMethod]
    public Task OutcomesAreScopedByRepositoryIdAndRetainTheirBranchAndPullRequestLink() => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        using var model = CreateModel(new Ports(analysis));
        await model.SetAnalysisAsync(analysis);
        var first = new RepositoryUpdateResult("first", RepositoryUpdateStatus.Completed, "soup-align/first",
            PullRequestUrl: "https://dev.azure.com/example/platform/_git/first/pullrequest/1");
        var second = new RepositoryUpdateResult("second", RepositoryUpdateStatus.PushedOnly, "soup-align/second", Detail: "PR not created");
        model.Results = [first, second];
        Assert.AreSame(first, model.VisibleResults.Cast<RepositoryUpdateResult>().Single());
        Assert.IsTrue(model.ChangeResultSummary.Contains("first | main | 1 recorded outcomes", StringComparison.Ordinal));
        Assert.AreEqual("soup-align/first", model.VisibleResults.Cast<RepositoryUpdateResult>().Single().Branch);
        Assert.AreEqual(first.PullRequestUrl, model.VisibleResults.Cast<RepositoryUpdateResult>().Single().PullRequestUrl);
        model.SelectedChangeRepository = model.RepositoryChoices.Single(choice => choice.Source.Id == "second");
        Assert.AreSame(second, model.VisibleResults.Cast<RepositoryUpdateResult>().Single());
        Assert.IsTrue(model.ChangeResultSummary.Contains("second | main | 1 recorded outcomes", StringComparison.Ordinal));
        Assert.HasCount(2, model.Results);
    });

    private static AnalysisResult Fixture(bool unresolved = false)
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var first = TestData.Repo("first");
        var second = TestData.Repo("second");
        return TestData.Analyze(
            TestData.Inventory(platform, TestData.Usage(platform, "upgrade", "2.0"), TestData.Usage(platform, "downgrade", "2.0"),
                TestData.Usage(platform, "aligned", "2.0"), TestData.Usage(platform, "mixed", "2.0"), TestData.Usage(platform, "partial", "2.0")),
            TestData.Inventory(first, TestData.Usage(first, "upgrade", "1.0"), TestData.Usage(first, "downgrade", "3.0"),
                TestData.Usage(first, "aligned", "2.0"), TestData.Usage(first, "soup", "1.0"),
                TestData.Usage(first, "elekta.empty", "1.0"), TestData.Usage(first, "elekta.custom", "1.0"),
                TestData.Usage(first, "mixed", "1.0"), TestData.Usage(first, "mixed", "3.0", project: "src/Other/Other.csproj"),
                TestData.Usage(first, "partial", "1.0"), TestData.Usage(first, "partial", unresolved ? "[1.0,3.0)" : "2.0", project: "src/Other/Other.csproj")),
            TestData.Inventory(second, TestData.Usage(second, "upgrade", "3.0")),
            TestData.Inventory(TestData.Repo("emulator", RepositoryCategory.ICEmulator)));
    }

    private static MainViewModel CreateModel(Ports ports) => new(ports, ports, ports,
        new ScanWorkflow(ports, ports, ports), new ExportReportWorkflow(ports), new UpdateWorkflow(ports), ports);

    private static Task OnDispatcherAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); }
            }));
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class Ports(AnalysisResult analysis) : IConfigurationService, ISettingsStore, IAccessTokenProvider,
        IRepositorySnapshotProvider, IPackageScanner, INuGetCandidateProvider, IUpdateExecutor, IExcelReportWriter, IUserInteraction
    {
        public ChangePlan? Approved { get; private set; }
        public ReportSelection? ExportedSelection { get; private set; }
        public AnalysisResult? ExportedAnalysis { get; private set; }
        public int Executions { get; private set; }
        public UpdateStatus? ExecutionStatus { get; set; }
        public List<ChangePlan> ExecutedPlans { get; } = [];
        public int Approvals { get; private set; }
        public int Previews { get; private set; }
        public ChangePlan? Preview { get; private set; }
        public TaskCompletionSource? ConfigurationWait { get; set; }
        public TaskCompletionSource ConfigurationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool AllowScan { get; init; }
        public bool FailConfigurationLoad { get; set; }
        public bool FailSettingsSave { get; set; }
        public LoadedConfiguration Configuration { get; set; } = new(
            new(1, analysis.ManifestVersion, [.. analysis.OriginalRefs.Where(source => source.Category == RepositoryCategory.Platform)]),
            analysis.ManifestHash, AnalysisRules.Default, new(1, "", "", "", ["scope"]));
        public UserSettings Settings { get; set; } = UserSettings.Default with
            { Repositories = [.. analysis.OriginalRefs.Where(source => source.Category != RepositoryCategory.Platform)] };
        public async Task<LoadedConfiguration> LoadAsync(CancellationToken cancellationToken)
        {
            if (FailConfigurationLoad) throw new IOException("Fixture configuration read failed.");
            if (ConfigurationWait is not null)
            {
                ConfigurationEntered.TrySetResult();
                await ConfigurationWait.Task.WaitAsync(cancellationToken);
            }
            return Configuration;
        }
        Task<UserSettings> ISettingsStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);
        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken)
        {
            if (FailSettingsSave) throw new IOException("Fixture settings write failed.");
            Settings = settings;
            return Task.CompletedTask;
        }
        public Task<AccessToken> GetTokenAsync(bool allowInteractive, CancellationToken cancellationToken) => throw new AssertFailedException("No authentication expected.");
        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<SourceSnapshot> GetSnapshotAsync(RepositorySource source, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) =>
            AllowScan ? Task.FromResult(TestData.Inventory(source).Snapshot!) : throw new AssertFailedException("No scan expected.");
        public PackageScanResult Scan(SourceSnapshot snapshot, AnalysisRules rules, CancellationToken cancellationToken) =>
            AllowScan ? new([], []) : throw new AssertFailedException("No scan expected.");
        public Task<NuGetCandidate> GetCandidateAsync(string packageId, CancellationToken cancellationToken) => throw new AssertFailedException("No query expected.");
        public Task<UpdateResult> ExecuteAsync(ChangePlan approvedPlan, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
        {
            Executions++;
            if (ExecutionStatus is { } status)
            {
                ExecutedPlans.Add(approvedPlan);
                return Task.FromResult(new UpdateResult(approvedPlan.Id, status,
                    [new("first", status == UpdateStatus.Completed ? RepositoryUpdateStatus.Completed : RepositoryUpdateStatus.PushedOnly,
                        "soup-align/" + approvedPlan.Id.ToString("N"), Detail: "Fixture execution")]));
            }
            throw new AssertFailedException("No executor call expected.");
        }
        public Task WriteAsync(AnalysisResult value, string path, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task WriteAsync(AnalysisResult value, string path, IProgress<OperationProgress>? progress, CancellationToken cancellationToken, ReportSelection? selection)
        {
            ExportedAnalysis = value;
            ExportedSelection = selection;
            return Task.CompletedTask;
        }
        public string? ChooseReportPath(string? directory) => "fixture.xlsx";
        public bool Approve(ChangePlan plan) { Approvals++; Approved = plan; return true; }
        public void ShowPreview(ChangePlan plan, string blockers) { Previews++; Preview = plan; Assert.IsFalse(plan.CanExecute); StringAssert.Contains(blockers, "IC baseline incomplete"); }
        public void ShowSettings(MainViewModel model) => throw new AssertFailedException("No dialog expected.");
    }
}