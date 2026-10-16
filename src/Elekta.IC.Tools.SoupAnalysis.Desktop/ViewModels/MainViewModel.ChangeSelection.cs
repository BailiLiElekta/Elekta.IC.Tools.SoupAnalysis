using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Input;
using Elekta.IC.Tools.SoupAnalysis.Domain;

namespace Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private RepositoryChoice? selectedChangeRepository;
    private bool batchingChangeSelection;
    private IReadOnlyList<ReviewRow> CurrentChangeRows { get; set; } = Array.Empty<ReviewRow>();

    public RepositoryChoice? SelectedChangeRepository
    {
        get => selectedChangeRepository;
        set
        {
            dispatcher.VerifyAccess();
            if (IsBusy && !installingRows) return;
            var choice = value is null ? null : RepositoryChoices.FirstOrDefault(item =>
                item.Source.Id.Equals(value.Source.Id, StringComparison.OrdinalIgnoreCase));
            if (!SetProperty(ref selectedChangeRepository, choice)) return;
            SetChangeRepositoryScope();
            if (!installingRows) InvalidateApproval("Changes repository changed; review and approve this repository");
        }
    }

    public ICollectionView VisibleReviewRows { get; private set; } = null!;
    public ICollectionView VisibleResults { get; private set; } = null!;
    public RelayCommand SelectUpgradesCommand { get; private set; } = null!;
    public RelayCommand SelectAllPackagesCommand { get; private set; } = null!;
    public RelayCommand ClearPackageSelectionCommand { get; private set; } = null!;
    public string ChangeScopeName => SelectedChangeRepository is { } choice ? $"{choice.Name} | {choice.Source.Ref}" : "No IC repository";
    private RepositoryInventory? ChangeInventory => Analysis?.Inventories.FirstOrDefault(inventory =>
        string.Equals(inventory.Source.Id, SelectedChangeRepository?.Source.Id, StringComparison.OrdinalIgnoreCase));
    private bool HasValidChangeScope => SelectedChangeRepository?.Source.Category == RepositoryCategory.IC &&
        ChangeInventory?.IsComplete == true && CurrentChangeRows.Count > 0;
    public bool CanSelectChangePackages => CanEdit && Analysis is not null && !IsAnalysisStale && HasValidChangeScope;
    public string ChangeTableTitle => $"{ChangeScopeName} | {CurrentChangeRows.Count(row => row.IsSelected)} selected";
    public string ChangeResultSummary => $"{ChangeScopeName} | {Results.Count(row => string.Equals(row.RepositoryId, SelectedChangeRepository?.Source.Id, StringComparison.OrdinalIgnoreCase))} recorded outcomes in this repository | {ResultSummary}";
    public string ChangeRepositorySummary
    {
        get
        {
            if (SelectedChangeRepository is null) return "No normal IC repository selected; review blocked.";
            var inventory = ChangeInventory;
            var scopeState = inventory is null ? "Unresolved / no inventory; review blocked" : !inventory.IsComplete
                ? $"{inventory.Status}; review blocked; {string.Join("; ", inventory.Issues.Select(issue => issue.Message))}"
                : CurrentChangeRows.Count == 0 ? "Empty inventory; review blocked" : !CanSelectChangePackages
                ? IsBusy ? "Busy; selection locked" : IsAnalysisStale ? "Stale snapshot; rescan required" : "Incomplete global snapshots; review blocked"
                : Analysis?.CanUpdate == true ? "Complete" : "Selection and preview available; approval/execution blocked";
            return $"{ChangeScopeName} | {scopeState} | {CurrentChangeRows.Count} packages | {CurrentChangeRows.Count(row => row.IsSelected)} selected | " +
                $"{CurrentChangeRows.Count(row => row.Action == "Upgrade")} upgrades | {CurrentChangeRows.Count(row => row.Action == "Downgrade")} downgrades | " +
                $"{CurrentChangeRows.Count(row => row.Action == "Mixed")} mixed | {CurrentChangeRows.Count(row => row.Package.SelectedTarget is null && row.Action != "Unresolved")} awaiting targets | " +
                $"{CurrentChangeRows.Count(row => row.Action == "Unresolved")} unresolved | {CurrentChangeRows.Count(row => !row.Eligible)} skipped / ineligible";
        }
    }

    private void InitializeChangeSelection()
    {
        SelectUpgradesCommand = new RelayCommand(() => SelectChangePackages(row => row.Action == "Upgrade", "Select Upgrades"), () => CanSelectChangePackages);
        SelectAllPackagesCommand = new RelayCommand(() => SelectChangePackages(_ => true, "Select All"), () => CanSelectChangePackages);
        ClearPackageSelectionCommand = new RelayCommand(() => SelectChangePackages(_ => false, "Clear Selection"), () => CanSelectChangePackages);
        VisibleReviewRows = CollectionViewSource.GetDefaultView(ReviewRows);
        VisibleResults = CollectionViewSource.GetDefaultView(Results);
    }

    private void InstallChangeRepositories()
    {
        var previousId = selectedChangeRepository?.Source.Id;
        selectedChangeRepository = RepositoryChoices.FirstOrDefault(choice => choice.Source.Id.Equals(previousId, StringComparison.OrdinalIgnoreCase))
            ?? RepositoryChoices.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedChangeRepository));
        SetChangeRepositoryScope();
    }

    private void SetChangeRepositoryScope()
    {
        CurrentChangeRows = ReviewRows.Where(row => string.Equals(row.Repository.Id, selectedChangeRepository?.Source.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        VisibleReviewRows = CollectionViewSource.GetDefaultView(ReviewRows);
        VisibleReviewRows.Filter = item => item is ReviewRow row && string.Equals(row.Repository.Id, selectedChangeRepository?.Source.Id, StringComparison.OrdinalIgnoreCase);
        VisibleReviewRows.Refresh();
        OnPropertyChanged(nameof(VisibleReviewRows));
        RefreshChangeResults();
        RefreshChangeSelection();
    }

    private void RefreshChangeResults()
    {
        VisibleResults = CollectionViewSource.GetDefaultView(Results);
        VisibleResults.Filter = item => item is RepositoryUpdateResult row && string.Equals(row.RepositoryId, selectedChangeRepository?.Source.Id, StringComparison.OrdinalIgnoreCase);
        VisibleResults.Refresh();
        OnPropertyChanged(nameof(VisibleResults));
        OnPropertyChanged(nameof(ChangeResultSummary));
    }

    partial void OnResultsChanged(IReadOnlyList<RepositoryUpdateResult> value) => RefreshChangeResults();

    private void RefreshChangeSelection()
    {
        OnPropertyChanged(nameof(ChangeScopeName));
        OnPropertyChanged(nameof(ChangeTableTitle));
        OnPropertyChanged(nameof(ChangeRepositorySummary));
        OnPropertyChanged(nameof(ChangeResultSummary));
        RefreshCommands();
    }

    private bool PlanMatchesChangeRepository(ChangePlan candidate) => SelectedChangeRepository is { } choice &&
        candidate.Changes.All(change => change.RepositoryId.Equals(choice.Source.Id, StringComparison.OrdinalIgnoreCase));

    private void SelectChangePackages(Func<ReviewRow, bool> include, string command)
    {
        dispatcher.VerifyAccess();
        if (!CanSelectChangePackages) return;
        batchingChangeSelection = true;
        try
        {
            foreach (var row in CurrentChangeRows) row.IsSelected = row.Eligible && include(row);
        }
        finally { batchingChangeSelection = false; }
        var skipped = CurrentChangeRows.Where(row => !row.IsSelected).ToArray();
        var missing = skipped.Count(row => row.SelectionBlockReason == "Target missing");
        var unsafeCount = skipped.Count(row => row.SelectionBlockReason == "Unsafe ownership");
        var unresolved = skipped.Count(row => row.SelectionBlockReason is "Unresolved versions" or "Unknown ownership" or "Incomplete inventory");
        InvalidateApproval($"{command}: {CurrentChangeRows.Count(row => row.IsSelected)} selected; {skipped.Length} skipped " +
            $"({missing} target missing, {unsafeCount} unsafe ownership, {unresolved} unresolved / unknown, " +
            $"{skipped.Length - missing - unsafeCount - unresolved} outside selection). " +
            (Analysis?.CanUpdate == true ? "Review and approve required" : "Preview available; not approved; approval/execution blocked"));
        RefreshChangeSelection();
    }
}