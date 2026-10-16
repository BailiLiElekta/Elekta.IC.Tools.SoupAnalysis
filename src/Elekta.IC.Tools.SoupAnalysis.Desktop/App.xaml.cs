using System.Diagnostics;
using System.IO;
using System.Windows;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Desktop.Verification;
using Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elekta.IC.Tools.SoupAnalysis.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? host;
    public static void ConfigureServices(IServiceCollection services) => CompositionRoot.ConfigureServices(services);
    protected override async void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        string? capturePath = null;
        var scenario = "repositories";
        try
        {
            for (var index = 0; index < args.Args.Length; index++)
            {
                switch (args.Args[index])
                {
                    case "--smoke-test" when index + 1 < args.Args.Length: capturePath = Path.GetFullPath(args.Args[++index]); break;
                    case "--smoke-test-scenario" when index + 1 < args.Args.Length: scenario = args.Args[++index]; break;
                    default: throw new ArgumentException("Supported arguments: --smoke-test <PNG path> --smoke-test-scenario repositories|analysis|changes");
                }
            }
            if (scenario is not ("repositories" or "analysis" or "changes") || capturePath is not null && !capturePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a known smoke scenario and a PNG output path.");
            if (capturePath is null && args.Args.Length > 0) throw new ArgumentException("Smoke scenario requires --smoke-test.");
            var smoke = capturePath is not null;
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders();
            builder.Logging.AddDebug();
            CompositionRoot.ConfigureServices(builder.Services, smoke);
            host = builder.Build();
            await host.StartAsync();
            var model = host.Services.GetRequiredService<MainViewModel>();
            var bindings = new BindingErrorListener();
            if (smoke)
            {
                PresentationTraceSources.DataBindingSource.Listeners.Add(bindings);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            }
            var window = new MainWindow { DataContext = model };
            MainWindow = window;
            window.Loaded += async (_, _) =>
            {
                try
                {
                    await model.InitializeCommand.ExecuteAsync(null);
                    if (!model.IsInitialized) throw new InvalidOperationException(model.UserError ?? "Initialization failed.");
                    if (smoke)
                    {
                        var checks = await DesktopViewModelChecks.RunAsync(model, host.Services.GetRequiredService<IConfigurationService>());
                        if (scenario != "repositories")
                        {
                            var fixture = SmokeFixture.Create(await host.Services.GetRequiredService<IConfigurationService>().LoadAsync(CancellationToken.None));
                            await model.SetAnalysisAsync(fixture);
                            foreach (var row in model.Packages.Where(row => row.Decision.IsSoup)) row.SelectedTarget = row.Choices.FirstOrDefault();
                            model.SelectAllPackagesCommand.Execute(null);
                            model.Status = "SMOKE TEST | DESIGN FIXTURES ONLY | No network, export, approval or remote writes";
                            model.AuthenticationStatus = "DESIGN FIXTURES / SMOKE TEST ONLY";
                            model.ReviewStatus = "DESIGN FIXTURES ONLY | Review rows, not an executable plan";
                        }
                        model.SelectedTab = scenario == "analysis" ? 1 : scenario == "changes" ? 2 : 0;
                        await NativeCapture.CaptureAsync(window, capturePath!, scenario, bindings, checks);
                        model.Dispose();
                        await ShutdownHostAsync(0);
                    }
                    else
                    {
                        var layout = model.WindowLayout;
                        window.Width = Math.Clamp(double.IsFinite(layout.Width) ? layout.Width : 1360, window.MinWidth, Math.Max(window.MinWidth, SystemParameters.WorkArea.Width));
                        window.Height = Math.Clamp(double.IsFinite(layout.Height) ? layout.Height : 880, window.MinHeight, Math.Max(window.MinHeight, SystemParameters.WorkArea.Height));
                        if (layout.Maximized) window.WindowState = WindowState.Maximized;
                    }
                }
                catch (Exception exception) { await StartupFailureAsync(exception, capturePath); }
            };
            window.Show();
        }
        catch (Exception exception) { await StartupFailureAsync(exception, capturePath); }
    }
    private async Task StartupFailureAsync(Exception exception, string? capturePath)
    {
        var message = exception is InvalidOperationException && exception.Message.Contains("No service for type", StringComparison.Ordinal)
            ? "Infrastructure composition is not registered. Implement CompositionRoot.RegisterInfrastructure with the real port adapters before normal startup. Smoke mode remains offline and independently runnable."
            : $"{exception.GetType().Name}: {exception.Message}";
        if (capturePath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
            await File.WriteAllTextAsync(capturePath + ".error.txt", message);
        }
        else MessageBox.Show(message, "SOUP Analysis startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
        await ShutdownHostAsync(1);
    }
    public async Task ShutdownHostAsync(int exitCode = 0)
    {
        if (host is not null) { await host.StopAsync(); host.Dispose(); host = null; }
        Shutdown(exitCode);
    }
}