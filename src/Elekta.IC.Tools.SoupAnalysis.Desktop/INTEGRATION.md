# Desktop Integration

The Desktop project and all WPF screens compile. Native smoke mode is independently runnable with real manifest loading and network/write guard ports. Normal startup requires the completed infrastructure adapters to be registered; it does not substitute fake functional adapters when a service is missing.

## Main-Agent Wiring

Implement the following partial method in another Desktop-owned source file after the parallel adapters are complete:

```csharp
namespace Elekta.IC.Tools.SoupAnalysis.Desktop;

public static partial class CompositionRoot
{
    static partial void RegisterInfrastructure(
        Microsoft.Extensions.DependencyInjection.IServiceCollection services,
        DesktopPaths paths)
    {
        // Register the six real acquisition/report/update ports listed below.
    }
}
```

Required real adapter registrations:

- `IAccessTokenProvider`: register `ReloadingAccessTokenProvider`, using the already-registered `IConfigurationService` and a factory creating `MicrosoftAccountLogin(authentication, Path.Combine(paths.UserDataDirectory, "auth"))`. Do not capture the packaged blank authentication record at startup: the GUI saves a nonsecret per-user override and the wrapper reloads it on the next login.
- `IRepositorySnapshotProvider`: `AzureReposClient`, with an owned `HttpClient`, the same token provider, and a tool-owned immutable cache directory.
- `IPackageScanner`: `PackageReferenceScanner`.
- `INuGetCandidateProvider`: `NuGetCandidateProvider`, with its owned HTTP client.
- `IExcelReportWriter`: `ExcelReportWriter`.
- `IUpdateExecutor`: the parallel update agent's completed, journaling executor.

Configuration, settings, authentication editing, UI interaction, logging, `MainViewModel`, and workflows are already registered. `DesktopConfigurationService` wraps the real `ConfigurationService`; `SettingsStore` handles repository/ref/target settings. Do not replace the effective configuration service with the packaged-only service. Configuration is in the distributed `config` directory; user settings and the nonsecret authentication override are under `%LOCALAPPDATA%/Elekta/SoupAnalysis`. Source configuration files are never edited by the GUI.

A single injected `OperationGate` is shared by `ScanWorkflow` and `UpdateWorkflow`. Do not register replacement workflows with independent default gates. `MainViewModel` requires these six constructor arguments, in order: `IConfigurationService`, `ISettingsStore`, `IAccessTokenProvider`, `ScanWorkflow`, `ExportReportWorkflow`, `UpdateWorkflow`. Its optional arguments are `IUserInteraction`, `IAuthenticationSettingsEditor`, `ILogger<MainViewModel>`, and `smokeMode`. All UI-facing model construction and initialization must occur on the WPF dispatcher.

## Verification

Use the installed executable `C:/Program Files/dotnet/dotnet.exe` to build this project in Release with `--no-restore`. WPF markup compilation is part of this build.

Run the built Desktop DLL with:

```text
--smoke-test <absolute PNG path> --smoke-test-scenario repositories|analysis|changes
```

Only smoke arguments activate design fixtures; normal launch has no sample rows. Repositories always initializes the distributed manifest. Analysis/changes fixtures are visibly marked DESIGN FIXTURE and never create an approved plan. Smoke ports reject login, snapshots, candidates, export and remote writes, and user settings are not saved. The smoke runner also executes dispatcher-driven behavioral checks against isolated fake ports, never a real remote executor.

Each PNG has a `.verification.json` with size, DPI, visible pixels, distinct colors, binding errors, and behavioral check names. Failed runs return exit code 1 and produce `.error.txt`; successful runs remove that stale error marker and return exit code 0. Captures use the native WPF client area, not a browser or a server. Smoke fixtures and the behavioral harness live only in `Verification` and do not require a Desktop reference in the shared test project.

The current checks cover membership/ref restrictions, URL/ref validation, fixed shared targets, candidate outage isolation, separate emulator references, approval versus execution, target/selection/source invalidation, initial busy state, cancellation, previous-analysis retention, partial-analysis export, and bounded log batching. Live MFA, network scanning, Excel file output, journaled PR creation, and physical 150/200% DPI remain integration acceptance gates, not claims established by an offline screenshot.

## Safety And Lifecycle

The view model creates progress sinks on the dispatcher, projects large result lists on a CPU worker, installs them in batches, bounds/throttles logs, virtualizes grids, and keeps navigation, filtering and cancellation available during operations. Settings/ref/target/review edits are disabled during conflicting operations. A new scan retains its previous immutable result separately and marks it stale until replacement succeeds. Configuration is reloaded and compared before both approval and execution; manifest, refs, rules and authentication changes invalidate the plan.

Approval uses `ChangePlanner.Create`, a counts/downgrades/compatibility-warning modal, then `ChangePlan.Approve`; execution is a separate `UpdateWorkflow.ExecuteAsync` command. Selection and target edits invalidate approval. The plan is consumed before execution to prevent duplicate attempts. Update failures without returned repository outcomes require explicit journal-review acknowledgment before the window can close. Active close first asks to cancel and awaits the owned operation. Normal shutdown awaits host shutdown; smoke shutdown bypasses interactive closing and saves no settings.