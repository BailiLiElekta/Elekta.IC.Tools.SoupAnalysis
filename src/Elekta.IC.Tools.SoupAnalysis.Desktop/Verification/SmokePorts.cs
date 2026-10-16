using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Domain;

namespace Elekta.IC.Tools.SoupAnalysis.Desktop.Verification;

internal sealed class SmokePorts : IAccessTokenProvider, IRepositorySnapshotProvider, IPackageScanner,
    INuGetCandidateProvider, IExcelReportWriter, IUpdateExecutor
{
    private static InvalidOperationException Blocked() => new("SMOKE TEST: network, login, report export and remote writes are prohibited.");
    public Task<AccessToken> GetTokenAsync(bool allowInteractive, CancellationToken cancellationToken) => Task.FromException<AccessToken>(Blocked());
    public Task SignOutAsync(CancellationToken cancellationToken) => Task.FromException(Blocked());
    public Task<SourceSnapshot> GetSnapshotAsync(RepositorySource source, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) => Task.FromException<SourceSnapshot>(Blocked());
    public PackageScanResult Scan(SourceSnapshot snapshot, AnalysisRules rules, CancellationToken cancellationToken) => throw Blocked();
    public Task<NuGetCandidate> GetCandidateAsync(string packageId, CancellationToken cancellationToken) => Task.FromException<NuGetCandidate>(Blocked());
    public Task WriteAsync(AnalysisResult analysis, string outputPath, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) => Task.FromException(Blocked());
    public Task<UpdateResult> ExecuteAsync(ChangePlan approvedPlan, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) => Task.FromException<UpdateResult>(Blocked());
}

internal sealed class SmokeSettingsStore : ISettingsStore
{
    public Task<UserSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(UserSettings.Default);
    public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken) => Task.FromException(new InvalidOperationException("Smoke tests do not modify user settings."));
}