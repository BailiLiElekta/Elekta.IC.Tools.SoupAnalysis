using System.Collections.Immutable;
using NuGet.Versioning;

namespace Elekta.IC.Tools.SoupAnalysis.Domain;

public enum InventoryStatus { Complete, Unresolved, Failed }
public enum PackageOwnership { SharedWithPlatform, Internal, Soup, Unconfirmed }
public enum CandidateStatus { Available, NotFound, Failed, NotRequested }
public enum AlignmentAction { Aligned, Upgrade, Downgrade, Mixed, AwaitingSelection, NotApplicable, Unresolved }

public sealed record InventoryIssue(string Code, string Message, string? SourceFile = null, bool BlocksUpdates = true);

public sealed record RepositoryInventory(
    RepositorySource Source,
    SourceSnapshot? Snapshot,
    ImmutableArray<PackageUsage> Usages,
    ImmutableArray<InventoryIssue> Issues,
    InventoryStatus Status)
{
    public bool IsComplete => Status == InventoryStatus.Complete && Snapshot is not null
        && !Issues.Any(issue => issue.BlocksUpdates) && Usages.All(usage => usage.Version is not null);
}

public sealed record NuGetCandidate(
    string PackageId, NuGetVersion? Version, CandidateStatus Status,
    string Source, DateTimeOffset QueriedAt, string? Detail = null)
{
    public bool IsAvailable => Status == CandidateStatus.Available && Version is not null;
}

public sealed record RepositoryPackageDecision(
    string RepositoryId, string PackageId, bool TestOnly,
    ImmutableArray<PackageUsage> Usages, AlignmentAction Action);

public sealed record PlatformPackageBaseline(RepositorySource Source, NuGetVersion? Version, string? UnavailableReason);

public sealed record PackageDecision(
    string PackageId, PackageOwnership Ownership,
    NuGetVersion? PlatformMax, NuGetVersion? ICMax, NuGetCandidate? NuGetCandidate,
    bool IsPlatformMaxComplete, bool IsICMaxComplete,
    ImmutableArray<RepositoryPackageDecision> Repositories)
{
    public bool IsSoup => Ownership == PackageOwnership.Soup;
    public PlatformPackageBaseline? PlatformPackage { get; init; }
    public TargetSource PlatformTargetSource => PlatformPackage is null ? TargetSource.PlatformMax : TargetSource.PlatformTag;
    public NuGetVersion? PlatformTarget => PlatformPackage is null ? PlatformMax : PlatformPackage.Version;
    public bool IsPlatformTargetComplete => IsPlatformMaxComplete && PlatformTarget is not null;
}

public sealed record AnalysisResult(
    Guid Id, DateTimeOffset ScannedAt, string ManifestVersion, string ManifestHash,
    ImmutableArray<RepositorySource> OriginalRefs,
    ImmutableArray<RepositoryInventory> Inventories,
    ImmutableArray<PackageDecision> Packages,
    bool CompletePlatform, bool CompleteIC)
{
    public bool CanUpdate => CompletePlatform && CompleteIC;
}