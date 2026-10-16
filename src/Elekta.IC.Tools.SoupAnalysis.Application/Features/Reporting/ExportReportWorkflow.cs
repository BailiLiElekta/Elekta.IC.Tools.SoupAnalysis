using Elekta.IC.Tools.SoupAnalysis.Domain;

namespace Elekta.IC.Tools.SoupAnalysis.Application;

public sealed class ExportReportWorkflow(IExcelReportWriter writer)
{
    public async Task ExportAsync(AnalysisResult analysis, string outputPath,
        IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default, ReportSelection? selection = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        var reporter = new WorkflowProgress(progress);
        reporter.Report(OperationStage.Exporting, force: true);
        try
        {
            selection?.Validate(analysis);
            await writer.WriteAsync(analysis, outputPath, progress, cancellationToken, selection).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            reporter.Report(OperationStage.Completed, force: true);
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