using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Desktop.Presentation;
using Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using NuGet.Versioning;

namespace Elekta.IC.Tools.SoupAnalysis.Tests;

[TestClass]
public sealed class RepositoryAnalysisTests
{
    [TestMethod]
    public Task AnalysisDefaultsToFirstICLocalVersionsAndReferences() => OnDispatcherAsync(async () =>
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var first = TestData.Repo("first");
        var second = TestData.Repo("second");
        var analysis = TestData.Analyze(TestData.Inventory(platform, TestData.Usage(platform, "shared", "2.0")),
            TestData.Inventory(first, TestData.Usage(first, "shared", "1.0", TestClassification.Test)),
            TestData.Inventory(second, TestData.Usage(second, "shared", "3.0"), TestData.Usage(second, "only-second", "1.0")));
        using var model = CreateModel(analysis);
        await model.SetAnalysisAsync(analysis);
        var shared = model.Packages.Single(row => row.PackageId == "shared");
        Assert.AreEqual("1.0", shared.CurrentVersions);
        Assert.AreEqual("Upgrade", shared.Action);
        Assert.HasCount(1, model.PackagesView.Cast<PackageRow>());
        Assert.IsTrue(model.ReferencesView.Cast<ReferenceRow>().All(row => row.RepositoryId == first.Id));
    });

    [TestMethod]
    public Task LocalActionsTestStatusAndGlobalTargetsSurviveSwitches() => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        using var model = CreateModel(analysis);
        await model.SetAnalysisAsync(analysis);
        var shared = model.Packages.Single(row => row.PackageId == "shared");
        var soup = model.Packages.Single(row => row.PackageId == "soup");
        Assert.AreEqual("2.0.0", shared.PlatformMax);
        Assert.AreEqual("3.0.0", soup.ICMax);
        Assert.AreEqual("4.0.0", soup.NuGetMax);
        Assert.HasCount(2, soup.Choices);
        Assert.AreEqual("AwaitingSelection", soup.Action);
        Assert.AreEqual("Yes", soup.TestOnlyStatus);
        soup.SelectedTarget = soup.Choices.Single(choice => choice.Source == TargetSource.ICMax);
        Assert.AreEqual("Upgrade", soup.Action);
        Select(model, "second");
        Assert.AreSame(shared, model.PackagesView.Cast<PackageRow>().Single(row => row.PackageId == "shared"));
        Assert.AreEqual("3.0", shared.CurrentVersions);
        Assert.AreEqual("Downgrade", shared.Action);
        Assert.AreEqual("No", shared.TestOnlyStatus);
        Assert.AreEqual("Aligned", soup.Action);
        Assert.AreEqual("No", soup.TestOnlyStatus);
        Assert.AreEqual("3.0.0", soup.Target);
        Assert.AreEqual("NotApplicable", model.Packages.Single(row => row.PackageId == "elekta.internal").Action);
        soup.SelectedTarget = soup.Choices.Single(choice => choice.Source == TargetSource.NuGetCandidate);
        Assert.AreEqual("Upgrade", soup.Action);
        Select(model, "first");
        Assert.AreEqual("4.0.0", soup.Target);
        Assert.AreEqual("Upgrade", soup.Action);
        Assert.AreEqual("4.0.0", model.ReviewRows.Single(row => row.Repository.Id == "second" && row.PackageId == "soup").NewVersion);
        Assert.AreSame(analysis, model.Analysis);
    });

    [TestMethod]
    public Task SwitchingResetsEveryDisplayFilterAndSelectionWithoutChangingUpdateSelections() => OnDispatcherAsync(async () =>
    {
        using var model = CreateModel(Fixture());
        await model.SetAnalysisAsync(Fixture());
        var choice = model.RepositoryChoices.Single(row => row.Source.Id == "first");
        model.SelectedChangeRepository = choice;
        var review = model.ReviewRows.First(row => row.Repository.Id == "first");
        review.IsSelected = true;
        model.SelectedPackage = model.Packages.Single(row => row.PackageId == "shared");
        model.Search = "missing";
        model.OwnershipFilter = "Internal";
        model.ActionFilter = "Aligned";
        model.CategoryFilter = "Platform";
        model.ReferenceCategoryFilter = "Platform";
        Select(model, "second");
        Assert.IsNull(model.SelectedPackage);
        Assert.AreEqual("", model.Search);
        Assert.AreEqual("All ownership", model.OwnershipFilter);
        Assert.AreEqual("All actions", model.ActionFilter);
        Assert.AreEqual("All categories", model.CategoryFilter);
        Assert.AreEqual("All references", model.ReferenceCategoryFilter);
        Assert.AreSame(choice, model.SelectedChangeRepository);
        Assert.IsTrue(review.IsSelected);
        Assert.IsTrue(model.PackagesView.Cast<PackageRow>().Any(row => row.PackageId == "only-second"));
        Assert.IsTrue(model.ReferencesView.Cast<ReferenceRow>().All(row => row.RepositoryId == "second"));
        model.SelectedPackage = model.Packages.Single(row => row.PackageId == "shared");
        Assert.HasCount(1, model.ReferencesView.Cast<ReferenceRow>());
        model.ClearDetailsCommand.Execute(null);
        Assert.HasCount(4, model.ReferencesView.Cast<ReferenceRow>());
        Assert.IsTrue(model.ReferencesView.Cast<ReferenceRow>().All(row => row.RepositoryId == "second"));
    });

    [TestMethod]
    public Task TestOnlyClassificationIsLocalAndAllPackagesRemainVisible() => OnDispatcherAsync(async () =>
    {
        using var model = CreateModel(Fixture());
        await model.SetAnalysisAsync(Fixture());
        CollectionAssert.AreEquivalent(new[] { "shared", "soup" }, model.PackagesView.Cast<PackageRow>().Select(row => row.PackageId).ToArray());
        Assert.IsTrue(model.PackagesView.Cast<PackageRow>().All(row => row.IsTestOnly && row.TestOnlyStatus == "Yes"));
        Select(model, "second");
        Assert.HasCount(4, model.PackagesView.Cast<PackageRow>());
        Assert.IsTrue(model.PackagesView.Cast<PackageRow>().All(row => !row.IsTestOnly && row.TestOnlyStatus == "No"));
        Select(model, null);
        Assert.AreEqual("Global package summary", model.AnalysisScopeTitle);
        Assert.AreEqual("Any: first", model.Packages.Single(row => row.PackageId == "shared").TestOnlyStatus);
        Assert.HasCount(4, model.PackagesView.Cast<PackageRow>());
        Assert.HasCount(2, model.PackagesView.Cast<PackageRow>().Where(row => row.IsTestOnly));
    });

    [TestMethod]
    [DataRow("platform-two", "shared", "2.0")]
    [DataRow("emulator", "emulator-only", "99.0")]
    public Task ReadOnlySourcesIncludeAllLocalPackagesWithoutUpdates(string repository, string package, string version) => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        using var model = CreateModel(analysis);
        await model.SetAnalysisAsync(analysis);
        Select(model, repository);
        var row = model.PackagesView.Cast<PackageRow>().Single(item => item.PackageId == package);
        Assert.AreEqual(version, row.CurrentVersions);
        Assert.AreEqual("NotApplicable", row.Action);
        Assert.IsFalse(row.IsTargetEditable);
        Assert.IsTrue(model.RepositoryAnalysisSummary.Contains("read-only", StringComparison.Ordinal));
        Assert.IsFalse(model.RepositoryChoices.Any(choice => choice.Source.Id == repository));
        Assert.IsFalse(model.ReviewRows.Any(review => review.Repository.Id == repository));
        Assert.AreEqual("3.0.0", model.Packages.Single(item => item.PackageId == "soup").ICMax);
        Assert.AreEqual("2.0.0", model.Packages.Single(item => item.PackageId == "shared").PlatformMax);
        Assert.AreEqual("No", model.PackagesView.Cast<PackageRow>().First(item => item.PackageId == "shared" || item.PackageId == "soup").TestOnlyStatus);
    });

    [TestMethod]
    [DataRow(InventoryStatus.Unresolved)]
    [DataRow(InventoryStatus.Failed)]
    public Task PartialInventoryNeverConfirmsTestOnlyAndDisplaysBlockedState(InventoryStatus status) => OnDispatcherAsync(async () =>
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("partial");
        var inventory = TestData.Inventory(ic, TestData.Usage(ic, "shared", "1.0", TestClassification.Test)) with
            { Status = status, Issues = [new("failure", "Incomplete source")], Snapshot = status == InventoryStatus.Failed ? null : TestData.Inventory(ic).Snapshot };
        var analysis = TestData.Analyze(TestData.Inventory(platform, TestData.Usage(platform, "shared", "2.0")), inventory);
        using var model = CreateModel(analysis);
        await model.SetAnalysisAsync(analysis);
        var row = model.Packages.Single();
        Assert.AreEqual("Unconfirmed", row.TestOnlyStatus);
        Assert.IsFalse(row.IsTestOnly);
        Assert.AreEqual("Unresolved", row.Action);
        Assert.IsTrue(model.RepositoryAnalysisSummary.Contains(status == InventoryStatus.Failed ? "RepoFailed" : "Unresolved", StringComparison.Ordinal));
        Assert.IsTrue(model.RepositoryAnalysisSummary.Contains("Incomplete source", StringComparison.Ordinal));
        Assert.IsTrue(model.RepositoryAnalysisSummary.Contains("blocked", StringComparison.Ordinal));
        Assert.IsFalse(model.ReferencesView.Cast<ReferenceRow>().Single().TestOnly);
        Assert.HasCount(1, model.IssuesView.Cast<IssueRow>());
        Assert.HasCount(1, model.PackagesView.Cast<PackageRow>());
    });

    [TestMethod]
    public Task FailedPlatformMakesOwnershipAndUpdatesUnconfirmed() => OnDispatcherAsync(async () =>
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("first");
        var analysis = TestData.Analyze(TestData.Inventory(platform) with { Status = InventoryStatus.Failed, Snapshot = null, Issues = [new("failed", "Platform unavailable")] },
            TestData.Inventory(ic, TestData.Usage(ic, "soup", "1.0")));
        using var model = CreateModel(analysis);
        await model.SetAnalysisAsync(analysis);
        Assert.AreEqual("Unconfirmed", model.Packages.Single().SourceLabel);
        Assert.AreEqual("Unresolved", model.Packages.Single().Action);
        Assert.IsTrue(model.RepositoryAnalysisSummary.Contains("blocked", StringComparison.Ordinal));
        Assert.HasCount(0, model.IssuesView.Cast<IssueRow>());
        Select(model, "platform");
        Assert.IsTrue(model.RepositoryAnalysisSummary.Contains("RepoFailed", StringComparison.Ordinal));
        Assert.IsTrue(model.RepositoryAnalysisSummary.Contains("absence unconfirmed", StringComparison.Ordinal));
        Assert.HasCount(1, model.IssuesView.Cast<IssueRow>());
        Assert.HasCount(0, model.PackagesView.Cast<PackageRow>());
    });

    [TestMethod]
    public Task UnknownClassificationNeverClaimsTestOnly() => OnDispatcherAsync(async () =>
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("first");
        var analysis = TestData.Analyze(TestData.Inventory(platform), TestData.Inventory(ic, TestData.Usage(ic, "unknown-test", "1.0", TestClassification.Unknown)));
        using var model = CreateModel(analysis);
        await model.SetAnalysisAsync(analysis);
        Assert.AreEqual("Unconfirmed", model.Packages.Single().TestOnlyStatus);
        Assert.IsFalse(model.Packages.Single().IsTestOnly);
    });

    [TestMethod]
    [DataRow("2.0", "Upgrade")]
    [DataRow("3.0", "Mixed")]
    public void MixedMeansUpgradesAndDowngradesWithinSameRepository(string secondVersion, string expected)
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("first");
        var inventory = TestData.Inventory(ic, TestData.Usage(ic, "shared", "1.0") with { Condition = "Debug" },
            TestData.Usage(ic, "shared", secondVersion, TestClassification.Test, "src/Test.One.csproj") with { Condition = "Release" });
        var analysis = TestData.Analyze(TestData.Inventory(platform, TestData.Usage(platform, "shared", "2.0")), inventory);
        var row = new PackageRow(analysis.Packages.Single());
        row.SetRepositoryScope(ic, inventory.Usages, true);
        Assert.AreEqual(expected, row.Action);
        Assert.AreEqual("No", row.TestOnlyStatus);
        Assert.AreEqual(2, row.ProjectCount);
    }

    [TestMethod]
    public Task StableScannedChoicesIncludeEmptySourcesAndPreserveIdAcrossAnalyses() => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        using var model = CreateModel(analysis);
        await model.SetAnalysisAsync(analysis);
        Assert.HasCount(8, model.AnalysisRepositoryChoices);
        Select(model, "empty");
        Assert.HasCount(0, model.PackagesView.Cast<PackageRow>());
        Assert.IsTrue(model.RepositoryAnalysisSummary.Contains("Complete", StringComparison.Ordinal));
        Select(model, "second");
        model.Repositories = [new(TestData.Repo("editable-list-only"))];
        await model.SetAnalysisAsync(analysis with { Id = Guid.NewGuid() });
        Assert.AreEqual("second", model.SelectedAnalysisRepository?.Id);
        Assert.IsFalse(model.AnalysisRepositoryChoices.Any(choice => choice.Id == "editable-list-only"));
        var firstOnly = TestData.Analyze(analysis.Inventories[0], analysis.Inventories.Single(inventory => inventory.Source.Id == "first"));
        await model.SetAnalysisAsync(firstOnly);
        Assert.AreEqual("first", model.SelectedAnalysisRepository?.Id);
        Select(model, null);
        await model.SetAnalysisAsync(analysis);
        Assert.IsNull(model.SelectedAnalysisRepository?.Id);
        Assert.IsNotNull(model.SelectedAnalysisRepository);
    });

    [TestMethod]
    public Task DefaultsToFirstSourceThenGlobalWhenNoICExists() => OnDispatcherAsync(async () =>
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var analysis = TestData.Analyze(TestData.Inventory(platform));
        using var model = CreateModel(analysis);
        await model.SetAnalysisAsync(analysis);
        Assert.AreEqual(platform.Id, model.SelectedAnalysisRepository?.Id);
        await model.SetAnalysisAsync(TestData.Analyze());
        Assert.HasCount(1, model.AnalysisRepositoryChoices);
        Assert.IsNull(model.SelectedAnalysisRepository?.Id);
        Assert.AreEqual("Global package summary", model.AnalysisScopeTitle);
    });

    [TestMethod]
    public Task IssuesWithDuplicateDisplayNamesStayScopedById() => OnDispatcherAsync(async () =>
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var first = TestData.Repo("first") with { Name = "same-name" };
        var second = TestData.Repo("second") with { Name = "same-name" };
        var analysis = TestData.Analyze(TestData.Inventory(platform), TestData.Inventory(first) with { Issues = [new("first", "first issue", BlocksUpdates: false)] },
            TestData.Inventory(second) with { Issues = [new("second", "second issue", BlocksUpdates: false)] });
        using var model = CreateModel(analysis);
        await model.SetAnalysisAsync(analysis);
        Assert.AreEqual("first", model.IssuesView.Cast<IssueRow>().Single().Code);
        Select(model, "second");
        Assert.AreEqual("second", model.IssuesView.Cast<IssueRow>().Single().Code);
        Select(model, null);
        Assert.HasCount(2, model.IssuesView.Cast<IssueRow>());
    });

    [TestMethod]
    public Task BrowsingPreservesApprovalAndExportUsesFullAnalysisAndGlobalSelection() => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        var ports = new Ports(analysis);
        using var model = CreateModel(analysis, ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(analysis);
        var soup = model.Packages.Single(row => row.PackageId == "soup");
        soup.SelectedTarget = soup.Choices.Single(choice => choice.Source == TargetSource.NuGetCandidate);
        model.SelectedChangeRepository = model.RepositoryChoices.Single(choice => choice.Source.Id == "first");
        model.ReviewRows.Single(row => row.Repository.Id == "first" && row.PackageId == "shared").IsSelected = true;
        model.ReviewRows.Single(row => row.Repository.Id == "first" && row.PackageId == "soup").IsSelected = true;
        await model.ReviewCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.IsTrue(model.CanExecute);
        var approval = model.ReviewStatus;
        Select(model, "second");
        Select(model, "emulator");
        Select(model, null);
        Select(model, "empty");
        model.ShowAllScanIssues = true;
        model.Search = "soup";
        model.ShowAllScanIssues = false;
        Assert.IsTrue(model.CanExecute);
        Assert.AreEqual(approval, model.ReviewStatus);
        Assert.IsFalse(model.IsAnalysisStale);
        await model.ExportCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.AreSame(analysis, ports.ExportedAnalysis);
        Assert.AreEqual(TargetSource.NuGetCandidate, ports.ExportedSelection!.Targets.Single(target => target.PackageId == "soup").Source);
        Assert.HasCount(2, ports.ExportedSelection.Selections);
        Assert.IsTrue(ports.ExportedSelection.Selections.All(selection => selection.RepositoryId == "first"));
        Assert.IsNotNull(ports.ExportedSelection.ReviewPlan);
        Select(model, "second");
        soup.SelectedTarget = soup.Choices.Single(choice => choice.Source == TargetSource.ICMax);
        Assert.IsFalse(model.CanExecute);
    });

    [TestMethod]
    public Task AwaitingTargetsDoNotCountAsNeededChangesAndFilterUsesLocalActions() => OnDispatcherAsync(async () =>
    {
        using var model = CreateModel(Fixture());
        await model.SetAnalysisAsync(Fixture());
        Assert.IsTrue(model.RepositoryAnalysisSummary.Contains("1 need changes", StringComparison.Ordinal));
        model.ActionFilter = "Need changes";
        model.PackagesView.Refresh();
        Assert.AreEqual("shared", model.PackagesView.Cast<PackageRow>().Single().PackageId);
        var soup = model.Packages.Single(row => row.PackageId == "soup");
        soup.SelectedTarget = soup.Choices.Single(choice => choice.Source == TargetSource.ICMax);
        Assert.IsTrue(model.RepositoryAnalysisSummary.Contains("2 need changes", StringComparison.Ordinal));
        Assert.HasCount(2, model.PackagesView.Cast<PackageRow>());
        Select(model, "second");
        Assert.IsTrue(model.RepositoryAnalysisSummary.Contains("4 packages", StringComparison.Ordinal));
        Assert.IsTrue(model.RepositoryAnalysisSummary.Contains("1 need changes", StringComparison.Ordinal));
        model.ActionFilter = "Need changes";
        model.PackagesView.Refresh();
        Assert.AreEqual("Downgrade", model.PackagesView.Cast<PackageRow>().Single().Action);
    });

    [TestMethod]
    public Task MissingInventoryRemainsSelectableAndNeverLooksHealthyOrProvesAbsence() => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        var missing = TestData.Repo("missing");
        analysis = analysis with { OriginalRefs = analysis.OriginalRefs.Add(missing), CompleteIC = false };
        using var model = CreateModel(analysis);
        await model.SetAnalysisAsync(analysis);
        Select(model, missing.Id);
        Assert.HasCount(0, model.PackagesView.Cast<PackageRow>());
        Assert.HasCount(0, model.ReferencesView.Cast<ReferenceRow>());
        Assert.IsTrue(model.RepositoryAnalysisSummary.Contains("Unresolved / no inventory", StringComparison.Ordinal));
        Assert.IsTrue(model.RepositoryAnalysisSummary.Contains("absence unconfirmed", StringComparison.Ordinal));
        Assert.IsTrue(model.RepositoryAnalysisSummary.Contains("blocked", StringComparison.Ordinal));
    });

    [TestMethod]
    public Task PackageMembershipUsesCaseInsensitiveGlobalIds() => OnDispatcherAsync(async () =>
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("first");
        var analysis = TestData.Analyze(TestData.Inventory(platform, TestData.Usage(platform, "shared", "2.0")),
            TestData.Inventory(ic, TestData.Usage(ic, "SHARED", "1.0", TestClassification.Test)));
        using var model = CreateModel(analysis);
        await model.SetAnalysisAsync(analysis);
        var package = model.PackagesView.Cast<PackageRow>().Single();
        Assert.AreEqual("1.0", package.CurrentVersions);
        Assert.AreEqual("Upgrade", package.Action);
        Assert.AreEqual("Yes", package.TestOnlyStatus);
        model.SelectedPackage = package;
        Assert.AreEqual("SHARED", model.ReferencesView.Cast<ReferenceRow>().Single().PackageId);
        Assert.HasCount(1, model.Packages);
    });

    private static void Select(MainViewModel model, string? id) => model.SelectedAnalysisRepository = model.AnalysisRepositoryChoices.Single(choice => choice.Id == id);

    [TestMethod]
    public Task ExportRetainsUnselectedCustomChoiceAndCurrentApproval() => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        var ports = new Ports(analysis);
        using var model = CreateModel(analysis, ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(analysis);
        model.Packages.Single(row => row.PackageId == "elekta.internal").CustomVersionText = "2.0";
        model.SelectedChangeRepository = model.RepositoryChoices.Single(choice => choice.Source.Id == "first");
        model.ReviewRows.Single(row => row.Repository.Id == "first" && row.PackageId == "shared").IsSelected = true;
        await model.ReviewCommand.ExecuteAsync(null);
        Assert.IsTrue(model.CanExecute);
        await model.ExportCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.IsTrue(model.CanExecute);
        Assert.AreEqual("2.0.0", ports.ExportedSelection!.Targets.Single(target => target.PackageId == "elekta.internal").CustomVersion);
        var approved = ports.ExportedSelection.ReviewPlan;
        Assert.IsNotNull(approved);
        Assert.IsTrue(approved.CanExecute);
        Assert.IsTrue(approved.Matches(analysis));
        Assert.HasCount(1, approved.Changes);
        Assert.AreEqual("shared", approved.Changes[0].PackageId);
        Assert.HasCount(1, ports.ExportedSelection.Selections);
        ports.ExportedSelection.Validate(analysis);
        Select(model, "second");
        await model.ExportCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.IsTrue(model.CanExecute);
        Assert.AreSame(approved, ports.ExportedSelection!.ReviewPlan);
        model.Packages.Single(row => row.PackageId == "elekta.internal").CustomVersionText = "[2.0,3.0)";
        Assert.IsFalse(model.CanExecute);
        await model.ExportCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.IsNull(ports.ExportedSelection!.ReviewPlan);
        await model.ReviewCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.IsTrue(model.CanExecute);
        model.Repositories.Single(row => row.Source.Id == "first").Ref += "-changed";
        Assert.IsTrue(model.IsAnalysisStale);
        Assert.IsFalse(model.CanReview);
        Assert.IsFalse(model.CanExecute);
        await model.ExportCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.IsNull(ports.ExportedSelection!.ReviewPlan);
    });

    [TestMethod]
    public Task CustomTargetPersistsAcrossScopesSettingsReportAndApprovalThenInvalidInputClearsIt() => OnDispatcherAsync(async () =>
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var first = TestData.Repo("first");
        var second = TestData.Repo("second");
        var analysis = TestData.Analyze(TestData.Inventory(platform),
            TestData.Inventory(first, TestData.Usage(first, "elekta.internal", "1.0", TestClassification.Test)),
            TestData.Inventory(second, TestData.Usage(second, "elekta.internal", "3.0")));
        var ports = new Ports(analysis);
        using var model = CreateModel(analysis, ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(analysis);
        var row = model.Packages.Single();
        row.CustomVersionText = "2.0-preview.1";
        Assert.AreEqual("Upgrade", row.Action);
        Select(model, "second");
        Assert.AreEqual("Downgrade", row.Action);
        Assert.AreEqual("No", row.TestOnlyStatus);
        Assert.AreEqual("2.0-preview.1", row.CustomVersionText);
        model.SelectedChangeRepository = model.RepositoryChoices.Single(choice => choice.Source.Id == "first");
        model.ReviewRows.Single(review => review.Repository.Id == "first").IsSelected = true;
        await model.ReviewCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.IsTrue(model.CanExecute);
        Select(model, "first");
        Assert.IsTrue(model.CanExecute);
        Assert.AreEqual("Yes", row.TestOnlyStatus);
        await model.ExportCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        var exported = ports.ExportedSelection!.Targets.Single();
        Assert.AreEqual(TargetSource.Custom, exported.Source);
        Assert.AreEqual("2.0.0-preview.1", exported.CustomVersion);
        Assert.AreEqual("2.0.0-preview.1", ports.ExportedSelection.ReviewPlan!.Changes.Single().Target.ToNormalizedString());
        Assert.AreEqual("2.0.0-preview.1", ports.SavedSettings!.CandidatePreferences.Single().CustomVersion);
        await model.SetAnalysisAsync(analysis);
        row = model.Packages.Single();
        Assert.AreEqual("2.0.0-preview.1", row.CustomVersionText);
        Assert.AreEqual(TargetSource.Custom, row.SelectedTarget!.Source);
        row.CustomVersionText = "[2.0,3.0)";
        Assert.IsNull(row.SelectedTarget);
        Assert.IsFalse(model.CanExecute);
        await model.ExportCommand.ExecuteAsync(null);
        Assert.IsNull(model.UserError);
        Assert.HasCount(0, ports.ExportedSelection!.Targets);
        Assert.HasCount(0, ports.SavedSettings!.CandidatePreferences);
    });

    [TestMethod]
    public Task CompleteCurrentRepositoryShowsGlobalBlockersAndIssuesToggleOnlyChangesIssues() => OnDispatcherAsync(async () =>
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var local = TestData.Repo("local") with { Name = "company.current" };
        var other = TestData.Repo("other") with { Name = local.Name };
        var emulator = TestData.Repo("emulator", RepositoryCategory.ICEmulator);
        var analysis = TestData.Analyze(TestData.Inventory(platform), TestData.Inventory(local, TestData.Usage(local, "soup", "10.0.12")),
            TestData.Inventory(other) with { Status = InventoryStatus.Unresolved, Issues = [new("other-issue", "Source incomplete")] },
            TestData.Inventory(emulator) with { Status = InventoryStatus.Failed, Snapshot = null, Issues = [new("emulator-issue", "Emulator unavailable")] });
        using var model = CreateModel(analysis);
        await model.SetAnalysisAsync(analysis);
        Assert.AreEqual("local", model.SelectedAnalysisRepository?.Id);
        Assert.HasCount(0, model.IssuesView.Cast<IssueRow>());
        StringAssert.Contains(model.RepositoryAnalysisSummary, "IC baseline incomplete");
        StringAssert.Contains(model.GlobalBaselineBlockReasons, "company.current [other]: Unresolved");
        Assert.IsFalse(model.GlobalBaselineBlockReasons.Contains("emulator:", StringComparison.Ordinal));
        var package = model.Packages.Single();
        model.SelectedPackage = package;
        model.Search = "soup";
        var changeRepository = model.SelectedChangeRepository;
        var reviewStatus = model.ReviewStatus;
        Assert.IsFalse(model.ShowAllScanIssues);
        model.ShowAllScanIssues = true;
        Assert.HasCount(2, model.IssuesView.Cast<IssueRow>());
        Assert.AreEqual("other", model.IssuesView.Cast<IssueRow>().Single(row => row.Code == "other-issue").RepositoryId);
        Assert.AreSame(package, model.SelectedPackage);
        Assert.AreEqual("soup", model.Search);
        Assert.AreSame(changeRepository, model.SelectedChangeRepository);
        Assert.AreEqual(reviewStatus, model.ReviewStatus);
        Assert.AreSame(analysis, model.Analysis);
        Assert.IsFalse(model.IsAnalysisStale);
        Assert.HasCount(1, model.PackagesView.Cast<PackageRow>());
        Select(model, "other");
        Assert.HasCount(2, model.IssuesView.Cast<IssueRow>());
        model.ShowAllScanIssues = false;
        Assert.AreEqual("other-issue", model.IssuesView.Cast<IssueRow>().Single().Code);
        Select(model, null);
        Assert.HasCount(2, model.IssuesView.Cast<IssueRow>());
        Assert.AreEqual(reviewStatus, model.ReviewStatus);
    });

    [TestMethod]
    public Task GlobalBlockReasonsIncludeFailedPlatformAndMissingICButExcludeEmulator() => OnDispatcherAsync(async () =>
    {
        var platform = TestData.Repo("failed-platform", RepositoryCategory.Platform);
        var local = TestData.Repo("complete-local");
        var emulator = TestData.Repo("failed-emulator", RepositoryCategory.ICEmulator);
        var missing = TestData.Repo("missing-ic");
        var analysis = TestData.Analyze(TestData.Inventory(platform) with { Status = InventoryStatus.Failed, Snapshot = null },
            TestData.Inventory(local, TestData.Usage(local, "soup", "1.0")),
            TestData.Inventory(emulator) with { Status = InventoryStatus.Failed, Snapshot = null });
        analysis = analysis with { OriginalRefs = analysis.OriginalRefs.Add(missing), CompleteIC = false };
        using var model = CreateModel(analysis);
        await model.SetAnalysisAsync(analysis);
        StringAssert.Contains(model.RepositoryAnalysisSummary, "Platform baseline incomplete");
        StringAssert.Contains(model.GlobalBaselineBlockReasons, "failed-platform: Failed");
        StringAssert.Contains(model.GlobalBaselineBlockReasons, "missing-ic: Missing inventory");
        Assert.IsFalse(model.GlobalBaselineBlockReasons.Contains("failed-emulator", StringComparison.Ordinal));
        Assert.IsFalse(model.CanReview);
    });

    [TestMethod]
    public Task AvailableNuGetWithIncompleteOtherICDoesNotEnableUpdates() => OnDispatcherAsync(async () =>
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var local = TestData.Repo("local");
        var other = TestData.Repo("other");
        var analysis = TestData.Analyze(TestData.Inventory(platform), TestData.Inventory(local, TestData.Usage(local, "soup", "10.0.12")),
            TestData.Inventory(other) with { Status = InventoryStatus.Failed, Snapshot = null });
        analysis = analysis with { Packages = [analysis.Packages.Single() with
            { NuGetCandidate = new("soup", NuGetVersion.Parse("10.0.13"), CandidateStatus.Available, "fixture", DateTimeOffset.UtcNow) }] };
        using var model = CreateModel(analysis);
        await model.SetAnalysisAsync(analysis);
        var row = model.Packages.Single();
        Assert.HasCount(2, row.Choices);
        row.SelectedTarget = row.Choices.Single(choice => choice.Source == TargetSource.NuGetCandidate);
        Assert.AreEqual("Upgrade", row.Action);
        Assert.IsFalse(model.CanReview);
        Assert.IsFalse(model.CanExecute);
        Assert.IsFalse(analysis.CanUpdate);
    });

    [TestMethod]
    public Task EmulatorFailureAloneDoesNotReportBaselineBlocker() => OnDispatcherAsync(async () =>
    {
        var analysis = Fixture();
        analysis = analysis with { Inventories = [.. analysis.Inventories.Select(inventory => inventory.Source.Category == RepositoryCategory.ICEmulator
            ? inventory with { Status = InventoryStatus.Failed, Snapshot = null, Issues = [new("emulator-only", "Fixture unavailable")] } : inventory)] };
        using var model = CreateModel(analysis);
        await model.SetAnalysisAsync(analysis);
        Assert.AreEqual("", model.GlobalBaselineBlockReasons);
        Assert.AreEqual("", model.GlobalBaselineBlockSummary);
        Assert.IsTrue(analysis.CanUpdate);
        model.ShowAllScanIssues = true;
        Assert.AreEqual("emulator-only", model.IssuesView.Cast<IssueRow>().Single().Code);
    });

    [TestMethod]
    public Task SavedICPreferenceRestoresOnlyDraftSourceAndResolvesFreshMaximum() => OnDispatcherAsync(async () =>
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var local = TestData.Repo("local");
        var other = TestData.Repo("other");
        var analysis = TestData.Analyze(TestData.Inventory(platform), TestData.Inventory(local, TestData.Usage(local, "soup", "10.0.12")),
            TestData.Inventory(other) with { Status = InventoryStatus.Failed, Snapshot = null });
        var ports = new Ports(analysis) { Preferences = [new("soup", TargetSource.ICMax)] };
        using var model = CreateModel(analysis, ports);
        await model.InitializeCommand.ExecuteAsync(null);
        await model.SetAnalysisAsync(analysis);
        var row = model.Packages.Single();
        Assert.HasCount(1, row.Choices);
        Assert.IsTrue(row.SelectedTarget!.IsProvisional);
        Assert.AreEqual("10.0.12", row.Target);
        StringAssert.Contains(row.TargetTooltip, "other: Failed");
        Assert.AreEqual("Aligned", row.Action);
        Assert.IsFalse(model.CanReview);
        Assert.IsFalse(model.CanExecute);
        var fresh = analysis with { Id = Guid.NewGuid(), Packages = [analysis.Packages[0] with { ICMax = NuGetVersion.Parse("10.0.13") }] };
        await model.SetAnalysisAsync(fresh);
        Assert.AreEqual("10.0.13", model.Packages.Single().Target);
        Assert.IsTrue(model.Packages.Single().SelectedTarget!.IsProvisional);
        Assert.IsFalse(model.CanExecute);
    });

    [TestMethod]
    public Task CandidateMetadataIssuesUseAffectedSourceIdsAndDoNotBlockCompleteBaselines() => OnDispatcherAsync(async () =>
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var first = TestData.Repo("first") with { Name = "same-display-name" };
        var second = TestData.Repo("second") with { Name = first.Name };
        var analysis = TestData.Analyze(TestData.Inventory(platform), TestData.Inventory(first, TestData.Usage(first, "soup", "1.0")),
            TestData.Inventory(second, TestData.Usage(second, "soup", "2.0")));
        analysis = analysis with { Packages = [analysis.Packages.Single() with
            { NuGetCandidate = new("soup", null, CandidateStatus.Failed, "fixture", DateTimeOffset.UtcNow, "HTTP Found (302)") }] };
        using var model = CreateModel(analysis);
        await model.SetAnalysisAsync(analysis);
        var issue = model.IssuesView.Cast<IssueRow>().Single();
        Assert.AreEqual(first.Id, issue.RepositoryId);
        Assert.AreEqual(first.Name, issue.Repository);
        Assert.AreEqual("NuGetCandidateFailed", issue.Code);
        StringAssert.Contains(issue.Message, "soup");
        StringAssert.Contains(issue.Message, "HTTP Found (302)");
        Assert.IsFalse(issue.BlocksUpdates);
        Select(model, second.Id);
        Assert.AreEqual(second.Id, model.IssuesView.Cast<IssueRow>().Single().RepositoryId);
        model.ShowAllScanIssues = true;
        Assert.HasCount(2, model.IssuesView.Cast<IssueRow>());
        Assert.IsTrue(analysis.CanUpdate);
        var package = model.Packages.Single();
        Assert.IsTrue(package.IsSoupTargetEditable);
        Assert.HasCount(1, package.Choices);
        Assert.AreEqual(TargetSource.ICMax, package.Choices.Single().Source);
    });

    private static AnalysisResult Fixture()
    {
        var platformOne = TestData.Repo("platform-one", RepositoryCategory.Platform);
        var platformTwo = TestData.Repo("platform-two", RepositoryCategory.Platform);
        var platformThree = TestData.Repo("platform-three", RepositoryCategory.Platform);
        var first = TestData.Repo("first");
        var second = TestData.Repo("second");
        var emulator = TestData.Repo("emulator", RepositoryCategory.ICEmulator);
        var analysis = TestData.Analyze(
            TestData.Inventory(platformOne, TestData.Usage(platformOne, "shared", "1.0")),
            TestData.Inventory(platformTwo, TestData.Usage(platformTwo, "shared", "2.0")),
            TestData.Inventory(platformThree, TestData.Usage(platformThree, "shared", "1.5")),
            TestData.Inventory(first, TestData.Usage(first, "shared", "1.0", TestClassification.Test), TestData.Usage(first, "soup", "1.0", TestClassification.Test)),
            TestData.Inventory(second, TestData.Usage(second, "shared", "3.0"), TestData.Usage(second, "soup", "3.0"),
                TestData.Usage(second, "only-second", "1.0"), TestData.Usage(second, "elekta.internal", "1.0")),
            TestData.Inventory(emulator, TestData.Usage(emulator, "soup", "99.0"), TestData.Usage(emulator, "emulator-only", "99.0")),
            TestData.Inventory(TestData.Repo("empty")));
        return analysis with { Packages = [.. analysis.Packages.Select(package => package.PackageId == "soup" ? package with
            { NuGetCandidate = new("soup", NuGetVersion.Parse("4.0"), CandidateStatus.Available, "fixture", DateTimeOffset.UtcNow) } : package)] };
    }

    private static MainViewModel CreateModel(AnalysisResult analysis, Ports? ports = null)
    {
        ports ??= new Ports(analysis);
        return new(ports, ports, ports, new ScanWorkflow(ports, ports, ports),
            new ExportReportWorkflow(ports), new UpdateWorkflow(ports), ports);
    }

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
        public AnalysisResult? ExportedAnalysis { get; private set; }
        public ReportSelection? ExportedSelection { get; private set; }
        public UserSettings? SavedSettings { get; private set; }
        public System.Collections.Immutable.ImmutableArray<CandidatePreference> Preferences { get; init; } = [];
        public Task<LoadedConfiguration> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new LoadedConfiguration(
            new(1, analysis.ManifestVersion, [.. analysis.OriginalRefs.Where(source => source.Category == RepositoryCategory.Platform)]),
            analysis.ManifestHash, AnalysisRules.Default, new(1, "", "", "", ["scope"])));
        Task<UserSettings> ISettingsStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(UserSettings.Default with
            { Repositories = [.. analysis.OriginalRefs.Where(source => source.Category != RepositoryCategory.Platform)], CandidatePreferences = Preferences });
        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken) { SavedSettings = settings; return Task.CompletedTask; }
        public Task<AccessToken> GetTokenAsync(bool allowInteractive, CancellationToken cancellationToken) => throw new AssertFailedException("No login expected.");
        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<SourceSnapshot> GetSnapshotAsync(RepositorySource source, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) => throw new AssertFailedException("No scan expected.");
        public PackageScanResult Scan(SourceSnapshot snapshot, AnalysisRules rules, CancellationToken cancellationToken) => throw new AssertFailedException("No scan expected.");
        public Task<NuGetCandidate> GetCandidateAsync(string packageId, CancellationToken cancellationToken) => throw new AssertFailedException("No query expected.");
        public Task<UpdateResult> ExecuteAsync(ChangePlan approvedPlan, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) => throw new AssertFailedException("No remote writes expected.");
        public Task WriteAsync(AnalysisResult value, string path, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task WriteAsync(AnalysisResult value, string path, IProgress<OperationProgress>? progress, CancellationToken cancellationToken, ReportSelection? selection)
        {
            ExportedAnalysis = value;
            ExportedSelection = selection;
            return Task.CompletedTask;
        }
        public string? ChooseReportPath(string? directory) => "fixture.xlsx";
        public bool Approve(ChangePlan plan) => true;
        public void ShowSettings(MainViewModel model) => throw new AssertFailedException("No dialog expected.");
    }
}