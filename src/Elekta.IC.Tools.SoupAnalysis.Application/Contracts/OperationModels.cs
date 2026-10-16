using System.Collections.Immutable;
using Elekta.IC.Tools.SoupAnalysis.Domain;

namespace Elekta.IC.Tools.SoupAnalysis.Application;

public enum OperationStage
{
    Idle, ResolvingRefs, ReadingSources, Parsing, Comparing, QueryingNuGet, Ready, Incomplete,
    Exporting, PreparingChanges, WaitingForApproval, Updating, Completed, PartiallyFailed, Cancelled, Failed
}

public sealed record OperationProgress(
    OperationStage Stage, string? RepositoryId = null, int Completed = 0,
    int? Total = null, TimeSpan Elapsed = default, string? Detail = null);

public sealed record ScanRequest(
    PlatformManifest Manifest, string ManifestHash, ImmutableArray<RepositorySource> Sources, AnalysisRules Rules);

public sealed record PackageScanResult(
    ImmutableArray<PackageUsage> Usages, ImmutableArray<InventoryIssue> Issues);

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"AccessToken (expires {ExpiresAt:O})";
}