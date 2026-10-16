using System.Collections.Immutable;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using NuGet.Versioning;

namespace Elekta.IC.Tools.SoupAnalysis.Application;

public sealed class ChangePlanner
{
    public ChangePlan Create(AnalysisResult analysis,
        ImmutableArray<RepositoryPackageSelection> selections,
        ImmutableArray<PackageTargetSelection> targets)
        => CreateCore(analysis, selections, targets, false);

    public ChangePlan CreatePreview(AnalysisResult analysis,
        ImmutableArray<RepositoryPackageSelection> selections,
        ImmutableArray<PackageTargetSelection> targets)
        => CreateCore(analysis, selections, targets, true);

    private static ChangePlan CreateCore(AnalysisResult analysis,
        ImmutableArray<RepositoryPackageSelection> selections,
        ImmutableArray<PackageTargetSelection> targets, bool previewOnly)
    {
        if ((!previewOnly && !analysis.CanUpdate) || string.IsNullOrWhiteSpace(analysis.ManifestHash))
            throw new InvalidOperationException("Complete Platform and normal IC analysis is required.");
        if (previewOnly && !analysis.CompletePlatform)
            throw new InvalidOperationException("Complete Platform analysis is required to confirm ownership for preview.");
        if (selections.IsDefaultOrEmpty || targets.IsDefaultOrEmpty)
            throw new ArgumentException("Select repository/packages and one target source per package.");
        if (selections.GroupBy(selection => (selection.RepositoryId.ToUpperInvariant(), selection.PackageId.ToUpperInvariant()))
            .Any(group => group.Count() != 1) || targets.GroupBy(target => target.PackageId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
            throw new ArgumentException("Selections must be unique and each package must have exactly one target source.");
        if (targets.Any(target => !selections.Any(selection => selection.PackageId.Equals(target.PackageId, StringComparison.OrdinalIgnoreCase)))
            || selections.Any(selection => !targets.Any(target => target.PackageId.Equals(selection.PackageId, StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("Target sources must correspond exactly to selected packages.");
        var changes = ImmutableArray.CreateBuilder<PlannedPackageChange>();
        var selectedUsages = ImmutableArray.CreateBuilder<PackageUsage>();
        foreach (var selection in selections)
        {
            var inventory = analysis.Inventories.SingleOrDefault(item => item.Source.Id.Equals(selection.RepositoryId, StringComparison.OrdinalIgnoreCase));
            if (inventory is null || inventory.Source.Category != RepositoryCategory.IC)
                throw new ArgumentException("Only known normal IC repositories may be selected.");
            if (!inventory.IsComplete || inventory.Source.RefKind != RefKind.Branch)
                throw new InvalidOperationException("Selected IC repository is incomplete or is not a branch source.");
            if (inventory.Snapshot is null || string.IsNullOrWhiteSpace(inventory.Snapshot.Commit)
                || inventory.Snapshot.Source != inventory.Source
                || !analysis.OriginalRefs.Contains(inventory.Source))
                throw new InvalidOperationException("Selected IC repository has no matching snapshot provenance.");
            var package = analysis.Packages.SingleOrDefault(item => item.PackageId.Equals(selection.PackageId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("Unknown selected package.");
            var usages = inventory.Usages.Where(usage => usage.PackageId.Equals(selection.PackageId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (usages.Length == 0)
                throw new ArgumentException("Selected package is not used by the selected repository.");
            var target = targets.Single(target => target.PackageId.Equals(selection.PackageId, StringComparison.OrdinalIgnoreCase));
            var targetSource = target.Source;
            var version = previewOnly ? ResolvePreviewTarget(package, targetSource, target.CustomVersion)
                : ResolveTarget(package, targetSource, target.CustomVersion);
            if (usages.Any(usage => usage.Version is null || !usage.Declaration.IsUniquelyOwned
                || usage.Repository != inventory.Source || usage.Declaration.Kind == VersionDeclarationKind.Unresolved
                || string.IsNullOrWhiteSpace(usage.Declaration.VersionOwner.SourceFile)
                || string.IsNullOrWhiteSpace(usage.Declaration.VersionOwner.ElementPath)))
                throw new InvalidOperationException("Selected declarations do not have resolved, uniquely identified ownership.");
            selectedUsages.AddRange(usages);
            var edits = usages.Select(usage => new UsageChange(usage, version, AlignmentAnalyzer.GetAction(usage, version)))
                .Where(change => change.Action != AlignmentAction.Aligned).ToImmutableArray();
            if (edits.Any(change => change.Action == AlignmentAction.Unresolved || !change.Usage.Declaration.IsUniquelyOwned
                || change.Usage.Declaration.Kind == VersionDeclarationKind.Unresolved))
                throw new InvalidOperationException("Selected declarations do not have resolved, uniquely identified ownership.");
            if (!edits.IsEmpty)
                changes.Add(new(inventory.Source.Id, package.PackageId, targetSource, version,
                    usages.All(usage => usage.TestClassification == TestClassification.Test), edits));
        }
        return new(Guid.NewGuid(), analysis, changes.ToImmutable(), selections, targets, selectedUsages.ToImmutable(), previewOnly);
    }

    internal static NuGetVersion ResolvePreviewTarget(PackageDecision package, TargetSource source, string? customVersion = null)
    {
        if (package.Ownership == PackageOwnership.Soup && source == TargetSource.ICMax && customVersion is null
            && package.ICMax is { } known)
            return known;
        return ResolveTarget(package, source, customVersion);
    }

    internal static NuGetVersion ResolveTarget(PackageDecision package, TargetSource source, string? customVersion = null)
    {
        if (!Enum.IsDefined(source))
            throw new ArgumentException("Unknown target source.");
        if (source == TargetSource.Custom)
        {
            if (package.Ownership != PackageOwnership.Internal)
                throw new ArgumentException("Only Internal packages may choose a custom version.");
            if (string.IsNullOrWhiteSpace(customVersion) || PackageVersions.ParseExact(customVersion) is not { } version)
                throw new ArgumentException("A custom target requires an explicit exact NuGet version.");
            return version;
        }
        if (customVersion is not null)
            throw new ArgumentException("Only Custom targets may specify a custom version.");
        if (package.Ownership == PackageOwnership.SharedWithPlatform)
        {
            if (source != TargetSource.PlatformMax)
                throw new ArgumentException("Shared packages are locked to PlatformMax.");
            return package.IsPlatformMaxComplete && package.PlatformMax is { } platform ? platform
                : throw new InvalidOperationException("Platform baseline is incomplete.");
        }
        if (package.Ownership != PackageOwnership.Soup || source == TargetSource.PlatformMax)
            throw new ArgumentException("Only SOUP packages may choose ICMax or NuGetCandidate.");
        return source switch
        {
            TargetSource.ICMax when package.IsICMaxComplete && package.ICMax is { } current => current,
            TargetSource.NuGetCandidate when package.NuGetCandidate is { IsAvailable: true, Version: { IsPrerelease: false } candidate } => candidate,
            _ => throw new InvalidOperationException("The selected candidate is unavailable or incomplete.")
        };
    }
}