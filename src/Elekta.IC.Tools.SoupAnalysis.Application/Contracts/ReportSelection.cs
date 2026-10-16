using System.Collections.Immutable;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using NuGet.Versioning;

namespace Elekta.IC.Tools.SoupAnalysis.Application;

public sealed record ReportSelection(Guid AnalysisId, ImmutableArray<PackageTargetSelection> Targets,
    ImmutableArray<RepositoryPackageSelection> Selections, ChangePlan? ReviewPlan = null)
{
    public NuGetVersion? ResolveTarget(PackageDecision package) => Targets.FirstOrDefault(target =>
        target.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase)) is { } selected
        ? ResolveTarget(package, selected) : null;

    private bool IsReadOnly => ReviewPlan is null || ReviewPlan.Status == ChangePlanStatus.PreviewOnly;
    private NuGetVersion ResolveTarget(PackageDecision package, PackageTargetSelection target) => IsReadOnly
        ? ChangePlanner.ResolvePreviewTarget(package, target.Source, target.CustomVersion)
        : ChangePlanner.ResolveTarget(package, target.Source, target.CustomVersion);

    public void Validate(AnalysisResult analysis)
    {
        if (AnalysisId != analysis.Id || Targets.IsDefault || Selections.IsDefault
            || Targets.GroupBy(target => target.PackageId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1)
            || Selections.GroupBy(selection => (selection.RepositoryId.ToUpperInvariant(), selection.PackageId.ToUpperInvariant())).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Report selections must belong to this analysis and be unique.");
        foreach (var target in Targets)
            _ = ResolveTarget(analysis.Packages.Single(package => package.PackageId.Equals(target.PackageId, StringComparison.OrdinalIgnoreCase)), target);
        foreach (var selection in Selections)
            if (!analysis.Inventories.Any(inventory => inventory.Source.Id == selection.RepositoryId && inventory.Source.Category == RepositoryCategory.IC
                && inventory.Usages.Any(usage => usage.PackageId.Equals(selection.PackageId, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("Report selection identifies an unknown IC package usage.");
        if (ReviewPlan is not null)
        {
            if (!(IsReadOnly ? ReviewPlan.MatchesSnapshot(analysis) : ReviewPlan.Matches(analysis)))
                throw new InvalidOperationException("Report plan does not match this analysis.");
            var selectedPackageIds = Selections.Select(selection => selection.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedTargets = Targets.Where(target => selectedPackageIds.Contains(target.PackageId)).ToImmutableArray();
            var current = IsReadOnly ? new ChangePlanner().CreatePreview(analysis, Selections, selectedTargets)
                : new ChangePlanner().Create(analysis, Selections, selectedTargets);
            if (!current.Selections.SequenceEqual(ReviewPlan.Selections) || !current.Targets.SequenceEqual(ReviewPlan.Targets)
                || !current.SelectedUsages.SequenceEqual(ReviewPlan.SelectedUsages)
                || !current.Changes.SelectMany(change => change.Usages).SequenceEqual(ReviewPlan.Changes.SelectMany(change => change.Usages))
                || !current.Changes.Select(change => (change.RepositoryId, change.PackageId, change.TargetSource, change.Target))
                    .SequenceEqual(ReviewPlan.Changes.Select(change => (change.RepositoryId, change.PackageId, change.TargetSource, change.Target))))
                throw new InvalidOperationException("Report choices differ from the reviewed plan.");
        }
    }
}