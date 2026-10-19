using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Desktop.Presentation;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.AzureDevOps;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IConfigurationService configurationService;
    private readonly ISettingsStore settingsStore;
    private readonly IAccessTokenProvider accessTokens;
    private readonly ScanWorkflow scan;
    private readonly ExportReportWorkflow export;
    private readonly UpdateWorkflow update;
    private readonly IUserInteraction interaction;
    private readonly ILogger<MainViewModel> logger;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer timer;
    private readonly DispatcherTimer filterTimer;
    private readonly Stopwatch stopwatch = new();
    private readonly Queue<LogEntry> pendingLogs = new();
    private LoadedConfiguration? configuration;
    private UserSettings settings = UserSettings.Default;
    private CancellationTokenSource? operationCancellation;
    private TaskCompletionSource? operationCompletion;
    private Guid operationId;
    private bool installingRows;
    private ChangePlan? plan;
    private ChangePlan? retryPlan;
    private long selectionRevision;

    public MainViewModel(IConfigurationService configurationService, ISettingsStore settingsStore,
        IAccessTokenProvider accessTokens, ScanWorkflow scan, ExportReportWorkflow export, UpdateWorkflow update,
        IUserInteraction? interaction = null,
        ILogger<MainViewModel>? logger = null, bool smokeMode = false)
    {
        this.configurationService = configurationService;
        this.settingsStore = settingsStore;
        this.accessTokens = accessTokens;
        this.scan = scan;
        this.export = export;
        this.update = update;
        this.interaction = interaction ?? new WpfUserInteraction();
        this.logger = logger ?? NullLogger<MainViewModel>.Instance;
        SmokeMode = smokeMode;
        dispatcher = Dispatcher.CurrentDispatcher;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, OnTick, dispatcher);
        filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        filterTimer.Tick += (_, _) => RefreshAnalysisViews();
        InitializeCommand = new AsyncRelayCommand(InitializeAsync, () => !IsBusy && !IsInitialized);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => CanScan);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !IsBusy && Analysis is not null && !SmokeMode);
        LoginCommand = new AsyncRelayCommand(LoginAsync, () => CanEdit && !SmokeMode);
        SignOutCommand = new AsyncRelayCommand(SignOutAsync, () => CanEdit && !SmokeMode);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => CanEdit && !SmokeMode);
        ReviewCommand = new AsyncRelayCommand(ReviewAsync, () => CanReview);
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, () => CanExecute);
        AddRepositoryCommand = new RelayCommand(AddRepository, () => CanEdit && !SmokeMode);
        RemoveRepositoryCommand = new RelayCommand<RepositoryRow>(RemoveRepository, row => CanEdit && !SmokeMode && row?.CanDelete == true);
        CancelCommand = new RelayCommand(() => { operationCancellation?.Cancel(); Status = "Cancellation requested; awaiting actual outcome"; }, () => IsBusy);
        SettingsCommand = new RelayCommand(() => ViewAction(() => this.interaction.ShowSettings(this)), () => CanEdit);
        OpenPullRequestCommand = new RelayCommand<RepositoryUpdateResult>(OpenPullRequest);
        ClearDetailsCommand = new RelayCommand(() => SelectedPackage = null);
        PackagesView = CollectionViewSource.GetDefaultView(Packages);
        ReferencesView = CollectionViewSource.GetDefaultView(References);
        InitializeChangeSelection();
        ConfigureFilters();
    }

    [ObservableProperty] private bool isInitialized;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isAnalysisStale;
    [ObservableProperty] private bool resultsAreStale;
    [ObservableProperty] private bool remoteOutcomeNeedsReview;
    [ObservableProperty] private int selectedTab;
    [ObservableProperty] private string status = "Starting";
    [ObservableProperty] private string stage = "Idle";
    [ObservableProperty] private string elapsed = "00:00";
    [ObservableProperty] private bool isIndeterminate = true;
    [ObservableProperty] private double progressValue;
    [ObservableProperty] private double progressMaximum = 1;
    [ObservableProperty] private string progressCounts = "";
    [ObservableProperty] private string? userError;
    [ObservableProperty] private string accountStatus = "Not signed in";
    [ObservableProperty] private string authenticationStatus = "Azure CLI";
    [ObservableProperty] private string newRepositoryUrl = "";
    [ObservableProperty] private string newRepositoryBranch = "main";
    [ObservableProperty] private RepositoryCategory? newRepositoryCategory;
    [ObservableProperty] private string newRepositoryValidation = "";
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string ownershipFilter = "All ownership";
    [ObservableProperty] private string actionFilter = "All actions";
    [ObservableProperty] private string categoryFilter = "All categories";
    [ObservableProperty] private string referenceCategoryFilter = "All references";
    [ObservableProperty] private PackageRow? selectedPackage;
    [ObservableProperty] private AnalysisResult? analysis;
    [ObservableProperty] private AnalysisResult? previousAnalysis;
    [ObservableProperty] private string reviewStatus = "Choose an IC repository and package targets";
    [ObservableProperty] private string resultStatus = "No update executed";
    [ObservableProperty] private IReadOnlyList<RepositoryRow> repositories = Array.Empty<RepositoryRow>();
    [ObservableProperty] private IReadOnlyList<PackageRow> packages = Array.Empty<PackageRow>();
    [ObservableProperty] private IReadOnlyList<ReferenceRow> references = Array.Empty<ReferenceRow>();
    [ObservableProperty] private IReadOnlyList<IssueRow> issues = Array.Empty<IssueRow>();
    [ObservableProperty] private IReadOnlyList<ReviewRow> reviewRows = Array.Empty<ReviewRow>();
    [ObservableProperty] private IReadOnlyList<RepositoryChoice> repositoryChoices = Array.Empty<RepositoryChoice>();
    [ObservableProperty] private IReadOnlyList<RepositoryUpdateResult> results = Array.Empty<RepositoryUpdateResult>();
    public BoundedLog Logs { get; } = new();
    public bool SmokeMode { get; }
    public string PlatformConfigVersion => configuration?.Manifest.Version ?? "Not loaded";
    public string AnalysisFor => string.IsNullOrWhiteSpace(configuration?.Manifest.AnalysisFor)
        ? "Not specified" : configuration.Manifest.AnalysisFor;
    public bool CanEdit => IsInitialized && !IsBusy;
    public bool CanScan => CanEdit && !SmokeMode && Repositories.All(row => row.Validation == "Valid");
    public bool CanReview => CanSelectChangePackages && !SmokeMode && CurrentChangeRows.Any(row => row.IsSelected) &&
        CurrentChangeRows.Where(row => row.IsSelected).All(row => row.Eligible);
    public bool CanReviewDraft => CanReview && Analysis?.CanUpdate != true;
    public string ReviewButtonLabel => Analysis?.CanUpdate == true ? "Review and Approve" : "Review Preview";
    public bool CanExecute => CanEdit && !SmokeMode && !IsAnalysisStale && plan?.CanExecute == true &&
        Analysis is { } current && plan.Matches(current) && PlanMatchesChangeRepository(plan);
    public bool HasDetails => SelectedPackage is not null;
    public string AnalysisSummary => Analysis is null ? "No analysis yet" :
        $"{(IsAnalysisStale ? "STALE / PREVIOUS SNAPSHOT | " : "")}{Analysis.ScannedAt:g} | {Analysis.Id} | " +
        $"Platform: {(Analysis.CompletePlatform ? "complete" : "incomplete")} | IC: {(Analysis.CompleteIC ? "complete" : "incomplete")} | " +
        $"{Analysis.Packages.Length} packages | Updates: {(Analysis.CanUpdate && !IsAnalysisStale ? "eligible" : "blocked")}";
    public string ResultSummary => $"{(ResultsAreStale ? "STALE PLAN | " : "")}{ResultStatus}";
    public ICollectionView PackagesView { get; private set; }
    public ICollectionView ReferencesView { get; private set; }
    public string[] OwnershipFilters { get; } = ["All ownership", "IC SOUP", "SharedWithPlatform", "Internal", "Unconfirmed"];
    public string[] ActionFilters { get; } = ["All actions", "Need changes", "Aligned", "Upgrade", "Downgrade", "Mixed", "AwaitingSelection", "Unresolved", "NotApplicable"];
    public string[] CategoryFilters { get; } = ["All categories", "Platform", "IC", "ICEmulator"];
    public string[] ReferenceCategoryFilters { get; } = ["All references", "Platform", "IC", "IC Emulator"];
    public RepositoryCategory[] AddCategories { get; } = [RepositoryCategory.IC, RepositoryCategory.ICEmulator];
    public AsyncRelayCommand InitializeCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand LoginCommand { get; }
    public AsyncRelayCommand SignOutCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand ReviewCommand { get; }
    public AsyncRelayCommand ExecuteCommand { get; }
    public RelayCommand AddRepositoryCommand { get; }
    public RelayCommand<RepositoryRow> RemoveRepositoryCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand<RepositoryUpdateResult> OpenPullRequestCommand { get; }
    public RelayCommand ClearDetailsCommand { get; }

    private Task InitializeAsync() => RunAsync("Loading configuration", async (progress, token) =>
    {
        var loaded = await configurationService.LoadAsync(token);
        ApplyConfiguration(loaded, await settingsStore.LoadAsync(token));
        AuthenticationStatus = "Azure CLI | Current Windows user profile";
        IsInitialized = true;
        Stage = "Idle";
        Status = SmokeMode ? "SMOKE TEST | Real manifest | Network and writes disabled" : "Ready";
    });

    private ScanRequest ApplyConfiguration(LoadedConfiguration loaded, UserSettings loadedSettings)
    {
        var request = ConfigurationPolicy.CreateScanRequest(loaded, loadedSettings);
        var scopedSettings = ConfigurationPolicy.BindPlatformOverrides(loadedSettings, loaded.Manifest);
        if (loadedSettings.PlatformConfiguration != scopedSettings.PlatformConfiguration && loadedSettings.PlatformRefOverrides.Length > 0)
            EnqueueLog("Platform configuration version/release changed or legacy settings detected; using manifest refs instead of previous overrides.");
        configuration = loaded;
        settings = scopedSettings;
        InstallRepositories(request.Sources);
        OnPropertyChanged(nameof(PlatformConfigVersion));
        OnPropertyChanged(nameof(AnalysisFor));
        return request;
    }

    private void InstallRepositories(ImmutableArray<RepositorySource> sources)
    {
        installingRows = true;
        try
        {
            foreach (var row in Repositories) row.PropertyChanged -= RepositoryChanged;
            Repositories = sources.Select(source => new RepositoryRow(source)).ToArray();
            foreach (var row in Repositories) row.PropertyChanged += RepositoryChanged;
        }
        finally { installingRows = false; }
    }

    private void RepositoryChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!installingRows && args.PropertyName is nameof(RepositoryRow.Ref) or nameof(RepositoryRow.RefKind))
        {
            InvalidateAnalysis("Repository refs changed; rescan required");
            RefreshCommands();
        }
    }

    private UserSettings CurrentSettings()
    {
        var current = settings with
        {
            Repositories = [.. Repositories.Where(row => row.CanDelete).Select(row => row.ToSource())],
            PlatformRefOverrides = [.. Repositories.Where(row => !row.CanDelete).Select(row => new PlatformRefOverride(row.Source.Id, row.RefKind, row.Ref.Trim()))],
            CandidatePreferences = [.. settings.CandidatePreferences.Where(preference => !Packages.Any(row => row.PackageId.Equals(preference.PackageId, StringComparison.OrdinalIgnoreCase))),
                .. Packages.Where(row => (row.Decision.IsSoup || row.Decision.Ownership == PackageOwnership.Internal) && row.SelectedTarget is not null)
                    .Select(row => new CandidatePreference(row.PackageId, row.SelectedTarget!.Source,
                        row.SelectedTarget.Source == TargetSource.Custom ? row.SelectedTarget.Version.ToNormalizedString() : null))]
        };
        if (configuration is null) throw new InvalidOperationException("Configuration has not loaded.");
        ConfigurationPolicy.CreateScanRequest(configuration, current);
        return current;
    }

    private Task SaveSettingsAsync() => RunAsync("Saving settings", async (_, token) =>
    {
        settings = CurrentSettings();
        await settingsStore.SaveAsync(settings, token);
        Status = "Settings saved";
    });

    private Task LoginAsync() => RunAsync("Signing in through Azure CLI", async (_, token) =>
    {
        await accessTokens.GetTokenAsync(true, token);
        AccountStatus = "Connected via Azure CLI";
        Status = "Azure CLI session connected";
    });
    private Task SignOutAsync() => RunAsync("Disconnecting tool session", async (_, token) =>
    {
        await accessTokens.SignOutAsync(token);
        AccountStatus = "Not signed in";
        InvalidateApproval("Account signed out");
        Status = "Tool disconnected; shared Azure CLI session unchanged";
    });

    private Task ScanAsync() => RunAsync("Scanning repositories", async (progress, token) =>
    {
        PreviousAnalysis = Analysis;
        InvalidateAnalysis("New scan running; previous snapshot retained");
        var nextSettings = CurrentSettings();
        var loaded = await configurationService.LoadAsync(token);
        var request = ApplyConfiguration(loaded, nextSettings);
        settings = CurrentSettings();
        await settingsStore.SaveAsync(settings, token);
        foreach (var row in Repositories) row.ScanStatus = "Queued";
        var result = await scan.ScanAsync(request, progress, token);
        await SetAnalysisAsync(result, token);
        Status = result.CanUpdate ? "Analysis ready" : "Partial analysis; export available, updates blocked";
        Stage = result.CanUpdate ? "Ready" : "Incomplete";
        SelectedTab = 1;
    });

    public async Task SetAnalysisAsync(AnalysisResult result, CancellationToken token = default)
    {
        dispatcher.VerifyAccess();
        var projected = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var completeness = result.Inventories.ToDictionary(inventory => inventory.Source.Id, inventory => inventory.IsComplete, StringComparer.OrdinalIgnoreCase);
            var decisions = result.Packages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
            var emulatorOnly = result.Inventories.Where(inventory => inventory.Source.Category == RepositoryCategory.ICEmulator)
                .SelectMany(inventory => inventory.Usages).Where(usage => !decisions.ContainsKey(usage.PackageId))
                .GroupBy(usage => usage.PackageId, StringComparer.OrdinalIgnoreCase)
                .Select(group => new PackageDecision(group.Key, PackageOwnership.Unconfirmed, null, null, null,
                    result.CompletePlatform, result.CompleteIC, [.. group.GroupBy(usage => usage.Repository.Id, StringComparer.OrdinalIgnoreCase)
                        .Select(repository => new RepositoryPackageDecision(repository.Key, group.Key,
                            repository.All(usage => usage.TestClassification == TestClassification.Test), [.. repository], AlignmentAction.NotApplicable))]));
            var baselineBlockReasons = GetBaselineBlockReasons(result);
            var packageRows = result.Packages.Concat(emulatorOnly).Select(decision => new PackageRow(decision, completeness, baselineBlockReasons)).ToArray();
            var referenceRows = result.Inventories.SelectMany(inventory => inventory.Usages.GroupBy(usage => usage.PackageId, StringComparer.OrdinalIgnoreCase)
                .SelectMany(group =>
            {
                var testOnly = inventory.IsComplete && group.All(item => item.TestClassification == TestClassification.Test);
                return group.Select(usage =>
            {
                token.ThrowIfCancellationRequested();
                decisions.TryGetValue(usage.PackageId, out var package);
                var maximum = usage.Repository.Category == RepositoryCategory.Platform ? package?.PlatformMax :
                    usage.Repository.Category == RepositoryCategory.IC ? package?.ICMax : null;
                return new ReferenceRow(usage, maximum is not null && usage.Version is { } version && PackageVersions.Equal(version, maximum), testOnly);
            });
            })).ToArray();
            var choices = result.OriginalRefs.Where(source => source.Category == RepositoryCategory.IC).Select(source => new RepositoryChoice(source)).ToArray();
            var reviews = packageRows.SelectMany(package => package.Decision.Repositories.Where(repo => choices.Any(choice => choice.Source.Id == repo.RepositoryId))
                .Select(repo => new ReviewRow(repo, package, choices.Single(choice => choice.Source.Id == repo.RepositoryId).Source,
                    completeness.TryGetValue(repo.RepositoryId, out var complete) && complete))).ToArray();
            var metadataIssues = packageRows.Where(package => package.Decision.IsSoup && package.Decision.NuGetCandidate is { } candidate &&
                (candidate.Status is CandidateStatus.Failed or CandidateStatus.NotFound || candidate.Status == CandidateStatus.Available && !candidate.IsAvailable || candidate.Version?.IsPrerelease == true))
                .SelectMany(package => package.Decision.Repositories.SelectMany(repository => repository.Usages).Select(usage => usage.Repository)
                    .Where(source => source.Category == RepositoryCategory.IC).DistinctBy(source => source.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(source => new IssueRow(source.Name,
                        package.Decision.NuGetCandidate!.Status == CandidateStatus.Failed ? "NuGetCandidateFailed" : package.Decision.NuGetCandidate.Status == CandidateStatus.NotFound ? "NuGetCandidateNotFound" : "NoFormalNuGetCandidate",
                        $"{package.PackageId}: {package.CandidateStatus}", null, false, source.Id)));
            var issueRows = result.Inventories.SelectMany(inventory => inventory.Issues.Select(issue => new IssueRow(inventory.Source.Name, issue.Code, issue.Message, issue.SourceFile, issue.BlocksUpdates, inventory.Source.Id)))
                .Concat(metadataIssues)
                .Concat(result.Packages.Where(package => package.PlatformPackage?.UnavailableReason is not null)
                    .SelectMany(package => package.Repositories.SelectMany(repository => repository.Usages)
                        .Select(usage => usage.Repository).Where(source => source.Category != RepositoryCategory.Platform)
                        .DistinctBy(source => source.Id, StringComparer.OrdinalIgnoreCase)
                        .Select(source => new IssueRow(source.Name, "PlatformTagUnresolved",
                            $"{package.PackageId}: {package.PlatformPackage!.UnavailableReason}", null, true, source.Id))))
                .ToArray();
            return (packageRows, referenceRows, choices, reviews, issueRows);
        }, token);
        token.ThrowIfCancellationRequested();
        installingRows = true;
        try
        {
            foreach (var package in projected.packageRows)
            {
                var preference = settings.CandidatePreferences.FirstOrDefault(item => item.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase));
                if (package.Decision.IsSoup && preference is not null) package.SelectedTarget = package.Choices.SingleOrDefault(choice => choice.Source == preference.Source);
                if (package.Decision.Ownership == PackageOwnership.Internal && preference?.Source == TargetSource.Custom)
                    package.CustomVersionText = preference.CustomVersion ?? "";
                package.PropertyChanged += PackageChanged;
            }
            foreach (var row in projected.reviews) row.PropertyChanged += SelectionChanged;
            Packages = projected.packageRows;
            References = projected.referenceRows;
            ReviewRows = projected.reviews;
            RepositoryChoices = projected.choices;
            Issues = projected.issueRows;
            Analysis = result;
            IsAnalysisStale = false;
            SelectedPackage = null;
            foreach (var row in Repositories)
            {
                var inventory = result.Inventories.SingleOrDefault(item => item.Source.Id == row.Source.Id);
                row.ScanStatus = inventory is null ? "Not scanned" : $"{inventory.Status} | {inventory.Usages.Length} references | {inventory.Snapshot?.Commit[..Math.Min(12, inventory.Snapshot.Commit.Length)] ?? "No commit"}";
            }
            InstallChangeRepositories();
            InvalidateApproval("Analysis installed; choose packages in the current IC repository");
        }
        finally { installingRows = false; }
        InstallAnalysisRepositories(result);
        ConfigureFilters();
        RefreshCommands();
    }

    private Task ExportAsync()
    {
        var immutableAnalysis = Analysis;
        if (immutableAnalysis is null) return Task.CompletedTask;
        string? path = null;
        ViewAction(() => path = interaction.ChooseReportPath(settings.OutputDirectory));
        if (path is null) return Task.CompletedTask;
        var targets = Packages.Where(package => package.SelectedTarget is not null).Select(CreateTarget).ToImmutableArray();
        var selections = CurrentChangeRows.Where(row => row.IsSelected)
            .Select(row => new RepositoryPackageSelection(row.Repository.Id, row.PackageId)).ToImmutableArray();
        var reportPlan = !IsAnalysisStale && plan is not null &&
            (plan.Status == ChangePlanStatus.PreviewOnly ? plan.MatchesSnapshot(immutableAnalysis) : plan.Matches(immutableAnalysis))
            && PlanMatchesChangeRepository(plan) ? plan : null;
        var selection = new ReportSelection(immutableAnalysis.Id, targets, selections, reportPlan);
        return RunAsync("Exporting Excel", async (progress, token) =>
        {
            await export.ExportAsync(immutableAnalysis, path, progress, token, selection);
            settings = CurrentSettings() with { OutputDirectory = System.IO.Path.GetDirectoryName(path) };
            await settingsStore.SaveAsync(settings, token);
            Status = $"Exported snapshot {immutableAnalysis.Id} to {path}";
            Stage = "Completed";
        });
    }

    private Task ReviewAsync() => RunAsync("Preparing exact change plan", async (_, token) =>
    {
        var current = Analysis ?? throw new InvalidOperationException("Analysis required.");
        if (IsAnalysisStale) throw new InvalidOperationException("Rescan changed sources before review.");
        if (!HasValidChangeScope) throw new InvalidOperationException("Choose a complete normal IC repository; review is blocked.");
        if (SmokeMode) throw new InvalidOperationException("Review commands are disabled in smoke mode.");
        var selected = CurrentChangeRows.Where(row => row.IsSelected).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Select packages in the current IC repository before review.");
        if (selected.Any(row => !row.Eligible)) throw new InvalidOperationException("Select an eligible target in Analysis for every selected package.");
        var selections = selected.Select(row => new RepositoryPackageSelection(row.Repository.Id, row.PackageId)).ToImmutableArray();
        var targets = selected.Select(row => row.Package).Distinct().Select(CreateTarget).ToImmutableArray();
        var revision = selectionRevision;
        var pendingRetry = retryPlan;
        var candidate = pendingRetry is not null && pendingRetry.Matches(current) && PlanMatchesChangeRepository(pendingRetry)
            ? pendingRetry
            : await Task.Run(() => current.CanUpdate ? new ChangePlanner().Create(current, selections, targets)
                : new ChangePlanner().CreatePreview(current, selections, targets), token);
        token.ThrowIfCancellationRequested();
        var fresh = await configurationService.LoadAsync(token);
        if (!IsCurrentConfiguration(fresh, current))
        {
            InvalidateAnalysis("Manifest or source configuration changed; rescan required");
            throw new InvalidOperationException("Configuration changed after analysis.");
        }
        if (selectionRevision != revision)
            throw new InvalidOperationException("Selection or target changed during review; review the current choices again.");
        if (candidate.Status == ChangePlanStatus.PreviewOnly)
        {
            var blockers = $"{(!current.CompleteIC ? "IC baseline incomplete. " : "")}{(!current.CompletePlatform ? "Platform baseline incomplete. " : "")}{GlobalBaselineBlockReasons}";
            interaction.ShowPreview(candidate, blockers);
            token.ThrowIfCancellationRequested();
            plan = candidate;
            ReviewStatus = $"{ChangeScopeName}: Preview ready; not approved; {blockers}. Approval/execution blocked; rescan and create a new plan.";
            Status = ReviewStatus;
            return;
        }
        if (candidate.Status == ChangePlanStatus.NoChanges) { plan = candidate; ReviewStatus = $"{ChangeScopeName}: no changes; selected usages are aligned"; Status = ReviewStatus; return; }
        if (!interaction.Approve(candidate)) { plan = null; ReviewStatus = $"{ChangeScopeName}: approval declined; no remote operation"; Status = ReviewStatus; return; }
        token.ThrowIfCancellationRequested();
        if (selectionRevision != revision)
            throw new InvalidOperationException("Selection or target changed during approval; review the current choices again.");
        plan = candidate.Approve(current, Environment.UserName, DateTimeOffset.UtcNow);
        retryPlan = candidate;
        ReviewStatus = $"{ChangeScopeName}: approved {plan.Changes.Length} package changes, {plan.Changes.Sum(change => change.Usages.Length)} declarations. Compatibility unverified.";
        Status = "Approved; execute is a separate action";
    });

    private static PackageTargetSelection CreateTarget(PackageRow package) => new(package.PackageId, package.SelectedTarget!.Source,
        package.SelectedTarget.Source == TargetSource.Custom ? package.SelectedTarget.Version.ToNormalizedString() : null);

    private Task ExecuteAsync() => RunAsync("Executing approved IC changes", async (progress, token) =>
    {
        var approved = plan ?? throw new InvalidOperationException("Explicit approval required.");
        var current = Analysis ?? throw new InvalidOperationException("Analysis required.");
        if (!approved.CanExecute || IsAnalysisStale || !approved.Matches(current) || !PlanMatchesChangeRepository(approved))
            throw new InvalidOperationException("Approval is stale or belongs to another Changes repository; review and approve the current repository.");
        var fresh = await configurationService.LoadAsync(token);
        if (!IsCurrentConfiguration(fresh, current))
        {
            InvalidateAnalysis("Configuration changed before execution");
            throw new InvalidOperationException("Configuration changed. Rescan and approve again.");
        }
        Results = Array.Empty<RepositoryUpdateResult>();
        ResultsAreStale = false;
        ResultStatus = "Executing; outcomes will be recorded by the executor";
        plan = null;
        RemoteOutcomeNeedsReview = true;
        var result = await update.ExecuteAsync(approved, current, progress, token);
        Results = result.Repositories;
        RemoteOutcomeNeedsReview = false;
        ResultStatus = $"{result.Status} | plan {result.PlanId} | {result.Repositories.Length} repositories. Remaining mismatches require rescan.";
        Status = ResultStatus;
        Stage = result.Status.ToString();
        if (result.Status == UpdateStatus.Completed)
        {
            retryPlan = null;
            IsAnalysisStale = true;
            ReviewStatus = "Plan executed successfully. Rescan repositories to update baselines.";
        }
        else
        {
            ReviewStatus = $"{ChangeScopeName}: Update {result.Status.ToString().ToLowerInvariant()}; review and approve again to resume plan {approved.Id}. Existing branch/PR outcomes will be reconciled.";
            RefreshCommands();
        }
    });

    private async Task RunAsync(string description, Func<IProgress<OperationProgress>, CancellationToken, Task> action)
    {
        dispatcher.VerifyAccess();
        if (IsBusy) { UserError = "Another operation is active."; return; }
        IsBusy = true;
        UserError = null;
        Status = description;
        Stage = description;
        IsIndeterminate = true;
        ProgressCounts = "";
        var id = operationId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        operationCancellation = cancellation;
        var completion = operationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stopwatch.Restart();
        EnqueueLog(description);
        var progress = new Progress<OperationProgress>(report => { if (IsBusy && operationId == id) ApplyProgress(report); });
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await action(progress, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Stage = "Cancelled";
            Status = "Cancelled. Inspect recorded outcomes; successful remote writes are not rolled back.";
            ResultStatus = description.Contains("Executing", StringComparison.Ordinal) ? "Cancelled; inspect executor journal for partial remote outcomes" : ResultStatus;
            EnqueueLog(Status);
        }
        catch (Exception exception)
        {
            Stage = "Failed";
            UserError = SafeError(exception);
            Status = "Operation failed; see error and log";
            if (description.Contains("Executing", StringComparison.Ordinal)) ResultStatus = "Failed; inspect executor journal before retrying";
            logger.LogError("Desktop operation {Operation} failed ({ExceptionType})", description, exception.GetType().Name);
            EnqueueLog(UserError);
        }
        finally
        {
            stopwatch.Stop();
            Elapsed = stopwatch.Elapsed.ToString(@"hh\:mm\:ss");
            operationCancellation = null;
            IsBusy = false;
            FlushLogs();
            RefreshCommands();
            completion.TrySetResult();
        }
    }

    private static string SafeError(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("Bearer", StringComparison.OrdinalIgnoreCase) || message.Contains("access_token", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("refresh_token", StringComparison.OrdinalIgnoreCase) || message.Contains("authorization", StringComparison.OrdinalIgnoreCase))
            return $"{exception.GetType().Name}: authentication details omitted. Check registration and permissions.";
        return message.Length > 1200 ? message[..1200] : message;
    }

    private void ApplyProgress(OperationProgress progress)
    {
        Stage = progress.Stage.ToString();
        IsIndeterminate = progress.Total is null or <= 0;
        ProgressMaximum = progress.Total is > 0 ? progress.Total.Value : 1;
        ProgressValue = progress.Total is > 0 ? Math.Clamp(progress.Completed, 0, progress.Total.Value) : 0;
        ProgressCounts = progress.Total is > 0 ? $"{progress.Completed} / {progress.Total}" : progress.Completed > 0 ? $"{progress.Completed} completed" : "";
        if (progress.RepositoryId is { } repositoryId)
        {
            var row = Repositories.FirstOrDefault(item => item.Source.Id == repositoryId);
            if (row is not null) row.ScanStatus = progress.Stage.ToString();
        }
        if (!string.IsNullOrWhiteSpace(progress.Detail)) EnqueueLog(progress.Detail);
    }
    private void EnqueueLog(string message)
    {
        while (pendingLogs.Count >= 200) pendingLogs.Dequeue();
        pendingLogs.Enqueue(new(DateTimeOffset.Now, Stage, message));
    }
    private void FlushLogs()
    {
        if (pendingLogs.Count == 0) return;
        var batch = new List<LogEntry>();
        while (pendingLogs.Count > 0 && batch.Count < 30) batch.Add(pendingLogs.Dequeue());
        Logs.AppendBatch(batch);
    }
    private void OnTick(object? sender, EventArgs args) { if (IsBusy) Elapsed = stopwatch.Elapsed.ToString(@"hh\:mm\:ss"); FlushLogs(); }
    public Task WaitForIdleAsync() => operationCompletion?.Task ?? Task.CompletedTask;
    public void CancelActiveOperation() => CancelCommand.Execute(null);
    public void SetWindowLayout(double width, double height, bool maximized) => settings = settings with { Window = new(width, height, maximized) };
    public WindowLayout WindowLayout => settings.Window;
    public Task PersistOnCloseAsync() => IsInitialized && !SmokeMode ? SaveSettingsAsync() : Task.CompletedTask;

    private void AddRepository()
    {
        try
        {
            var source = ParseRepository(NewRepositoryUrl, NewRepositoryBranch, NewRepositoryCategory);
            if (Repositories.Any(row => row.Source.Id.Equals(source.Id, StringComparison.OrdinalIgnoreCase) || row.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase) || row.Url.Equals(source.Url, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Repository already exists.");
            var row = new RepositoryRow(source);
            row.PropertyChanged += RepositoryChanged;
            Repositories = [.. Repositories, row];
            NewRepositoryUrl = "";
            NewRepositoryCategory = null;
            NewRepositoryValidation = "Repository added; save settings to persist";
            InvalidateAnalysis("Repository list changed; rescan required");
        }
        catch (ArgumentException exception) { NewRepositoryValidation = exception.Message; UserError = exception.Message; }
    }

    private void ValidateNewRepository()
    {
        if (string.IsNullOrWhiteSpace(NewRepositoryUrl)) { NewRepositoryValidation = ""; return; }
        try
        {
            var source = ParseRepository(NewRepositoryUrl, NewRepositoryBranch, NewRepositoryCategory);
            NewRepositoryValidation = $"{source.Name} | {source.Category} | Branch";
        }
        catch (ArgumentException exception) { NewRepositoryValidation = exception.Message; }
    }
    partial void OnNewRepositoryUrlChanged(string value) => ValidateNewRepository();
    partial void OnNewRepositoryBranchChanged(string value) => ValidateNewRepository();
    partial void OnNewRepositoryCategoryChanged(RepositoryCategory? value) => ValidateNewRepository();

    public static RepositorySource ParseRepository(string text, string branch, RepositoryCategory? explicitCategory)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            throw new ArgumentException("Enter a credential-free HTTPS Azure repository URL.");
        AzureRepoUrl parsed;
        try { parsed = AzureRepoUrl.Parse(url.AbsoluteUri); AzureRepoUrl.ValidateRef(RefKind.Branch, branch); }
        catch (InvalidDataException exception) { throw new ArgumentException(exception.Message); }
        var name = parsed.Repository;
        var known = RepositoryClassifier.Classify(name);
        var category = known ?? explicitCategory ?? throw new ArgumentException("Unknown repository name: choose IC or IC Emulator explicitly.");
        if (category == RepositoryCategory.Platform) throw new ArgumentException("Platform additions are manifest-only.");
        if (!Enum.IsDefined(category) || string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("Select a category and enter a branch.");
        return new(parsed.NormalizedRepoUrl.ToLowerInvariant(), name, parsed.NormalizedRepoUrl, category, RefKind.Branch, branch);
    }
    private void RemoveRepository(RepositoryRow? row)
    {
        if (row?.CanDelete != true) return;
        row.PropertyChanged -= RepositoryChanged;
        Repositories = Repositories.Where(item => item != row).ToArray();
        InvalidateAnalysis("Repository removed; rescan required");
    }

    private void PackageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!installingRows && args.PropertyName is nameof(PackageRow.SelectedTarget) or nameof(PackageRow.CustomVersionText))
        {
            InvalidateApproval("Target changed; review and approve again");
            RefreshAnalysisViews();
            RefreshChangeSelection();
        }
    }
    private void SelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!installingRows && !batchingChangeSelection && args.PropertyName == nameof(ReviewRow.IsSelected))
        {
            if (sender is ReviewRow row && CurrentChangeRows.Contains(row))
                InvalidateApproval("Package selection changed; review and approve the current repository again");
            RefreshChangeSelection();
        }
    }
    private void InvalidateAnalysis(string reason) { IsAnalysisStale = Analysis is not null; InvalidateApproval(reason); }
    private bool IsCurrentConfiguration(LoadedConfiguration fresh, AnalysisResult current) => configuration is not null &&
        fresh.ManifestHash == current.ManifestHash &&
        fresh.Rules.SchemaVersion == configuration.Rules.SchemaVersion && fresh.Rules.MaxConcurrency == configuration.Rules.MaxConcurrency &&
        fresh.Rules.TestProjectPatterns.SequenceEqual(configuration.Rules.TestProjectPatterns) &&
        fresh.Authentication.TenantId == configuration.Authentication.TenantId && fresh.Authentication.ClientId == configuration.Authentication.ClientId &&
        fresh.Authentication.RedirectUri == configuration.Authentication.RedirectUri && fresh.Authentication.Scopes.SequenceEqual(configuration.Authentication.Scopes) &&
        ConfigurationPolicy.CreateScanRequest(fresh, CurrentSettings()).Sources.SequenceEqual(current.OriginalRefs);
    private void InvalidateApproval(string reason)
    {
        selectionRevision++;
        plan = null;
        retryPlan = null;
        ReviewStatus = $"{ChangeScopeName}: {reason}";
        ResultsAreStale = Results.Count > 0;
        RefreshCommands();
    }
    private void ConfigureFilters()
    {
        PackagesView = CollectionViewSource.GetDefaultView(Packages);
        PackagesView.Filter = item => item is PackageRow row &&
            row.HasScopedUsages &&
            (string.IsNullOrWhiteSpace(Search) || row.PackageId.Contains(Search, StringComparison.OrdinalIgnoreCase)) &&
            (OwnershipFilter == "All ownership" || row.Ownership == OwnershipFilter) &&
            (ActionFilter == "All actions" || (ActionFilter == "Need changes" ? row.Action is "Upgrade" or "Downgrade" or "Mixed" : row.Action == ActionFilter)) &&
            (CategoryFilter == "All categories" || (SelectedAnalysisRepository?.Id is not null ? row.ScopeCategory == CategoryFilter : row.Decision.Repositories.SelectMany(repo => repo.Usages).Any(usage => usage.Repository.Category.ToString() == CategoryFilter)));
        ReferencesView = CollectionViewSource.GetDefaultView(References);
        ReferencesView.Filter = item => item is ReferenceRow row &&
            (SelectedAnalysisRepository?.Id is not { } repositoryId || row.RepositoryId.Equals(repositoryId, StringComparison.OrdinalIgnoreCase)) &&
            (SelectedPackage is null || row.PackageId.Equals(SelectedPackage.PackageId, StringComparison.OrdinalIgnoreCase)) &&
            (ReferenceCategoryFilter == "All references" || row.Category == ReferenceCategoryFilter) &&
            (string.IsNullOrWhiteSpace(Search) || row.PackageId.Contains(Search, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(PackagesView));
        OnPropertyChanged(nameof(ReferencesView));
        IssuesView = CollectionViewSource.GetDefaultView(Issues);
        IssuesView.Filter = item => item is IssueRow row && (ShowAllScanIssues || SelectedAnalysisRepository?.Id is not { } repositoryId || string.Equals(row.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(IssuesView));
    }
    partial void OnSearchChanged(string value) { filterTimer.Stop(); filterTimer.Start(); }
    partial void OnOwnershipFilterChanged(string value) { filterTimer.Stop(); filterTimer.Start(); }
    partial void OnActionFilterChanged(string value) { filterTimer.Stop(); filterTimer.Start(); }
    partial void OnCategoryFilterChanged(string value) { filterTimer.Stop(); filterTimer.Start(); }
    partial void OnReferenceCategoryFilterChanged(string value) { filterTimer.Stop(); filterTimer.Start(); }
    partial void OnSelectedPackageChanged(PackageRow? value) { OnPropertyChanged(nameof(HasDetails)); ReferencesView.Refresh(); }
    partial void OnIsAnalysisStaleChanged(bool value) { OnPropertyChanged(nameof(AnalysisSummary)); OnPropertyChanged(nameof(RepositoryAnalysisSummary)); RefreshChangeSelection(); RefreshCommands(); }
    partial void OnAnalysisChanged(AnalysisResult? value) => OnPropertyChanged(nameof(AnalysisSummary));
    partial void OnResultsAreStaleChanged(bool value) { OnPropertyChanged(nameof(ResultSummary)); OnPropertyChanged(nameof(ChangeResultSummary)); }
    partial void OnResultStatusChanged(string value) { OnPropertyChanged(nameof(ResultSummary)); OnPropertyChanged(nameof(ChangeResultSummary)); }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); RefreshCommands(); }
    partial void OnIsInitializedChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); RefreshCommands(); }
    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanScan)); OnPropertyChanged(nameof(CanReview)); OnPropertyChanged(nameof(CanExecute));
        OnPropertyChanged(nameof(CanReviewDraft)); OnPropertyChanged(nameof(ReviewButtonLabel));
        InitializeCommand?.NotifyCanExecuteChanged(); ScanCommand?.NotifyCanExecuteChanged(); ExportCommand?.NotifyCanExecuteChanged();
        LoginCommand?.NotifyCanExecuteChanged(); SignOutCommand?.NotifyCanExecuteChanged(); SaveSettingsCommand?.NotifyCanExecuteChanged();
        ReviewCommand?.NotifyCanExecuteChanged(); ExecuteCommand?.NotifyCanExecuteChanged();
        SelectUpgradesCommand?.NotifyCanExecuteChanged(); SelectAllPackagesCommand?.NotifyCanExecuteChanged(); ClearPackageSelectionCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSelectChangePackages)); OnPropertyChanged(nameof(ChangeRepositorySummary));
        AddRepositoryCommand?.NotifyCanExecuteChanged(); RemoveRepositoryCommand?.NotifyCanExecuteChanged(); CancelCommand?.NotifyCanExecuteChanged(); SettingsCommand?.NotifyCanExecuteChanged();
    }
    private void OpenPullRequest(RepositoryUpdateResult? result)
    {
        if (result?.PullRequestUrl is not { } text || !Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo))
        { UserError = "The pull request link is not a valid credential-free HTTPS URL."; return; }
        try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception exception) { UserError = SafeError(exception); }
    }
    private void ViewAction(Action action)
    {
        try { action(); }
        catch (Exception exception) { UserError = SafeError(exception); EnqueueLog(UserError); }
    }
    public void Dispose()
    {
        timer.Stop(); filterTimer.Stop(); operationCancellation?.Cancel();
        foreach (var row in Repositories) row.PropertyChanged -= RepositoryChanged;
        foreach (var row in Packages) row.PropertyChanged -= PackageChanged;
        foreach (var row in ReviewRows) row.PropertyChanged -= SelectionChanged;
    }
}