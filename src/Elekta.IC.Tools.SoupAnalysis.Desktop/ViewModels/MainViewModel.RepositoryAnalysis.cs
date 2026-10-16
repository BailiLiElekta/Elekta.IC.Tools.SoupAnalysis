using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Elekta.IC.Tools.SoupAnalysis.Domain;

namespace Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private readonly Dictionary<string, Dictionary<string, PackageUsage[]>> analysisRepositoryPackages = new(StringComparer.OrdinalIgnoreCase);
    [ObservableProperty] private IReadOnlyList<AnalysisRepositoryChoice> analysisRepositoryChoices = Array.Empty<AnalysisRepositoryChoice>();
    [ObservableProperty] private AnalysisRepositoryChoice? selectedAnalysisRepository;
    [ObservableProperty] private bool showAllScanIssues;
    public ICollectionView IssuesView { get; private set; } = CollectionViewSource.GetDefaultView(Array.Empty<IssueRow>());
    public string AnalysisScopeTitle => SelectedAnalysisRepository?.Id is null ? "Global package summary" : SelectedAnalysisRepository.Label;
    public string GlobalBaselineBlockReasons => Analysis is null ? "" : GetBaselineBlockReasons(Analysis);
    public string GlobalBaselineBlockSummary => Analysis is null ? "" : string.Join(" | ", new[]
    {
        !Analysis.CompletePlatform ? "Platform baseline incomplete" : null,
        !Analysis.CompleteIC ? "IC baseline incomplete" : null
    }.Where(text => text is not null));
    private static string GetBaselineBlockReasons(AnalysisResult result)
    {
        var reasons = new List<string>();
        foreach (var category in new[] { RepositoryCategory.Platform, RepositoryCategory.IC })
        {
            var incomplete = result.OriginalRefs.Where(source => source.Category == category).Select(source =>
            {
                var inventory = result.Inventories.FirstOrDefault(item => string.Equals(item.Source.Id, source.Id, StringComparison.OrdinalIgnoreCase));
                var name = result.OriginalRefs.Count(item => string.Equals(item.Name, source.Name, StringComparison.OrdinalIgnoreCase)) > 1
                    ? $"{source.Name} [{source.Id}]" : source.Name;
                return inventory?.IsComplete == true ? null : $"{name}: {(inventory is null ? "Missing inventory" : inventory.Status == InventoryStatus.Complete ? "Unresolved" : inventory.Status.ToString())}";
            }).Where(text => text is not null).ToArray();
            if (incomplete.Length > 0 || !(category == RepositoryCategory.Platform ? result.CompletePlatform : result.CompleteIC))
                reasons.Add($"{category} baseline incomplete" + (incomplete.Length > 0 ? $" ({string.Join("; ", incomplete)})" : ""));
        }
        return string.Join(Environment.NewLine, reasons);
    }
    public string RepositoryAnalysisSummary
    {
        get
        {
            if (Analysis is null) return "No analysis yet";
            var inventory = SelectedInventory;
            var local = Packages.Where(row => row.HasScopedUsages).ToArray();
            var scanState = SelectedAnalysisRepository?.Id is null ? "All scanned repositories" : inventory is null ? "Unresolved / no inventory" :
                $"{(inventory.IsComplete ? "Complete" : inventory.Status == InventoryStatus.Failed ? "RepoFailed" : "Unresolved")} | SHA {inventory.Snapshot?.Commit ?? "unresolved"}";
            var error = inventory is { IsComplete: false } ? " | " + string.Join("; ", inventory.Issues.Select(issue => issue.Message)) : "";
            return $"{scanState}{error} | {local.Length} packages{(inventory is { IsComplete: false } || SelectedAnalysisRepository?.Id is not null && inventory is null ? " (known; absence unconfirmed)" : "")} | " +
                $"{local.Count(row => row.Decision.Ownership == PackageOwnership.SharedWithPlatform)} shared | {local.Count(row => row.Decision.IsSoup)} IC SOUP | " +
                $"{local.Count(row => row.Action is "Upgrade" or "Downgrade" or "Mixed")} need changes | {local.Count(row => row.IsTestOnly)} TEST Only | " +
                $"Platform authority: {(Analysis.CompletePlatform ? "complete" : "incomplete")} | IC authority: {(Analysis.CompleteIC ? "complete" : "incomplete")} | " +
                $"Updates: {(SelectedAnalysisRepository?.Category == RepositoryCategory.ICEmulator ? "blocked (IC Emulator read-only)" : SelectedAnalysisRepository?.Category == RepositoryCategory.Platform ? "blocked (Platform read-only)" : Analysis.CanUpdate && !IsAnalysisStale ? "eligible" : "blocked (incomplete or stale baselines)")}" +
                (GlobalBaselineBlockSummary.Length > 0 ? $" | {GlobalBaselineBlockSummary}" : "");
        }
    }
    private RepositoryInventory? SelectedInventory => Analysis?.Inventories.FirstOrDefault(inventory => string.Equals(inventory.Source.Id, SelectedAnalysisRepository?.Id, StringComparison.OrdinalIgnoreCase));

    private void InstallAnalysisRepositories(AnalysisResult result)
    {
        var previousId = SelectedAnalysisRepository?.Id;
        var preserveGlobal = SelectedAnalysisRepository is { Id: null };
        analysisRepositoryPackages.Clear();
        foreach (var inventory in result.Inventories)
            analysisRepositoryPackages[inventory.Source.Id] = inventory.Usages.GroupBy(usage => usage.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        AnalysisRepositoryChoices = [new(null, "All repositories (global summary)", null),
            .. result.OriginalRefs.Select(source => new AnalysisRepositoryChoice(source.Id, source.Name, source.Category))];
        SelectedAnalysisRepository = preserveGlobal ? AnalysisRepositoryChoices[0] :
            AnalysisRepositoryChoices.FirstOrDefault(choice => previousId is not null && string.Equals(choice.Id, previousId, StringComparison.OrdinalIgnoreCase)) ??
            AnalysisRepositoryChoices.FirstOrDefault(choice => choice.Category == RepositoryCategory.IC) ??
            AnalysisRepositoryChoices.FirstOrDefault(choice => choice.Id is not null) ?? AnalysisRepositoryChoices[0];
        ApplyAnalysisRepositoryScope();
    }

    partial void OnSelectedAnalysisRepositoryChanged(AnalysisRepositoryChoice? value) => ApplyAnalysisRepositoryScope();
    partial void OnShowAllScanIssuesChanged(bool value) => IssuesView.Refresh();

    private void ApplyAnalysisRepositoryScope()
    {
        dispatcher.VerifyAccess();
        SelectedPackage = null;
        Search = "";
        OwnershipFilter = "All ownership";
        ActionFilter = "All actions";
        CategoryFilter = "All categories";
        ReferenceCategoryFilter = "All references";
        var source = Analysis?.OriginalRefs.FirstOrDefault(source => string.Equals(source.Id, SelectedAnalysisRepository?.Id, StringComparison.OrdinalIgnoreCase));
        var inventory = SelectedInventory;
        Dictionary<string, PackageUsage[]>? usages = null;
        if (source is not null) analysisRepositoryPackages.TryGetValue(source.Id, out usages);
        foreach (var package in Packages)
            package.SetRepositoryScope(source, usages?.GetValueOrDefault(package.PackageId), inventory?.IsComplete == true);
        OnPropertyChanged(nameof(AnalysisScopeTitle));
        RefreshAnalysisViews();
    }

    private void RefreshAnalysisViews()
    {
        filterTimer.Stop();
        PackagesView?.Refresh();
        ReferencesView?.Refresh();
        IssuesView?.Refresh();
        OnPropertyChanged(nameof(RepositoryAnalysisSummary));
        OnPropertyChanged(nameof(GlobalBaselineBlockReasons));
        OnPropertyChanged(nameof(GlobalBaselineBlockSummary));
    }
}