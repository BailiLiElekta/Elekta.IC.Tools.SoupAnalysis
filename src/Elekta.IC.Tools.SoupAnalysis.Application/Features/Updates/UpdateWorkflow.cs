using Elekta.IC.Tools.SoupAnalysis.Domain;

namespace Elekta.IC.Tools.SoupAnalysis.Application;

public sealed class UpdateWorkflow(IUpdateExecutor executor, OperationGate? operations = null)
{
    private readonly OperationGate gate = operations ?? new();

    public async Task<UpdateResult> ExecuteAsync(ChangePlan plan, AnalysisResult currentAnalysis,
        IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var operation = gate.Enter();
        cancellationToken.ThrowIfCancellationRequested();
        if (!plan.CanExecute || !plan.Matches(currentAnalysis))
            throw new InvalidOperationException("Execution requires explicit approval of the current analysis and baselines.");
        var reporter = new WorkflowProgress(progress);
        reporter.Report(OperationStage.Updating, force: true);
        try
        {
            var result = await executor.ExecuteAsync(plan, progress, cancellationToken).ConfigureAwait(false);
            if (result.PlanId != plan.Id)
                throw new InvalidOperationException("Executor returned a result for a different plan.");
            reporter.Report(result.Status switch
            {
                UpdateStatus.Completed => OperationStage.Completed,
                UpdateStatus.PartiallyFailed => OperationStage.PartiallyFailed,
                UpdateStatus.Cancelled => OperationStage.Cancelled,
                _ => OperationStage.Failed
            }, force: true);
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