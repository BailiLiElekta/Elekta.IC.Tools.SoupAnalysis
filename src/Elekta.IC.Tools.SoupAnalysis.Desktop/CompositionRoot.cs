using System.IO;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Desktop.Presentation;
using Elekta.IC.Tools.SoupAnalysis.Desktop.Verification;
using Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Elekta.IC.Tools.SoupAnalysis.Desktop;

public sealed record DesktopPaths(string ConfigDirectory, string UserDataDirectory)
{
    public string SettingsPath => Path.Combine(UserDataDirectory, "settings.json");
    public string AuthenticationOverridePath => Path.Combine(UserDataDirectory, "authentication.json");
    public static DesktopPaths Default => new(Path.Combine(AppContext.BaseDirectory, "config"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Elekta", "SoupAnalysis"));
}

public static partial class CompositionRoot
{
    public static void ConfigureServices(IServiceCollection services, bool smokeMode = false)
    {
        var paths = DesktopPaths.Default;
        services.TryAddSingleton(paths);
        services.TryAddSingleton<IConfigurationService>(new ConfigurationService(paths.ConfigDirectory));
        if (smokeMode)
        {
            services.AddSingleton<ISettingsStore, SmokeSettingsStore>();
            services.AddSingleton<SmokePorts>();
            services.AddSingleton<IAccessTokenProvider>(provider => provider.GetRequiredService<SmokePorts>());
            services.AddSingleton<IRepositorySnapshotProvider>(provider => provider.GetRequiredService<SmokePorts>());
            services.AddSingleton<IPackageScanner>(provider => provider.GetRequiredService<SmokePorts>());
            services.AddSingleton<INuGetCandidateProvider>(provider => provider.GetRequiredService<SmokePorts>());
            services.AddSingleton<IExcelReportWriter>(provider => provider.GetRequiredService<SmokePorts>());
            services.AddSingleton<IUpdateExecutor>(provider => provider.GetRequiredService<SmokePorts>());
        }
        else
        {
            services.TryAddSingleton<ISettingsStore>(new SettingsStore(paths.SettingsPath));
            RegisterInfrastructure(services, paths);
        }
        services.TryAddSingleton<OperationGate>();
        services.TryAddSingleton(provider => new ScanWorkflow(provider.GetRequiredService<IRepositorySnapshotProvider>(),
            provider.GetRequiredService<IPackageScanner>(), provider.GetRequiredService<INuGetCandidateProvider>(), provider.GetRequiredService<OperationGate>()));
        services.TryAddSingleton(provider => new ExportReportWorkflow(provider.GetRequiredService<IExcelReportWriter>()));
        services.TryAddSingleton(provider => new UpdateWorkflow(provider.GetRequiredService<IUpdateExecutor>(), provider.GetRequiredService<OperationGate>()));
        services.TryAddSingleton<IUserInteraction, WpfUserInteraction>();
        services.TryAddSingleton(provider => new MainViewModel(provider.GetRequiredService<IConfigurationService>(),
            provider.GetRequiredService<ISettingsStore>(), provider.GetRequiredService<IAccessTokenProvider>(),
            provider.GetRequiredService<ScanWorkflow>(), provider.GetRequiredService<ExportReportWorkflow>(), provider.GetRequiredService<UpdateWorkflow>(),
            provider.GetRequiredService<IUserInteraction>(),
            provider.GetRequiredService<ILogger<MainViewModel>>(), smokeMode));
    }

    static partial void RegisterInfrastructure(IServiceCollection services, DesktopPaths paths);
}