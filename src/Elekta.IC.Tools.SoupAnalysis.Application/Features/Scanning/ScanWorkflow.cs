using System.Collections.Immutable;
using Elekta.IC.Tools.SoupAnalysis.Domain;

namespace Elekta.IC.Tools.SoupAnalysis.Application;

public sealed class ScanWorkflow(
    IRepositorySnapshotProvider snapshots, IPackageScanner scanner, INuGetCandidateProvider candidates,
    OperationGate? operations = null)
{
    private readonly OperationGate gate = operations ?? new();

    public async Task<AnalysisResult> ScanAsync(ScanRequest request, IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = gate.Enter();
        var reporter = new WorkflowProgress(progress);
        reporter.Report(OperationStage.ResolvingRefs, total: request.Sources.Length, force: true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfigurationPolicy.ValidateScanRequest(request);
            var inventories = new RepositoryInventory[request.Sources.Length];
            var completed = 0;
            await Parallel.ForEachAsync(Enumerable.Range(0, request.Sources.Length),
                new ParallelOptions { MaxDegreeOfParallelism = request.Rules.MaxConcurrency, CancellationToken = cancellationToken },
                async (index, token) =>
                {
                    var source = request.Sources[index];
                    SourceSnapshot? snapshot = null;
                    try
                    {
                        reporter.Report(OperationStage.ReadingSources, source.Id);
                        snapshot = await snapshots.GetSnapshotAsync(source, reporter.ForRepository(source.Id), token).ConfigureAwait(false);
                        if (snapshot.Source != source || snapshot.Commit.Length != 40 || !snapshot.Commit.All(Uri.IsHexDigit))
                            throw new InvalidOperationException("Snapshot must match the requested source and resolved full commit.");
                        reporter.Report(OperationStage.Parsing, source.Id);
                        var parsed = await Task.Run(() => scanner.Scan(snapshot, request.Rules, token), token).ConfigureAwait(false);
                        if (parsed.Usages.Any(usage => usage.Repository != source))
                            throw new InvalidOperationException("Scanner returned a usage for a different repository.");
                        var issues = parsed.Issues.AddRange(parsed.Usages.Where(usage => usage.Version is null)
                            .Select(usage => new InventoryIssue("UnresolvedVersion", "A package version is not an exact NuGet version.", usage.ProjectPath)));
                        inventories[index] = new(source, snapshot, parsed.Usages, issues,
                            issues.Any(issue => issue.BlocksUpdates) ? InventoryStatus.Unresolved : InventoryStatus.Complete);
                    }
                    catch (Exception exception) when (!token.IsCancellationRequested)
                    {
                        inventories[index] = new(source, snapshot, [],
                            [new("RepositoryScanFailed", $"Repository scan failed ({exception.GetType().Name}).")], InventoryStatus.Failed);
                    }
                    var count = Interlocked.Increment(ref completed);
                    reporter.Report(OperationStage.Parsing, source.Id, count, request.Sources.Length);
                }).ConfigureAwait(false);
            reporter.Report(OperationStage.Comparing, completed: completed, total: completed, force: true);
            var result = await Task.Run(() => AlignmentAnalyzer.Analyze(Guid.NewGuid(), request.Manifest,
                request.ManifestHash, request.Sources, [.. inventories], DateTimeOffset.UtcNow, cancellationToken), cancellationToken).ConfigureAwait(false);
            var decisions = result.Packages.ToArray();
            var soupIndexes = Enumerable.Range(0, decisions.Length).Where(index => decisions[index].IsSoup).ToArray();
            reporter.Report(OperationStage.QueryingNuGet, total: soupIndexes.Length, force: true);
            completed = 0;
            await Parallel.ForEachAsync(soupIndexes,
                new ParallelOptions { MaxDegreeOfParallelism = request.Rules.MaxConcurrency, CancellationToken = cancellationToken },
                async (index, token) =>
                {
                    var package = decisions[index];
                    NuGetCandidate candidate;
                    try
                    {
                        candidate = await candidates.GetCandidateAsync(package.PackageId, token).ConfigureAwait(false);
                        if (!candidate.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase)
                            || (candidate.Status == CandidateStatus.Available && candidate.Version is null))
                            throw new InvalidOperationException("Invalid candidate metadata.");
                    }
                    catch (Exception exception) when (!token.IsCancellationRequested)
                    {
                        candidate = new(package.PackageId, null, CandidateStatus.Failed,
                            "https://api.nuget.org/v3/index.json", DateTimeOffset.UtcNow,
                            $"Candidate query failed ({exception.GetType().Name}).");
                    }
                    decisions[index] = package with { NuGetCandidate = candidate };
                    var count = Interlocked.Increment(ref completed);
                    reporter.Report(OperationStage.QueryingNuGet, completed: count, total: soupIndexes.Length);
                }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            result = result with { Packages = [.. decisions] };
            reporter.Report(result.CanUpdate ? OperationStage.Ready : OperationStage.Incomplete,
                completed: request.Sources.Length, total: request.Sources.Length, force: true);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            reporter.Report(OperationStage.Cancelled, force: true);
            throw;
        }
        catch
        {
            reporter.Report(OperationStage.Failed, force: true);
            throw;
        }
    }

}