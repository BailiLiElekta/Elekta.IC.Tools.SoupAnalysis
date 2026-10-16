using System.Collections.Immutable;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Desktop.Presentation;
using Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Elekta.IC.Tools.SoupAnalysis.Desktop.Verification;

internal static class DesktopViewModelChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync(MainViewModel startup, IConfigurationService configurationService)
    {
        try { return await RunChecksAsync(startup, configurationService); }
        catch (Exception exception) { throw new InvalidOperationException($"Offline desktop verification: {exception}", exception); }
    }

    private static async Task<IReadOnlyList<string>> RunChecksAsync(MainViewModel startup, IConfigurationService configurationService)
    {
        var passed = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Desktop behavioral check failed: " + name);
            passed.Add(name);
        }
        var productionServices = new ServiceCollection();
        productionServices.AddLogging();
        CompositionRoot.ConfigureServices(productionServices);
        using (var production = productionServices.BuildServiceProvider(new ServiceProviderOptions
        { ValidateOnBuild = true, ValidateScopes = true }))
        {
            Check(production.GetRequiredService<MainViewModel>() is not null,
                "Production service graph resolves without login or remote calls");
        }
        Check(startup.IsInitialized && startup.Repositories.Count == 11, "Real 11-member Platform manifest excludes the IC bridge");
        Check(startup.Repositories.All(row => !row.CanDelete && row.AllowedRefKinds.Length == 3), "Platform membership locked, branch/tag/commit editable");
        Check(!startup.ScanCommand.CanExecute(null) && !startup.ExecuteCommand.CanExecute(null) && !startup.LoginCommand.CanExecute(null), "Smoke mode prohibits remote operations");
        var parsed = MainViewModel.ParseRepository("https://dev.azure.com/org/project/_git/elekta.ic.emulator.fixture", "main", RepositoryCategory.IC);
        Check(parsed.Category == RepositoryCategory.ICEmulator && parsed.Name == "elekta.ic.emulator.fixture", "Domain classification overrides supplied category for known names");
        Check(Rejects(() => MainViewModel.ParseRepository("https://dev.azure.com/org/project/_git/elekta.platform.fixture", "main", null)), "Platform additions rejected");
        Check(Rejects(() => MainViewModel.ParseRepository("https://dev.azure.com/org/project/_git/unknown", "main", null)), "Unknown repository requires explicit category");
        Check(Rejects(() => MainViewModel.ParseRepository("https://user:secret@dev.azure.com/org/project/_git/unknown", "main", RepositoryCategory.IC)), "Credential-bearing URLs rejected");
        Check(Rejects(() => MainViewModel.ParseRepository("https://dev.azure.com/org/project/_git/unknown", "topic[1]", RepositoryCategory.IC)), "Invalid Git branch syntax rejected before acquisition");
        Check(Rejects(() => MainViewModel.ParseRepository("https://dev.azure.com/org/project/_git/unknown%2frepo", "main", RepositoryCategory.IC)), "Ambiguous encoded repository path rejected");
        var configuration = await configurationService.LoadAsync(CancellationToken.None);
        var fixture = SmokeFixture.Create(configuration);
        var settings = UserSettings.Default with { Repositories = [.. fixture.OriginalRefs.Where(source => source.Category != RepositoryCategory.Platform)] };
        var ports = new TestPorts(configuration, settings);
        var gate = new OperationGate();
        using var model = new MainViewModel(ports, ports, ports, new ScanWorkflow(ports, ports, ports, gate),
            new ExportReportWorkflow(ports), new UpdateWorkflow(ports, gate), ports);
        await model.InitializeCommand.ExecuteAsync(null);
        Check(model.LoginCommand.CanExecute(null) && model.ScanCommand.CanExecute(null),
            "Azure CLI login and scan do not require application registration");
        await model.SetAnalysisAsync(fixture);
        var shared = model.Packages.Single(row => row.PackageId == "Newtonsoft.Json");
        Check(model.SelectedAnalysisRepository?.Id == "smoke-ic" && model.AnalysisRepositoryChoices.Count == fixture.OriginalRefs.Length + 1,
            "Analysis defaults to first scanned IC and includes every immutable scanned source");
        Check(model.PackagesView.Cast<PackageRow>().Count() == 4 && shared.CurrentVersions == "13.0.1" && shared.Action == "Upgrade",
            "First IC shows all four local packages and local shared upgrade");
        Check(!shared.IsTargetEditable && shared.SelectedTarget?.Source == TargetSource.PlatformMax && shared.Choices.Count == 1, "Shared target fixed to PlatformMax");
        var failed = model.Packages.Single(row => row.PackageId == "Dapper");
        Check(failed.TestOnlyStatus == "Yes", "First IC TEST Only uses only complete local usages");
        Check(failed.Choices.Count == 1 && failed.Choices[0].Source == TargetSource.ICMax, "NuGet outage disables only NuGet choice");
        var internalReview = model.ReviewRows.Single(row => row.Repository.Id == "smoke-ic" && row.PackageId == "elekta.design.fixture");
        Check(!internalReview.Eligible && internalReview.Action == "NotApplicable"
            && internalReview.NewVersion == "Not applicable" && !internalReview.Reason.Contains("target required", StringComparison.Ordinal),
            "Internal packages show no SOUP alignment target or action");
        Check(model.References.Any(row => row.Category == "IC Emulator" && row.Version == "99.0.0"), "Emulator references included separately");
        Check(model.Packages.Single(row => row.PackageId == "Polly").ICMax == "8.0.0", "Global ICMax includes second IC but excludes Emulator");
        model.SelectedAnalysisRepository = model.AnalysisRepositoryChoices.Single(choice => choice.Id == "smoke-ic-second");
        Check(shared.CurrentVersions == "13.0.5" && shared.Action == "Downgrade" && failed.TestOnlyStatus == "No",
            "Second IC changes local action and TEST Only without changing target");
        Check(model.PackagesView.Cast<PackageRow>().Any(row => row.PackageId == "Serilog") &&
            model.ReferencesView.Cast<ReferenceRow>().All(row => row.RepositoryId == "smoke-ic-second"),
            "Second-only package appears and references never leak other repositories");
        model.SelectedAnalysisRepository = model.AnalysisRepositoryChoices.Single(choice => choice.Id == "smoke-emulator");
        Check(model.PackagesView.Cast<PackageRow>().Count() == 3 && model.PackagesView.Cast<PackageRow>().All(row => !row.IsTargetEditable && !row.IsCustomTargetEditable && row.Action == "NotApplicable"),
            "Emulator shows all local packages including Emulator-only IDs as read-only");
        model.SelectedAnalysisRepository = model.AnalysisRepositoryChoices.Single(choice => choice.Id == "smoke-ic");
        Check(!model.IsAnalysisStale && model.Analysis?.CanUpdate == true, "Complete analysis installed on UI dispatcher");
        Check(model.SelectedChangeRepository?.Source.Id == "smoke-ic" && model.RepositoryChoices.Count == 2 &&
            model.VisibleReviewRows.Cast<ReviewRow>().Count() == 4 && model.VisibleReviewRows.Cast<ReviewRow>().All(row => row.Repository.Id == "smoke-ic"),
            "Changes defaults to one immutable normal IC repository with only its package rows");
        model.SelectAllPackagesCommand.Execute(null);
        Check(model.VisibleReviewRows.Cast<ReviewRow>().Count(row => row.IsSelected) == 1 && ports.Approvals == 0 && ports.Executions == 0 &&
            model.ReviewStatus.Contains("3 target missing", StringComparison.Ordinal),
            "Select All selects only resolved targets and reports missing targets without approval or execution");
        model.SelectUpgradesCommand.Execute(null);
        Check(model.VisibleReviewRows.Cast<ReviewRow>().Single(row => row.IsSelected).PackageId == "Newtonsoft.Json",
            "Select Upgrades replaces the current repository selection only");
        model.ClearPackageSelectionCommand.Execute(null);
        Check(model.VisibleReviewRows.Cast<ReviewRow>().All(row => !row.IsSelected), "Clear Selection clears the current repository only");
        var review = model.ReviewRows.Single(row => row.Repository.Id == "smoke-ic" && row.PackageId == "Newtonsoft.Json");
        review.IsSelected = true;
        await model.ReviewCommand.ExecuteAsync(null);
        Check(model.CanExecute && ports.Approvals == 1 && ports.Executions == 0, "Explicit approval does not execute remote workflow");
        model.SelectedAnalysisRepository = model.AnalysisRepositoryChoices.Single(choice => choice.Id == "smoke-ic-second");
        Check(model.CanExecute && review.IsSelected && model.SelectedChangeRepository?.Source.Id == "smoke-ic",
            "Browsing Analysis preserves immutable approval and the independent Changes scope");
        model.RepositoryChoices[1].IsSelected = true;
        Check(model.CanExecute && model.SelectedChangeRepository?.Source.Id == "smoke-ic",
            "Obsolete repository checkbox flags cannot mutate scope or invalidate approval");
        model.SelectedChangeRepository = model.RepositoryChoices.Single(choice => choice.Source.Id == "smoke-ic-second");
        Check(!model.CanExecute && review.IsSelected && model.VisibleReviewRows.Cast<ReviewRow>().Count() == 5,
            "Changes browsing preserves stored package checkboxes but invalidates hidden approval");
        model.SelectUpgradesCommand.Execute(null);
        Check(model.VisibleReviewRows.Cast<ReviewRow>().All(row => !row.IsSelected) && review.IsSelected && ports.Executions == 0,
            "Second repository downgrade and awaiting targets are not selected by Select Upgrades");
        model.SelectedChangeRepository = model.RepositoryChoices.Single(choice => choice.Source.Id == "smoke-ic");
        await model.ReviewCommand.ExecuteAsync(null);
        Check(model.CanExecute, "Returning to a saved selection requires a fresh scoped review");
        model.SelectedAnalysisRepository = model.AnalysisRepositoryChoices.Single(choice => choice.Id == "smoke-ic");
        review.IsSelected = false;
        Check(!model.CanExecute, "Selection change invalidates approval");
        review.IsSelected = true;
        await model.ReviewCommand.ExecuteAsync(null);
        var soup = model.Packages.Single(row => row.PackageId == "Polly");
        soup.SelectedTarget = soup.Choices.First();
        Check(!model.CanExecute, "Global package target change invalidates approval");
        var internalPackage = internalReview.Package;
        Check(shared.ICMax == "N/A" && internalPackage.ICMax == "N/A" && internalPackage.NuGetMax == "N/A",
            "ICMax and NuGetMax comparisons are exclusive to IC SOUP");
        internalPackage.CustomVersionText = "2.0.0-preview.1";
        Check(internalPackage.Action == "Upgrade" && internalReview.Eligible && internalReview.Action == "Upgrade",
            "Valid internal prerelease custom target enables review without fixed choices");
        model.SelectedAnalysisRepository = model.AnalysisRepositoryChoices.Single(choice => choice.Id == "smoke-ic-second");
        Check(internalPackage.Action == "Downgrade" && internalPackage.TestOnlyStatus == "Yes" && internalPackage.Target == "2.0.0-preview.1",
            "Global custom target survives browsing with local downgrade and TEST Only classification");
        model.SelectedAnalysisRepository = model.AnalysisRepositoryChoices.Single(choice => choice.Id == "smoke-ic");
        internalReview.IsSelected = true;
        model.ReviewRows.Single(row => row.Repository.Id == "smoke-ic" && row.PackageId == "Polly").IsSelected = true;
        await model.ReviewCommand.ExecuteAsync(null);
        Check(model.CanExecute && model.UserError is null, "Custom target review creates an executable approved plan without execution");
        await model.ExportCommand.ExecuteAsync(null);
        Check(model.UserError is null, "Custom in-memory export succeeds: " + model.UserError);
        Check(ports.ExportedSelection?.Targets.Single(target => target.PackageId == internalPackage.PackageId).CustomVersion == "2.0.0-preview.1" &&
            ports.ExportedSelection.ReviewPlan?.Changes.Any(change => change.PackageId == internalPackage.PackageId && change.TargetSource == TargetSource.Custom) == true,
            "In-memory export carries identical custom target and reviewed plan");
        Check(ports.SavedSettings?.CandidatePreferences.Single(preference => preference.PackageId == internalPackage.PackageId).CustomVersion == "2.0.0-preview.1",
            "Settings persist explicit normalized custom version without approval");
        internalPackage.CustomVersionText = "2.*";
        Check(internalPackage.SelectedTarget is null && !internalReview.Eligible && !model.CanExecute && internalPackage.TargetError is not null,
            "Invalid custom edit clears prior target and approval immediately");
        await model.ExportCommand.ExecuteAsync(null);
        Check(model.UserError is null && ports.ExportedSelection?.Targets.All(target => target.PackageId != internalPackage.PackageId) == true &&
            ports.ExportedSelection.ReviewPlan is null, "Invalid custom input is excluded from report targets and reviewed plan");
        var partialInventories = fixture.Inventories.Select(inventory => inventory.Source.Id == "smoke-ic-second"
            ? inventory with { Status = InventoryStatus.Failed, Snapshot = null, Usages = [] } : inventory).ToImmutableArray();
        var previewAnalysis = AlignmentAnalyzer.Analyze(Guid.NewGuid(), configuration.Manifest, configuration.ManifestHash,
            fixture.OriginalRefs, partialInventories, DateTimeOffset.UtcNow);
        await model.SetAnalysisAsync(previewAnalysis);
        var provisional = model.Packages.Single(row => row.PackageId == "Polly");
        provisional.SelectedTarget = provisional.Choices.Single(choice => choice.Source == TargetSource.ICMax);
        model.Packages.Single(row => row.PackageId == "elekta.design.fixture").CustomVersionText = "25.1.3";
        model.SelectAllPackagesCommand.Execute(null);
        var previousApprovals = ports.Approvals;
        Check(model.CanReviewDraft && model.ReviewButtonLabel == "Review Preview" && model.CanSelectChangePackages,
            "Complete current IC permits selection and draft review despite another failed IC");
        await model.ReviewCommand.ExecuteAsync(null);
        Check(model.UserError is null && ports.Previews == 1 && ports.Approvals == previousApprovals && ports.Executions == 0 && !model.CanExecute,
            "Partial review calls only read-only preview interaction with zero new approvals or executions");
        await model.ExportCommand.ExecuteAsync(null);
        ports.ExportedSelection!.Validate(previewAnalysis);
        Check(ports.ExportedSelection.ReviewPlan?.Status == ChangePlanStatus.PreviewOnly &&
            ports.SavedSettings?.CandidatePreferences.Single(preference => preference.PackageId == "Polly").CustomVersion is null,
            "Preview export preserves non-approval provenance and saves only the ICMax source preference");
        await model.ExecuteCommand.ExecuteAsync(null);
        Check(ports.Executions == 0, "Direct desktop Execute invocation rejects read-only preview before executor access");
        await model.SetAnalysisAsync(fixture);
        model.Repositories[0].Ref += "-changed";
        Check(model.IsAnalysisStale && !model.CanReview && !model.CanExecute, "Source edit marks analysis stale and blocks approval");
        model.Repositories[0].Ref = fixture.OriginalRefs[0].Ref;
        await model.SetAnalysisAsync(fixture);
        ports.AllowScanAuthentication();
        await model.LoginCommand.ExecuteAsync(null);
        var running = model.ScanCommand.ExecuteAsync(null);
        Check(model.IsBusy && !model.CanEdit && model.CancelCommand.CanExecute(null), "Busy state precedes asynchronous work and cancellation stays usable");
        model.SelectedChangeRepository = model.RepositoryChoices.Single(choice => choice.Source.Id == "smoke-ic-second");
        Check(model.SelectedChangeRepository?.Source.Id == "smoke-ic" && !model.SelectAllPackagesCommand.CanExecute(null),
            "Busy operations lock the Changes selector and bulk commands");
        model.CancelActiveOperation();
        await running;
        Check(!model.IsBusy && model.Stage == "Cancelled" && ReferenceEquals(model.Analysis, fixture), "Cancelled scan retains previous analysis without success state");
        var partial = fixture with { CompletePlatform = false };
        await model.SetAnalysisAsync(partial);
        Check(!model.CanReview && model.ExportCommand.CanExecute(null), "Incomplete analysis blocks updates but permits Excel export");
        var log = new BoundedLog();
        log.AppendBatch(Enumerable.Range(0, 700).Select(index => new LogEntry(DateTimeOffset.UtcNow, "Test", index.ToString())));
        Check(log.Count == 500, "Log batches bounded to 500 entries");
        return passed;
    }
    private static bool Rejects(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
    }

    private sealed class TestPorts(LoadedConfiguration configuration, UserSettings settings) :
        IConfigurationService, ISettingsStore, IAccessTokenProvider, IRepositorySnapshotProvider, IPackageScanner,
        INuGetCandidateProvider, IExcelReportWriter, IUpdateExecutor, IUserInteraction
    {
        private LoadedConfiguration currentConfiguration = configuration;
        public int Approvals { get; private set; }
        public int Executions { get; private set; }
        public int Previews { get; private set; }
        public ReportSelection? ExportedSelection { get; private set; }
        public UserSettings? SavedSettings { get; private set; }
        public void AllowScanAuthentication() => currentConfiguration = currentConfiguration with
        { Authentication = currentConfiguration.Authentication with { TenantId = "design", ClientId = "00000000-0000-0000-0000-000000000001", RedirectUri = "http://localhost" } };
        Task<LoadedConfiguration> IConfigurationService.LoadAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(currentConfiguration); }
        Task<UserSettings> ISettingsStore.LoadAsync(CancellationToken token) => Task.FromResult(settings);
        public Task SaveAsync(UserSettings value, CancellationToken token) { token.ThrowIfCancellationRequested(); SavedSettings = value; return Task.CompletedTask; }
        public Task<AccessToken> GetTokenAsync(bool allowInteractive, CancellationToken token) => Task.FromResult(new AccessToken("DESIGN TEST / NOT A REAL TOKEN", DateTimeOffset.UtcNow));
        public Task SignOutAsync(CancellationToken token) => Task.CompletedTask;
        public async Task<SourceSnapshot> GetSnapshotAsync(RepositorySource source, IProgress<OperationProgress>? progress, CancellationToken token)
        {
            var delayed = new TaskCompletionSource<SourceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            return await delayed.Task.WaitAsync(token);
        }
        public PackageScanResult Scan(SourceSnapshot snapshot, AnalysisRules rules, CancellationToken token) => new([], []);
        public Task<NuGetCandidate> GetCandidateAsync(string packageId, CancellationToken token) => Task.FromResult(new NuGetCandidate(packageId, null, CandidateStatus.NotRequested, "Design", DateTimeOffset.UtcNow));
        public Task WriteAsync(AnalysisResult analysis, string path, IProgress<OperationProgress>? progress, CancellationToken token) => Task.CompletedTask;
        public Task WriteAsync(AnalysisResult analysis, string path, IProgress<OperationProgress>? progress, CancellationToken token, ReportSelection? selection)
        { ExportedSelection = selection; return Task.CompletedTask; }
        public Task<UpdateResult> ExecuteAsync(ChangePlan approved, IProgress<OperationProgress>? progress, CancellationToken token)
        { Executions++; return Task.FromResult(new UpdateResult(approved.Id, UpdateStatus.Completed, [])); }
        public string? ChooseReportPath(string? directory) => "offline-fixture.xlsx";
        public bool Approve(ChangePlan plan) { Approvals++; return true; }
        public void ShowPreview(ChangePlan plan, string blockers)
        {
            if (plan.Status != ChangePlanStatus.PreviewOnly || plan.CanExecute || !blockers.Contains("IC baseline incomplete", StringComparison.Ordinal))
                throw new InvalidOperationException("Offline preview must not expose approval or execution.");
            Previews++;
        }
        public void ShowSettings(MainViewModel model) { }
    }
}