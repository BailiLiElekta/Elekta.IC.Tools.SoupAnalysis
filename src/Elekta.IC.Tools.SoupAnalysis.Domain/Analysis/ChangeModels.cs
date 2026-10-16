using System.Collections.Immutable;
using NuGet.Versioning;

namespace Elekta.IC.Tools.SoupAnalysis.Domain;

public enum ChangePlanStatus { WaitingForApproval, Approved, NoChanges, PreviewOnly }
public enum UpdateStatus { Completed, PartiallyFailed, Failed, Cancelled }
public enum RepositoryUpdateStatus { Completed, Failed, Cancelled, PushedOnly }

public sealed record RepositoryCommit(string RepositoryId, string Commit);
public sealed record RepositoryPackageSelection(string RepositoryId, string PackageId);
public sealed record PackageTargetSelection(string PackageId, TargetSource Source, string? CustomVersion = null);
public sealed record UsageChange(PackageUsage Usage, NuGetVersion Target, AlignmentAction Action);
public sealed record PlannedPackageChange(
    string RepositoryId, string PackageId, TargetSource TargetSource, NuGetVersion Target,
    bool TestOnly, ImmutableArray<UsageChange> Usages);
public sealed record PlanApproval(string ApprovedBy, DateTimeOffset ApprovedAt);

public sealed record ChangePlan
{
    internal ChangePlan(Guid id, AnalysisResult analysis, ImmutableArray<PlannedPackageChange> changes,
        ImmutableArray<RepositoryPackageSelection> selections, ImmutableArray<PackageTargetSelection> targets,
        ImmutableArray<PackageUsage> selectedUsages, bool previewOnly = false)
    {
        Id = id;
        AnalysisId = analysis.Id;
        ManifestHash = analysis.ManifestHash;
        OriginalRefs = analysis.OriginalRefs;
        BaselineCommits = GetCommits(analysis);
        Changes = changes;
        Selections = selections;
        Targets = targets;
        SelectedUsages = selectedUsages;
        Status = previewOnly ? ChangePlanStatus.PreviewOnly
            : changes.IsEmpty ? ChangePlanStatus.NoChanges : ChangePlanStatus.WaitingForApproval;
    }

    public Guid Id { get; }
    public Guid AnalysisId { get; }
    public string ManifestHash { get; }
    public ImmutableArray<RepositorySource> OriginalRefs { get; }
    public ImmutableArray<RepositoryCommit> BaselineCommits { get; }
    public ImmutableArray<PlannedPackageChange> Changes { get; }
    public ImmutableArray<RepositoryPackageSelection> Selections { get; }
    public ImmutableArray<PackageTargetSelection> Targets { get; }
    public ImmutableArray<PackageUsage> SelectedUsages { get; }
    public ChangePlanStatus Status { get; private init; }
    public PlanApproval? Approval { get; private init; }
    public bool CanExecute => Status == ChangePlanStatus.Approved && Approval is not null && !Changes.IsEmpty;

    public bool Matches(AnalysisResult analysis) => analysis.CanUpdate && MatchesSnapshot(analysis);

    public bool MatchesSnapshot(AnalysisResult analysis) => AnalysisId == analysis.Id
        && ManifestHash == analysis.ManifestHash && OriginalRefs.SequenceEqual(analysis.OriginalRefs)
        && BaselineCommits.SequenceEqual(GetCommits(analysis));

    public ChangePlan Approve(AnalysisResult currentAnalysis, string approvedBy, DateTimeOffset approvedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);
        if (Status != ChangePlanStatus.WaitingForApproval || !Matches(currentAnalysis))
            throw new InvalidOperationException("Only a current, nonempty change plan can be approved.");
        return this with { Status = ChangePlanStatus.Approved, Approval = new(approvedBy, approvedAt) };
    }

    private static ImmutableArray<RepositoryCommit> GetCommits(AnalysisResult analysis) =>
        [.. analysis.Inventories.Where(inventory => inventory.Source.Category != RepositoryCategory.ICEmulator
            && inventory.Snapshot is not null)
            .OrderBy(inventory => inventory.Source.Id, StringComparer.OrdinalIgnoreCase)
            .Select(inventory => new RepositoryCommit(inventory.Source.Id, inventory.Snapshot!.Commit))];
}

public sealed record RepositoryUpdateResult(
    string RepositoryId, RepositoryUpdateStatus Status, string? Branch = null,
    string? Commit = null, string? PullRequestUrl = null, string? Detail = null);
public sealed record UpdateResult(Guid PlanId, UpdateStatus Status, ImmutableArray<RepositoryUpdateResult> Repositories);