using System.IO;
using System.Net;
using System.Net.Http;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Desktop.Presentation;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Authentication;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.AzureDevOps;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Git;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.NuGet;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Packages;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace Elekta.IC.Tools.SoupAnalysis.Desktop;

public static partial class CompositionRoot
{
    static partial void RegisterInfrastructure(IServiceCollection services, DesktopPaths paths)
    {
        services.AddSingleton<InfrastructureHttpClients>();
        services.AddSingleton<IAccessTokenProvider, AzureCliAccessTokenProvider>();
        services.AddSingleton<IRepositorySnapshotProvider>(provider => new AzureReposClient(
            provider.GetRequiredService<InfrastructureHttpClients>().Azure,
            provider.GetRequiredService<IAccessTokenProvider>(),
            cacheDirectory: Path.Combine(paths.UserDataDirectory, "cache")));
        services.AddSingleton<IPackageScanner, PackageReferenceScanner>();
        services.AddSingleton<INuGetCandidateProvider>(provider => new NuGetCandidateProvider(
            provider.GetRequiredService<InfrastructureHttpClients>().NuGet));
        services.AddSingleton<IExcelReportWriter, ExcelReportWriter>();
        services.AddSingleton<IUpdateExecutor>(provider => new GitUpdateExecutor(
            provider.GetRequiredService<IRepositorySnapshotProvider>(),
            provider.GetRequiredService<IPackageScanner>(),
            provider.GetRequiredService<IConfigurationService>(),
            provider.GetRequiredService<IAccessTokenProvider>(),
            provider.GetRequiredService<InfrastructureHttpClients>().PullRequests,
            appDataRoot: paths.UserDataDirectory));
    }

    private sealed class InfrastructureHttpClients : IDisposable
    {
        public HttpClient Azure { get; } = Create();
        public HttpClient NuGet { get; } = Create();
        public HttpClient PullRequests { get; } = Create();

        private static HttpClient Create() => new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = TimeSpan.FromSeconds(90) };

        public void Dispose()
        {
            Azure.Dispose();
            NuGet.Dispose();
            PullRequests.Dispose();
        }
    }
}