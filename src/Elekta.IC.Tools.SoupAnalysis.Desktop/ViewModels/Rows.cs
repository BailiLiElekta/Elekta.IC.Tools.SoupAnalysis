using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.AzureDevOps;
using NuGet.Versioning;

namespace Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels;

public sealed class RepositoryRow : ObservableObject
{
    private RefKind refKind;
    private string reference;
    private string scanStatus = "Not scanned";
    public RepositoryRow(RepositorySource source)
    {
        Source = source;
        refKind = source.RefKind;
        reference = source.Ref;
    }
    public RepositorySource Source { get; }
    public string Name => Source.Name;
    public string Url => Source.Url;
    public string Category => Source.Category == RepositoryCategory.ICEmulator ? "IC Emulator" : Source.Category.ToString();
    public bool CanDelete => Source.Category != RepositoryCategory.Platform;
    public RefKind[] AllowedRefKinds => CanDelete ? [Domain.RefKind.Branch] : Enum.GetValues<RefKind>();
    public RefKind RefKind { get => refKind; set { if (SetProperty(ref refKind, value)) OnPropertyChanged(nameof(Validation)); } }
    public string Ref { get => reference; set { if (SetProperty(ref reference, value)) OnPropertyChanged(nameof(Validation)); } }
    public string ScanStatus { get => scanStatus; set => SetProperty(ref scanStatus, value); }
    public string Validation
    {
        get
        {
            if (!AllowedRefKinds.Contains(RefKind)) return "Branch required";
            try { AzureRepoUrl.ValidateRef(RefKind, Ref); return "Valid"; }
            catch (InvalidDataException exception) { return exception.Message; }
        }
    }
    public RepositorySource ToSource() => Source with { RefKind = RefKind, Ref = Ref.Trim() };
}

public sealed record TargetChoice(TargetSource Source, NuGetVersion Version, bool IsProvisional = false)
{
    public string Label => $"{(Source == TargetSource.NuGetCandidate ? "NuGetMax" : Source)}: {Version.ToNormalizedString()}" +
        (IsProvisional ? " (provisional - preview only)" : "");
}

public sealed record AnalysisRepositoryChoice(string? Id, string Name, RepositoryCategory? Category)
{
    public string Label => Category is null ? Name : $"{Name} ({(Category == RepositoryCategory.ICEmulator ? "IC Emulator" : Category.ToString())})";
}

public sealed class PackageRow : ObservableObject
{
    private TargetChoice? selectedTarget;
    private string customVersionText = "";
    private RepositorySource? scopedRepository;
    private IReadOnlyList<PackageUsage> scopedUsages = Array.Empty<PackageUsage>();
    private bool scopeComplete;
    private readonly IReadOnlyDictionary<string, bool>? repositoryCompleteness;
    private readonly string baselineBlockReasons;
    public PackageRow(PackageDecision decision, IReadOnlyDictionary<string, bool>? repositoryCompleteness = null, string baselineBlockReasons = "")
    {
        Decision = decision;
        this.repositoryCompleteness = repositoryCompleteness;
        this.baselineBlockReasons = baselineBlockReasons;
        var choices = new List<TargetChoice>();
        if (decision.Ownership == PackageOwnership.SharedWithPlatform && decision.IsPlatformMaxComplete && decision.PlatformMax is { } platform)
            choices.Add(new(TargetSource.PlatformMax, platform));
        if (decision.IsSoup && decision.ICMax is { } current)
            choices.Add(new(TargetSource.ICMax, current, !decision.IsICMaxComplete));
        if (decision.IsSoup && decision.NuGetCandidate is { IsAvailable: true, Version: { IsPrerelease: false } candidate })
            choices.Add(new(TargetSource.NuGetCandidate, candidate));
        Choices = choices;
        if (decision.Ownership == PackageOwnership.SharedWithPlatform) selectedTarget = choices.SingleOrDefault();
    }
    public PackageDecision Decision { get; }
    public string PackageId => Decision.PackageId;
    public string Ownership => Decision.Ownership == PackageOwnership.Soup ? "IC SOUP" : Decision.Ownership.ToString();
    public string SourceLabel => Decision.Ownership switch
    {
        PackageOwnership.SharedWithPlatform => "Platform shared",
        PackageOwnership.Soup => "IC SOUP",
        PackageOwnership.Internal => "Internal",
        _ => "Unconfirmed"
    };
    public bool IsSoupTargetEditable => Decision.IsSoup && Choices.Count > 0 && IsEditableScope;
    public bool IsCustomTargetEditable => Decision.Ownership == PackageOwnership.Internal && IsEditableScope;
    private bool IsEditableScope => scopedRepository is null || scopedRepository.Category == RepositoryCategory.IC;
    public bool IsTargetEditable => IsSoupTargetEditable;
    public string CustomVersionText
    {
        get => customVersionText;
        set
        {
            if (!SetProperty(ref customVersionText, value)) return;
            if (Decision.Ownership == PackageOwnership.Internal)
                SelectedTarget = PackageVersions.ParseExact(value.Trim()) is { } parsed ? new(TargetSource.Custom, parsed) : null;
            OnPropertyChanged(nameof(TargetError));
            OnPropertyChanged(nameof(IsCustomTargetValid));
            OnPropertyChanged(nameof(Target));
            OnPropertyChanged(nameof(Action));
            OnPropertyChanged(nameof(Tags));
            OnPropertyChanged(nameof(MismatchTag));
        }
    }
    public bool IsCustomTargetValid => Decision.Ownership == PackageOwnership.Internal && selectedTarget?.Source == TargetSource.Custom;
    public string? TargetError => Decision.Ownership == PackageOwnership.Internal && !string.IsNullOrWhiteSpace(CustomVersionText) && !IsCustomTargetValid
        ? "Enter an exact NuGet version; ranges and wildcards are not allowed." : null;
    public IReadOnlyList<TargetChoice> Choices { get; }
    public TargetChoice? SelectedTarget
    {
        get => selectedTarget;
        set
        {
            if (value is not null && !(Decision.Ownership == PackageOwnership.Internal && value.Source == TargetSource.Custom) && !Choices.Contains(value))
                throw new ArgumentException("Target is not eligible.");
            if (Decision.Ownership == PackageOwnership.SharedWithPlatform && value != Choices.SingleOrDefault())
                throw new InvalidOperationException("Shared target is fixed to PlatformMax.");
            if (SetProperty(ref selectedTarget, value))
            {
                OnPropertyChanged(nameof(Target));
                OnPropertyChanged(nameof(Action));
                OnPropertyChanged(nameof(Tags));
                OnPropertyChanged(nameof(MismatchTag));
                OnPropertyChanged(nameof(IsCustomTargetValid));
                OnPropertyChanged(nameof(TargetError));
                OnPropertyChanged(nameof(TargetAvailability));
                OnPropertyChanged(nameof(IsTargetUnavailable));
                OnPropertyChanged(nameof(TargetUnavailableReason));
                OnPropertyChanged(nameof(TargetInfoText));
                OnPropertyChanged(nameof(TargetTooltip));
            }
        }
    }
    public string PlatformMax => Display(Decision.PlatformMax);
    public string ICMax => !Decision.IsSoup ? "N/A" : Decision.ICMax is null ? "Unknown"
        : Display(Decision.ICMax) + (Decision.IsICMaxComplete ? "" : " (provisional)");
    public string NuGetMax => Decision.IsSoup && Decision.NuGetCandidate is { IsAvailable: true, Version.IsPrerelease: false } candidate ? Display(candidate.Version) : "N/A";
    public string CandidateStatus => Decision.IsSoup && Decision.NuGetCandidate is { } candidate
        ? $"{candidate.Status}: {candidate.Detail ?? candidate.Source} ({candidate.QueriedAt:g})" : "Not requested";
    public bool IsTargetUnavailable => Decision.Ownership == PackageOwnership.Unconfirmed
        || Decision.Ownership == PackageOwnership.SharedWithPlatform && selectedTarget is null
        || Decision.IsSoup && Choices.Count == 0;
    public string TargetAvailability => IsTargetUnavailable ? "Unavailable" : !IsEditableScope ? "Read-only"
        : SelectedTarget?.IsProvisional == true ? "Preview only; IC baseline incomplete"
        : Decision.Ownership == PackageOwnership.SharedWithPlatform ? "Locked to PlatformMax"
        : Decision.Ownership == PackageOwnership.Internal ? "Manual optional" : "Available";
    private string NuGetUnavailableReason => Decision.NuGetCandidate switch
    {
        { Status: Domain.CandidateStatus.Failed } => "NuGet query failed",
        { Status: Domain.CandidateStatus.NotFound, Detail: { } detail } when detail.Contains("formal", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("stable", StringComparison.OrdinalIgnoreCase) || detail.Contains("prerelease", StringComparison.OrdinalIgnoreCase) => "No formal NuGet version",
        { Status: Domain.CandidateStatus.NotFound } => "NuGet package not found",
        { Status: Domain.CandidateStatus.Available } => "No formal NuGet version",
        _ => "NuGet query not completed"
    };
    public string TargetInfoText => SelectedTarget?.IsProvisional == true ? "Preview only; IC baseline incomplete"
        : !IsTargetUnavailable ? "" : Decision.Ownership == PackageOwnership.Unconfirmed ? "Ownership unconfirmed"
        : Decision.Ownership == PackageOwnership.SharedWithPlatform ? "Platform baseline incomplete"
        : string.Join("; ", new[] { !Decision.IsICMaxComplete ? "IC baseline incomplete" : "ICMax unknown", NuGetUnavailableReason });
    public string TargetUnavailableReason => !IsTargetUnavailable ? "" : string.Join(". ", new[]
    {
        TargetInfoText,
        baselineBlockReasons.Length > 0 ? baselineBlockReasons : null,
        Decision.IsSoup ? CandidateStatus : null
    }.Where(text => !string.IsNullOrWhiteSpace(text)));
    public string TargetTooltip => IsTargetUnavailable ? TargetUnavailableReason
        : SelectedTarget?.IsProvisional == true ? $"{SelectedTarget.Label}. Approval and execution require a complete new scan. {baselineBlockReasons}"
        : Decision.Ownership == PackageOwnership.SharedWithPlatform ? $"Locked to PlatformMax; {SelectedTarget?.Label}"
        : !IsEditableScope ? $"{scopedRepository?.Category} scope is read-only"
        : Decision.Ownership == PackageOwnership.Internal ? "Manual optional; enter an exact version"
        : SelectedTarget?.Label ?? "Choose ICMax (provisional is preview only) or NuGetMax (formal releases)";
    private IEnumerable<PackageUsage> CurrentUsages => scopedRepository is null
        ? Decision.Repositories.SelectMany(row => row.Usages).Where(usage => usage.Repository.Category == RepositoryCategory.IC)
        : scopedUsages;
    public string CurrentVersions => string.Join(", ", CurrentUsages.Select(usage => usage.RawVersion).Distinct());
    public string ScopeCategory => scopedRepository?.Category.ToString() ?? Categories;
    public bool HasScopedUsages => scopedRepository is null
        ? Decision.Repositories.Any(repository => repository.Usages.Any(usage => usage.Repository.Category != RepositoryCategory.ICEmulator))
        : scopedUsages.Count > 0;
    public int ProjectCount => CurrentUsages.Select(usage => (usage.Repository.Id, usage.ProjectPath)).Distinct().Count();
    public void SetRepositoryScope(RepositorySource? repository, IReadOnlyList<PackageUsage>? usages, bool inventoryComplete)
    {
        scopedRepository = repository;
        scopedUsages = usages ?? Array.Empty<PackageUsage>();
        scopeComplete = inventoryComplete;
        foreach (var property in new[] { nameof(CurrentVersions), nameof(Action), nameof(MismatchTag), nameof(Tags),
            nameof(TestOnlyStatus), nameof(IsTestOnly), nameof(ScopeCategory), nameof(HasScopedUsages), nameof(ProjectCount), nameof(IsTargetEditable),
            nameof(IsSoupTargetEditable), nameof(IsCustomTargetEditable), nameof(TargetAvailability), nameof(TargetTooltip) })
            OnPropertyChanged(property);
    }
    public string Target => TargetError is not null ? "Invalid exact version"
        : Decision.Ownership == PackageOwnership.Internal && selectedTarget is null ? "Not applicable"
        : selectedTarget?.Version.ToNormalizedString() ?? (IsTargetUnavailable ? "No confirmed target" : "Awaiting selection");
    public string Action
    {
        get
        {
            if (scopedRepository is not null && scopedRepository.Category != RepositoryCategory.IC) return "NotApplicable";
            if (scopedRepository is not null && !scopeComplete) return "Unresolved";
            if (TargetError is not null) return "Unresolved";
            if (Decision.Ownership == PackageOwnership.Internal && selectedTarget is null) return "NotApplicable";
            if (IsTargetUnavailable) return "Unresolved";
            var actions = CurrentUsages
                .Select(usage => usage.Version is null ? AlignmentAction.Unresolved : selectedTarget is null ? AlignmentAction.AwaitingSelection : AlignmentAnalyzer.GetAction(usage, selectedTarget.Version))
                .Distinct().ToArray();
            if (actions.Contains(AlignmentAction.Unresolved)) return "Unresolved";
            var changes = actions.Where(action => action != AlignmentAction.Aligned).ToArray();
            return changes.Length == 1 ? changes[0].ToString() : changes.Length > 1 ? "Mixed" : actions.Length > 0 ? "Aligned" : "NotApplicable";
        }
    }
    private bool IsRepositoryComplete(RepositoryPackageDecision repository) => repositoryCompleteness is not null
        ? repositoryCompleteness.TryGetValue(repository.RepositoryId, out var complete) && complete
        : repository.Usages.All(usage => usage.Repository.Category == RepositoryCategory.Platform ? Decision.IsPlatformMaxComplete : Decision.IsICMaxComplete);
    public bool IsTestOnly => scopedRepository is null
        ? Decision.Repositories.Any(row => IsRepositoryComplete(row) && row.Usages.Length > 0 && row.Usages.All(usage => usage.TestClassification == TestClassification.Test))
        : scopeComplete && scopedUsages.Count > 0 && scopedUsages.All(usage => usage.TestClassification == TestClassification.Test);
    public string TestOnlyStatus => scopedRepository is null
        ? IsTestOnly ? "Any: " + string.Join(", ", Decision.Repositories.Where(row => IsRepositoryComplete(row) && row.TestOnly).Select(row => row.Usages.First().Repository.Name))
            : Decision.Repositories.Any(row => !IsRepositoryComplete(row) || row.Usages.Any(usage => usage.TestClassification == TestClassification.Unknown)) ? "Unconfirmed" : "No (any repository)"
        : !scopeComplete || scopedUsages.Count == 0 || scopedUsages.Any(usage => usage.TestClassification == TestClassification.Unknown) ? "Unconfirmed" : IsTestOnly ? "Yes" : "No";
    public string TestOnly => string.Join(", ", Decision.Repositories.Where(row => row.TestOnly)
        .Select(row => Decision.Repositories.SelectMany(item => item.Usages).FirstOrDefault(usage => usage.Repository.Id == row.RepositoryId)?.Repository.Name ?? row.RepositoryId));
    public string Categories => string.Join(", ", Decision.Repositories.SelectMany(row => row.Usages).Select(usage => usage.Repository.Category).Distinct());
    public string HighestTag => Decision.PlatformMax is not null && Decision.ICMax is not null ? "Platform / IC" : Decision.PlatformMax is not null ? "Platform" : Decision.ICMax is not null ? "IC" : "Unknown";
    public string SoupTag => Decision.IsSoup ? "SOUP" : "";
    public string SharedTag => Decision.Ownership == PackageOwnership.SharedWithPlatform ? "Shared" : "";
    public string MismatchTag => Action is "Upgrade" or "Downgrade" or "Mixed" ? "Mismatch" : "";
    public string CompletenessTag => !Decision.IsPlatformMaxComplete || !Decision.IsICMaxComplete ? "Provisional" : "Complete";
    public string Tags => string.Join(" | ", new[]
    {
        Decision.IsSoup ? "SOUP" : null,
        Decision.Ownership == PackageOwnership.SharedWithPlatform ? "Shared" : null,
        Action is "Upgrade" or "Downgrade" or "Mixed" ? "Mismatch" : null,
        Decision.PlatformMax is not null || Decision.ICMax is not null ? "Highest versions listed" : "Unknown",
        !Decision.IsPlatformMaxComplete || !Decision.IsICMaxComplete ? "Provisional" : null
    }.Where(text => text is not null));
    private static string Display(NuGetVersion? version) => version?.ToNormalizedString() ?? "Unknown";
}

public sealed record ReferenceRow(PackageUsage Usage, bool Highest, bool TestOnly)
{
    public string Repository => Usage.Repository.Name;
    public string RepositoryId => Usage.Repository.Id;
    public string Category => Usage.Repository.Category == RepositoryCategory.ICEmulator ? "IC Emulator" : Usage.Repository.Category.ToString();
    public string PackageId => Usage.PackageId;
    public string Version => Usage.RawVersion;
    public string Tags => $"{(Highest ? "Highest" : "")}{(TestOnly ? " TEST Only" : "")}".Trim();
    public string Project => Usage.ProjectPath;
    public string File => Usage.Declaration.VersionOwner.SourceFile;
    public string Owner => $"{Usage.Declaration.Kind}: {Usage.Declaration.VersionOwner.ElementPath}";
    public string Condition => Usage.Condition ?? "Unconditional";
    public string Ref => $"{Usage.Repository.RefKind}: {Usage.Repository.Ref}";
}

public sealed class RepositoryChoice : ObservableObject
{
    private bool isSelected;
    public RepositoryChoice(RepositorySource source) => Source = source;
    public RepositorySource Source { get; }
    public string Name => Source.Name;
    public bool IsSelected { get => isSelected; set => SetProperty(ref isSelected, value); }
}

public sealed class ReviewRow : ObservableObject
{
    private bool isSelected;
    public ReviewRow(RepositoryPackageDecision decision, PackageRow package, RepositorySource repository, bool inventoryComplete = true)
    {
        Decision = decision;
        Package = package;
        Repository = repository;
        InventoryComplete = inventoryComplete;
        package.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(NewVersion)); OnPropertyChanged(nameof(Action)); OnPropertyChanged(nameof(Reason)); OnPropertyChanged(nameof(Eligible)); };
    }
    public RepositoryPackageDecision Decision { get; }
    public PackageRow Package { get; }
    public RepositorySource Repository { get; }
    private bool InventoryComplete { get; }
    public string RepositoryName => Repository.Name;
    public string PackageId => Package.PackageId;
    public bool IsSelected { get => isSelected; set => SetProperty(ref isSelected, value); }
    public string OldVersion => string.Join(", ", Decision.Usages.Select(usage => usage.RawVersion).Distinct());
    public string NewVersion => Package.Target;
    public string Action
    {
        get
        {
            if (!InventoryComplete || Package.Decision.Ownership == PackageOwnership.Unconfirmed || Decision.Usages.Any(usage => usage.Version is null)) return "Unresolved";
            if (Package.TargetError is not null) return "Unresolved";
            if (Package.SelectedTarget is not { } target) return Package.Decision.Ownership == PackageOwnership.Internal ? "NotApplicable"
                : Package.IsTargetUnavailable ? "Unresolved" : "AwaitingSelection";
            var actions = Decision.Usages.Select(usage => AlignmentAnalyzer.GetAction(usage, target.Version)).Distinct().ToArray();
            if (actions.Contains(AlignmentAction.Unresolved)) return "Unresolved";
            var changes = actions.Where(action => action != AlignmentAction.Aligned).ToArray();
            return changes.Length > 1 ? "Mixed" : changes.Length == 1 ? changes[0].ToString() : "Aligned";
        }
    }
    public string Reason => Package.TargetError is not null ? Package.TargetError
        : Package.SelectedTarget?.IsProvisional == true ? $"{Package.Ownership}; ICMax provisional - preview only"
        : Package.Decision.Ownership == PackageOwnership.Internal && Package.SelectedTarget is null ? "Internal; no SOUP alignment required"
        : Package.IsTargetUnavailable ? Package.TargetUnavailableReason
        : $"{Package.Ownership}; {Package.SelectedTarget?.Source.ToString() ?? "target required"}{(Decision.TestOnly ? "; TEST Only" : "")}";
    public string Files => string.Join(", ", Decision.Usages.Select(usage => usage.Declaration.VersionOwner.SourceFile).Distinct());
    public string? SelectionBlockReason => !InventoryComplete || !Package.Decision.IsPlatformMaxComplete ? "Incomplete inventory"
        : Repository.Category != RepositoryCategory.IC || Package.Decision.Ownership == PackageOwnership.Unconfirmed ? "Unknown ownership"
        : Decision.Usages.Length == 0 || Decision.Usages.Any(usage => usage.Version is null) ? "Unresolved versions"
        : Decision.Usages.Any(usage => !usage.Declaration.IsUniquelyOwned || usage.Declaration.Kind == VersionDeclarationKind.Unresolved ||
            string.IsNullOrWhiteSpace(usage.Declaration.VersionOwner.SourceFile) || string.IsNullOrWhiteSpace(usage.Declaration.VersionOwner.ElementPath)) ? "Unsafe ownership"
        : !HasValidTarget ? "Target missing" : null;
    public bool CanSelectForChange => SelectionBlockReason is null;
    public bool PreviewEligible => CanSelectForChange;
    public bool Eligible => PreviewEligible;
    private bool HasValidTarget =>
        (Package.Decision.Ownership == PackageOwnership.Internal ? Package.IsCustomTargetValid
            : Package.Decision.Ownership == PackageOwnership.SharedWithPlatform ? Package.SelectedTarget?.Source == TargetSource.PlatformMax
            : Package.Decision.IsSoup && Package.SelectedTarget is { Source: TargetSource.ICMax or TargetSource.NuGetCandidate } target && Package.Choices.Contains(target));
}

public sealed record IssueRow(string Repository, string Code, string Message, string? File, bool BlocksUpdates, string? RepositoryId = null);
public sealed record LogEntry(DateTimeOffset Time, string Stage, string Message);