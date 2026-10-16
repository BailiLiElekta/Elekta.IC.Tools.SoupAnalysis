using Elekta.IC.Tools.SoupAnalysis.Domain;
using NuGet.Versioning;

namespace Elekta.IC.Tools.SoupAnalysis.Application;

public interface IAccessTokenProvider
{
    Task<AccessToken> GetTokenAsync(bool allowInteractive, CancellationToken cancellationToken);
    Task SignOutAsync(CancellationToken cancellationToken);
}

public interface IRepositorySnapshotProvider
{
    Task<SourceSnapshot> GetSnapshotAsync(RepositorySource source,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
}

public interface IPackageScanner
{
    PackageScanResult Scan(SourceSnapshot snapshot, AnalysisRules rules, CancellationToken cancellationToken);
}

public interface INuGetCandidateProvider
{
    Task<NuGetCandidate> GetCandidateAsync(string packageId, CancellationToken cancellationToken);
    Task<NuGetCandidate> GetCandidateAsync(string packageId, NuGetVersion? minimumVersion, CancellationToken cancellationToken)
        => GetCandidateAsync(packageId, cancellationToken);
}

public interface IExcelReportWriter
{
    Task WriteAsync(AnalysisResult analysis, string outputPath,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
    Task WriteAsync(AnalysisResult analysis, string outputPath,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken, ReportSelection? selection) => selection is null
        ? WriteAsync(analysis, outputPath, progress, cancellationToken)
        : Task.FromException(new NotSupportedException("This report writer does not support selection snapshots."));
}

public interface ISettingsStore
{
    Task<UserSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(UserSettings settings, CancellationToken cancellationToken);
}

public interface IConfigurationService
{
    Task<LoadedConfiguration> LoadAsync(CancellationToken cancellationToken);
}

public interface IUpdateExecutor
{
    Task<UpdateResult> ExecuteAsync(ChangePlan approvedPlan,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
}