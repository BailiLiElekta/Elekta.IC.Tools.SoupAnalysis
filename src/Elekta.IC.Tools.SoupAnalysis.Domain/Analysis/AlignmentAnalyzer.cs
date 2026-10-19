using System.Collections.Immutable;
using NuGet.Versioning;

namespace Elekta.IC.Tools.SoupAnalysis.Domain;

public static class AlignmentAnalyzer
{
    public static AnalysisResult Analyze(
        Guid analysisId, PlatformManifest manifest, string manifestHash,
        ImmutableArray<RepositorySource> sources, ImmutableArray<RepositoryInventory> inventories,
        DateTimeOffset scannedAt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var platformSources = sources.Where(source => source.Category == RepositoryCategory.Platform).ToArray();
        var completePlatform = !manifest.Repositories.IsDefaultOrEmpty && !string.IsNullOrWhiteSpace(manifestHash)
            && manifest.Repositories.All(member => platformSources.Count(source =>
                source.Id.Equals(member.Id, StringComparison.OrdinalIgnoreCase)
                && source.Name == member.Name && source.Url == member.Url) == 1)
            && platformSources.Length == manifest.Repositories.Length
            && platformSources.All(source => IsComplete(source, inventories));
        var completeIC = sources.Where(source => source.Category == RepositoryCategory.IC)
            .All(source => IsComplete(source, inventories));
        var included = inventories.Where(inventory => sources.Contains(inventory.Source));
        var packages = included.SelectMany(inventory => inventory.Usages)
            .Where(usage => usage.Repository.Category != RepositoryCategory.ICEmulator
                || manifest.FindPackageRepositoryId(usage.PackageId) is not null)
            .Select(usage =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return usage;
            })
            .GroupBy(usage => usage.PackageId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => Decide(group.Key, [.. group], completePlatform, completeIC,
                platformSources.SingleOrDefault(source => source.Id.Equals(
                    manifest.FindPackageRepositoryId(group.Key), StringComparison.OrdinalIgnoreCase)), cancellationToken)).ToImmutableArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new(analysisId, scannedAt, manifest.Version, manifestHash, sources, inventories,
            packages, completePlatform, completeIC);
    }

    public static AlignmentAction GetAction(PackageUsage usage, NuGetVersion target)
    {
        if (usage.Version is not { } current)
            return AlignmentAction.Unresolved;
        var comparison = VersionComparer.VersionRelease.Compare(current, target);
        return comparison < 0 ? AlignmentAction.Upgrade
            : comparison > 0 ? AlignmentAction.Downgrade : AlignmentAction.Aligned;
    }

    private static bool IsComplete(RepositorySource source, ImmutableArray<RepositoryInventory> inventories)
    {
        var matching = inventories.Where(inventory => inventory.Source.Id.Equals(source.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matching.Length == 1 && matching[0].Source == source && matching[0].IsComplete
            && matching[0].Snapshot!.Source == source
            && !string.IsNullOrWhiteSpace(matching[0].Snapshot!.Commit)
            && matching[0].Usages.All(usage => usage.Repository == source);
    }

    private static PackageDecision Decide(string packageId, ImmutableArray<PackageUsage> usages,
        bool completePlatform, bool completeIC, RepositorySource? producer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var platform = usages.Where(usage => usage.Repository.Category == RepositoryCategory.Platform).ToArray();
        var normalIC = usages.Where(usage => usage.Repository.Category == RepositoryCategory.IC).ToArray();
        var platformMax = Maximum(platform, cancellationToken);
        PlatformPackageBaseline? baseline = null;
        if (producer is not null)
        {
            var tag = producer.Ref.StartsWith('v') || producer.Ref.StartsWith('V') ? producer.Ref[1..] : producer.Ref;
            var version = producer.RefKind == RefKind.Tag ? PackageVersions.ParseExact(tag) : null;
            baseline = new(producer, version, version is not null ? null
                : $"{producer.Name}: {producer.RefKind} '{producer.Ref}' does not provide an exact NuGet version tag.");
        }
        var ownership = producer is not null || platform.Length > 0 ? PackageOwnership.SharedWithPlatform
            : !completePlatform ? PackageOwnership.Unconfirmed
            : packageId.StartsWith("elekta.", StringComparison.OrdinalIgnoreCase)
                ? PackageOwnership.Internal : PackageOwnership.Soup;
        var repositories = usages.GroupBy(usage => usage.Repository.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RepositoryPackageDecision(group.Key, packageId,
                group.All(usage => usage.TestClassification == TestClassification.Test), [.. group],
                DecideAction([.. group], ownership, baseline is null ? platformMax : baseline.Version, completePlatform))).ToImmutableArray();
        return new(packageId, ownership, platformMax, Maximum(normalIC, cancellationToken), null,
            completePlatform, completeIC, repositories) { PlatformPackage = baseline };
    }

    private static NuGetVersion? Maximum(IEnumerable<PackageUsage> usages, CancellationToken cancellationToken)
    {
        NuGetVersion? maximum = null;
        foreach (var usage in usages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (usage.Version is { } version && (maximum is null || VersionComparer.VersionRelease.Compare(version, maximum) > 0))
                maximum = version;
        }
        return maximum;
    }

    private static AlignmentAction DecideAction(ImmutableArray<PackageUsage> usages,
        PackageOwnership ownership, NuGetVersion? platformMax, bool completePlatform)
    {
        if (usages.Any(usage => usage.Version is null) || ownership == PackageOwnership.Unconfirmed)
            return AlignmentAction.Unresolved;
        if (usages[0].Repository.Category == RepositoryCategory.Platform || ownership == PackageOwnership.Internal)
            return AlignmentAction.NotApplicable;
        if (ownership == PackageOwnership.Soup)
            return AlignmentAction.AwaitingSelection;
        if (!completePlatform || platformMax is null)
            return AlignmentAction.Unresolved;
        var actions = usages.Select(usage => GetAction(usage, platformMax)).Distinct().ToArray();
        var changes = actions.Where(action => action != AlignmentAction.Aligned).ToArray();
        return changes.Length == 0 ? AlignmentAction.Aligned
            : changes.Length == 1 ? changes[0] : AlignmentAction.Mixed;
    }
}