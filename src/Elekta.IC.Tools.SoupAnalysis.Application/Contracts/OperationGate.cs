using System.Diagnostics;

namespace Elekta.IC.Tools.SoupAnalysis.Application;

public sealed class OperationGate
{
    private int busy;

    internal IDisposable Enter()
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            throw new InvalidOperationException("A conflicting operation is already running.");
        return new Lease(this);
    }

    private sealed class Lease(OperationGate owner) : IDisposable
    {
        public void Dispose() => Interlocked.Exchange(ref owner.busy, 0);
    }
}

internal sealed class WorkflowProgress(IProgress<OperationProgress>? sink)
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly object sync = new();
    private TimeSpan lastReport = TimeSpan.MinValue;

    public void Report(OperationStage stage, string? repositoryId = null, int completed = 0,
        int? total = null, string? detail = null, bool force = false)
    {
        lock (sync)
        {
            var now = elapsed.Elapsed;
            if (!force && lastReport != TimeSpan.MinValue && now - lastReport < TimeSpan.FromMilliseconds(100))
                return;
            lastReport = now;
            sink?.Report(new(stage, repositoryId, completed, total, now, detail));
        }
    }

    public IProgress<OperationProgress> ForRepository(string repositoryId) => new RepositoryProgress(this, repositoryId);

    private sealed class RepositoryProgress(WorkflowProgress owner, string repositoryId) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) =>
            owner.Report(value.Stage, repositoryId, value.Completed, value.Total, value.Detail);
    }
}