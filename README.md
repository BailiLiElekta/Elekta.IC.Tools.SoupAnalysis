# Elekta IC SOUP Analysis

A .NET 10 WPF Windows desktop tool for read-only Azure Repos package analysis, Excel reporting, and explicitly approved IC package updates and pull requests. The implementation is isolated from the existing developer repositories. Scanning reads remote snapshots; only approved updates fetch code into a tool-owned local directory.

## Run

The current Windows x64 release is `artifacts/win-x64-pr-repair/Elekta.IC.Tools.SoupAnalysis.Desktop.exe`. It includes stronger action highlighting and PR failure recovery, alongside the unified SOUP application icon, explicit unavailable-target diagnostics, guarded NuGet redirects, the corrected IC bridge classification, Azure CLI login, repository-scoped views, bulk selection and optional Internal custom targets. No separate .NET installation is required for this self-contained build. Keep its DLLs and `config` directory together. Earlier release directories, including `win-x64-icon` and `win-x64-bridge`, retain their older UI and behavior.

The application opens without authentication configuration. Its three work areas are Repositories, Analysis, and Changes. It uses the supplied screenshot's light gray/cyan styling, not its layout. Long operations show stage, elapsed time, progress where known, cancellation, and a bounded operation log. Package tables are virtualized.

The application uses a cyan software-package cube and magnifying-glass logo for SOUP dependency analysis. The executable, taskbar and window title bars share a multi-resolution icon (16, 20, 24, 32, 40, 48, 64, 128 and 256 pixels); the header uses its scalable vector source. To regenerate the checked-in icon after editing [SoupAnalysis.xaml](src/Elekta.IC.Tools.SoupAnalysis.Desktop/Assets/SoupAnalysis.xaml), run `powershell.exe -NoProfile -STA -File .\src\Elekta.IC.Tools.SoupAnalysis.Desktop\Assets\Generate-Icon.ps1` on Windows, then rebuild. Native smoke verification checks the header, window icons and all nine embedded sizes.

1. Install Azure CLI, or use the existing installation, and click Sign In. The application first reuses your current CLI login; if a login or renewed MFA is needed, it opens a visible login window using `az login --allow-no-subscriptions`. Use your own company Microsoft account. No Tenant ID, Client ID, Redirect URI, application registration, client secret, or password field is required by this tool. Company access policies and repository permissions still apply.
2. Configure Platform branch/tag/commit references. Platform membership is read-only in the GUI. Add IC or IC Emulator repository URLs and branches; known names infer the correct category and unknown names require a category.
3. Scan repositories. The tool pins each source to a commit, scans direct references in `src`, and displays complete/provisional status and issues. It never runs target repository MSBuild, restore, build, or tests.
4. In Analysis, choose a Repository. The first IC repository is selected by default. The package table shows that repository's current versions, source/ownership, required action, TEST Only status and project count. `Platform shared` means the package is referenced by Platform and must align to PlatformMax; `IC SOUP` means IC owns alignment; `Internal` is excluded from SOUP alignment. Upgrade/Downgrade/Mixed identify needed changes; Upgrade has a distinct blue background and AwaitingSelection has an amber background to highlight that an IC SOUP target must first be chosen. These action colors also apply in Changes and remain visible when selected. TEST Only applies exclusively to the selected repository; Unconfirmed means the inventory cannot establish it reliably.
5. Use ownership/action/search filters within the selected repository. TEST Only is a result column between Source / Ownership and Current Versions; there is no TEST Only checkbox filter. Switching Repository clears display filters and the previous package detail, then shows all known packages for the new repository. References, local provenance and issues follow the same repository scope. Failed/empty repositories remain selectable, with an explicit scan/completeness summary rather than a false success. `All repositories (global summary)` restores the optional aggregated view.
6. Platform shared targets are locked to PlatformMax, and their ICMax/NuGetMax are not applicable. IC SOUP can choose ICMax or NuGetMax: ICMax reflects the actual highest IC reference, including prerelease; NuGetMax is the highest publicly listed formal/stable release, excluding all prereleases. Internal packages have no forced alignment or ICMax/NuGetMax; optionally enter any valid exact custom NuGet version, including a lower or prerelease version. Empty input keeps Internal unchanged; invalid input clears the previous target and approval. These targets remain shared per package across selected IC repositories, independently of the viewed repo. Browsing does not alter approval, but changing a target does. Platform and Emulator views remain read-only.
7. Export Excel, including partial results and explicit issues. Export always covers the full scanned analysis, independently of the display repository/filter, and includes current target selections and reviewed approval.
8. In Changes, choose one IC repository by name. Its packages and recorded outcomes are the only rows displayed. The former repository checkboxes are removed: this repository selector determines the current review/execution scope, while each package checkbox determines what to change. Select Upgrades replaces the current repository's selections with eligible Upgrade rows only, excluding Downgrade and Mixed actions. Select All checks every eligible row with a valid target and safe resolved declaration, including aligned no-op rows; the planner skips unchanged usages. Missing/invalid targets and unsafe/unresolved rows are skipped with immediate selected/skipped counts. Clear Selection unchecks the current repository only. These commands never choose targets, approve, push, or create PRs automatically.
9. Click Review and Approve to inspect and confirm the current repository's selected changes. Execute Approved Plan remains a separate command and creates a PR targeting that repository's configured branch. Package selections in other repositories are retained for later viewing but never enter the current plan. Switching the Changes repository invalidates the prior approval; browsing another Analysis repository does not. Complete/still-current analysis is required, and selection is locked during active operations. The tool does not merge PRs or bypass branch policies.

## Unavailable Target Choices

A complete displayed repository does not establish the highest version across an incomplete IC scan. Such ICMax values display `(provisional)` and cannot be selected as a confirmed target. If a SOUP has no confirmed ICMax and its formal NuGet query also fails or has no candidate, Target Choice shows `No confirmed target` with the reason, and Action is `Unresolved`, not `AwaitingSelection`.

The global-baseline summary and its tooltip identify incomplete repositories. In Completeness / Issues, enable Show all scan issues to include other repositories' scan and package-metadata issues without changing the displayed repository, targets, or approval. A valid formal NuGet candidate may still be selected while ICMax is provisional, but existing completeness gates continue to block updates until the required inventories are complete.

NuGet metadata follows at most five HTTPS redirects on `api.nuget.org` only. Cross-host redirects (including login/filter gateways), HTTP downgrade, credentials, fragments and loops are rejected; no Azure credentials are sent to NuGet. Error details include numeric HTTP status rather than only `Found`. The original live HTTP 302 was not reproduced: anonymous service-index, registration-index and sampled-page checks returned 200. This release improves safe redirect handling, but does not claim that a corporate-network issue is resolved.

## PR Failure Recovery

After a failed, partial or cancelled execution, inspect the recorded outcomes, then use Review and Approve followed by Execute Approved Plan to explicitly retry. Within the current application session, unchanged review inputs reuse the original plan ID, branch and execution journal instead of creating a new branch. Target/package-selection changes, switching the Changes repository, a new analysis, signing out or configuration invalidation discard this retry identity. Baselines and configuration are still checked before writes; completed execution requires a rescan. Restarting the app does not automatically restore a prior plan: inspect retained work journals and remote branches/PRs before starting a new plan.

The executor reconciles PRs before creating them. A lost response, server failure or timeout may mean a PR was created: if lookup remains empty, the same plan refuses another POST and retains the unknown outcome for investigation. A definite HTTP rejection (400/401/403/404/422/429) can be explicitly retried after correcting its cause. Existing inactive PRs and conflicting evidence remain blocked. HTTP failures include numeric status and, where available, the Azure error type; raw response messages and credentials are not included. A failed reconciliation preserves both failures rather than masking the creation error.

PR responses may omit the optional web link. After validating the returned PR ID, source/target refs and status, the tool derives that link from the already validated repository URL; an explicitly invalid web URL is still rejected. Execution-journal replacement now reuses the report writer's bounded Windows file-lock retry policy (up to five attempts, 1.5 seconds total backoff), without retrying Git pushes or PR POSTs. Persistent journal failures remain visible with the actual remote evidence.

These corrections are verified with offline HTTP fixtures, temporary journal locks and local Git tests. They do not establish which failure occurred in an earlier live run; Azure permissions, authentication, service availability and moved baselines can still reject execution.

## Configuration And Safety

The main window displays `Analysis For` and `Platform Config Version` on every tab. In `config/platform-repositories.json`, `version` is the configuration content revision (currently `"2"`), while `schemaVersion` identifies the JSON format and remains `1`. `analysisFor` identifies the release being analyzed (currently `"IC Berlin Release"`); older manifests without it display `Not specified`.

For the next release, update `analysisFor`, increment `version`, and set each Platform repository's `refKind` and `ref` to the required branch, tag, or commit. Edit the `config` file beside the executable you actually run; changing the source configuration does not update previously published copies. Configuration is reloaded at startup and before each scan, not watched live. The refreshed repository list and header reflect the loaded configuration. Saved/manual Platform refs apply only to the same `version` and `analysisFor`; if either changes, JSON refs replace the previous overrides. Legacy settings without a configuration identity also adopt JSON refs on first use. IC/Emulator repositories and other personal settings are preserved. To replace manual refs, change the configuration version even if the release name stays the same. Starting a new scan invalidates previous approval, including when reloading or saving configuration fails.

`config/platform-repositories.json` version 2 contains 11 Platform members. `workflow-management-task-management-bridge` is IC, not Platform, and is excluded from this fixed list and the Platform maximum. Add its Azure URL to your personal IC list and choose a branch if you want to scan it; classification is automatic and case-insensitive. Stale Platform ref overrides for the removed member are ignored. Rescan after adopting this release because ownership and version baselines may change. Maintainers may add/remove Platform members or change defaults in the manifest without changing code; distribute the updated file with the application. The GUI cannot alter Platform membership. SDK repositories are excluded.

`config/analysis-rules.json` contains naming-only test patterns (`Test.*`, `*.Test.Unit`, `*.UnitTests`) and scan concurrency. A package is TEST Only within a repository when all its direct usages are test-named; it still participates in maxima and updates. No test SDK or mocking package determines test classification.

Azure CLI manages the shared Microsoft login and its credential store. The tool requests the Azure DevOps resource (`499b84ac-1321-427f-aa17-267ca6975798`) and keeps its access-token cache only in memory. Disconnect clears this tool's cache and prevents silent reuse until you explicitly reconnect; it does not run `az logout`, clear CLI accounts, or change global CLI settings. Subscription-selector/browser settings are applied only to child processes. Background acquisition is cancellable and CLI output is never put in operation logs. The legacy `config/authentication.json` schema is retained for configuration compatibility, but its registration fields and older per-user registration overrides are not used by default CLI authentication.

User settings, immutable snapshot cache, and isolated update work/journals live under `%LOCALAPPDATA%/Elekta/SoupAnalysis`. Settings are atomically saved with backup recovery; approval is never restored as executable consent after restart. Users need read access for scans and branch-create/contribute/PR permissions for updates. If CLI has no active login and you start a scan directly, the tool reports that login is required instead of opening authentication prompts in the background.

Use a current Azure CLI release for company MFA and Conditional Access compatibility. The developer workstation currently has the old 2.7.0 release: its process entry point was checked, but its live company-account login was not. If login fails, update CLI using Microsoft's supported installation instructions. This application does not install CLI or bypass your organization's policies.

Malformed or mismatched repository identities, unresolved versions/imports, and incomplete Platform or normal IC inventories block automatic updates. Source/configuration changes invalidate approval. Shared XML version declarations are edited only when all affected consumers are approved; otherwise the change is blocked. Existing developer checkouts, Platform sources, and Emulator sources are not modified. Retries reconcile the same plan's remote branch/PR evidence; failed or unknown remote outcomes remain visible in the execution journal.

The report and PR description do not assert compatibility. Target code is not restored, built, or tested; users must resolve resulting compatibility problems through their normal review/build process. Floating/range versions and indeterminate MSBuild logic are reported rather than guessed. Private NuGet feeds and transitive dependencies are outside the current scope.

## Architecture

Dependency direction: Desktop -> Application + Infrastructure; Infrastructure -> Application + Domain; Application -> Domain. Domain has no IO. Application coordinates narrow ports without CQRS, a service locator, or a framework-specific presentation model. Public models are immutable records with immutable collections and WPF-friendly properties. Both namespaces are `Elekta.IC.Tools.SoupAnalysis.Domain` and `Elekta.IC.Tools.SoupAnalysis.Application`.

Policy is conservative: PlatformMax uses only Platform usages, ICMax only normal IC usages, and emulator inventories remain separate. Case-insensitive package IDs and NuGet release precedence include prerelease versions and ignore build metadata for alignment. Raw declarations remain intact. A repository/package is TEST Only only when every usage is test-named; versions do not split that reduction. Ranges, floating versions, unresolved issues, missing snapshots, and missing manifest members prevent authoritative updates. Provisional maxima remain exportable. Emulator failures do not invalidate normal baselines. Incomplete Platform absence cannot establish SOUP ownership.

Shared packages are locked to PlatformMax, including internal/test-only shared usages. Absent-from-Platform `elekta.*` packages remain Internal, not SOUP; only an explicit Custom target can request their modification. Other absent packages are SOUP. Candidates do not automatically select a target. Each selected package has exactly one target source/version across selected IC repositories. NuGet failure or a prerelease-only listing disables only the formal NuGet candidate, not a complete ICMax choice. Planning excludes no-op usages and rejects unknown selections, non-IC repositories, incomplete baselines, inappropriate target sources and ambiguous declaration owners. Shared/SOUP packages cannot use Custom targets; unchosen Internal packages are not modified.

## Public API Map

These files are the authoritative signatures for adapter and presentation work:

| Contract | Source |
| --- | --- |
| `RepositoryCategory`, `RefKind`, `RepositorySource(Id, Name, Url, Category, RefKind, Ref)`, `SnapshotFile(Path, Content, Sha256)`, `SourceSnapshot(Source, Commit, Files, CapturedAt)`, naming classifier | [RepositoryModels.cs](src/Elekta.IC.Tools.SoupAnalysis.Domain/Repositories/RepositoryModels.cs) |
| `PackageUsage(Repository, ProjectPath, PackageId, RawVersion, TestClassification, Declaration, Condition, AssemblyName)`, computed exact `Version`, `XmlOwner`, `DeclarationProvenance`, naming-only test classifier | [PackageModels.cs](src/Elekta.IC.Tools.SoupAnalysis.Domain/Packages/PackageModels.cs) |
| `RepositoryInventory(Source, Snapshot, Usages, Issues, Status)`, `InventoryIssue`, `NuGetCandidate`, `RepositoryPackageDecision`, `PackageDecision(PackageId, Ownership, PlatformMax, ICMax, NuGetCandidate, IsPlatformMaxComplete, IsICMaxComplete, Repositories)`, `AnalysisResult(Id, ScannedAt, ManifestVersion, ManifestHash, OriginalRefs, Inventories, Packages, CompletePlatform, CompleteIC)` and `CanUpdate` | [AnalysisModels.cs](src/Elekta.IC.Tools.SoupAnalysis.Domain/Analysis/AnalysisModels.cs) |
| `AlignmentAnalyzer.Analyze(analysisId, manifest, manifestHash, sources, inventories, scannedAt, cancellationToken)` and `GetAction(usage, target)` | [AlignmentAnalyzer.cs](src/Elekta.IC.Tools.SoupAnalysis.Domain/Analysis/AlignmentAnalyzer.cs) |
| Manifest, rules, auth, user settings, Platform ref overrides, window layout, candidate preferences, `TargetSource` | [ConfigurationModels.cs](src/Elekta.IC.Tools.SoupAnalysis.Domain/Configuration/ConfigurationModels.cs) |
| Selection/change/approval/result models; `ChangePlan` exposes `Id`, `AnalysisId`, `ManifestHash`, `OriginalRefs`, `BaselineCommits`, `Changes`, `Status`, `Approval`, `CanExecute`, `Matches(analysis)` and `Approve(currentAnalysis, approvedBy, approvedAt)` | [ChangeModels.cs](src/Elekta.IC.Tools.SoupAnalysis.Domain/Analysis/ChangeModels.cs) |
| All eight IO interfaces: `IAccessTokenProvider`, `IRepositorySnapshotProvider`, `IPackageScanner`, `INuGetCandidateProvider`, `IExcelReportWriter`, `ISettingsStore`, `IConfigurationService`, `IUpdateExecutor` | [Ports.cs](src/Elekta.IC.Tools.SoupAnalysis.Application/Contracts/Ports.cs) |
| Immutable report target/review snapshot | [ReportSelection.cs](src/Elekta.IC.Tools.SoupAnalysis.Application/Contracts/ReportSelection.cs) |
| `OperationStage`, `OperationProgress(Stage, RepositoryId, Completed, Total, Elapsed, Detail)`, `ScanRequest`, `PackageScanResult`, redacted `AccessToken` | [OperationModels.cs](src/Elekta.IC.Tools.SoupAnalysis.Application/Contracts/OperationModels.cs) |
| Shared conflicting-operation gate; internal throttled, thread-safe progress relay | [OperationGate.cs](src/Elekta.IC.Tools.SoupAnalysis.Application/Contracts/OperationGate.cs) |
| `ConfigurationPolicy.CreateScanRequest(configuration, settings)` and `ValidateScanRequest(request)` | [ConfigurationPolicy.cs](src/Elekta.IC.Tools.SoupAnalysis.Application/Features/Configuration/ConfigurationPolicy.cs) |
| `ScanWorkflow(snapshots, scanner, candidates, operations).ScanAsync(request, progress, cancellationToken)` | [ScanWorkflow.cs](src/Elekta.IC.Tools.SoupAnalysis.Application/Features/Scanning/ScanWorkflow.cs) |
| `ChangePlanner.Create(analysis, selections, targets)` | [ChangePlanner.cs](src/Elekta.IC.Tools.SoupAnalysis.Application/Features/Updates/ChangePlanner.cs) |
| `UpdateWorkflow(executor, operations).ExecuteAsync(plan, currentAnalysis, progress, cancellationToken)` | [UpdateWorkflow.cs](src/Elekta.IC.Tools.SoupAnalysis.Application/Features/Updates/UpdateWorkflow.cs) |
| `ExportReportWorkflow(writer).ExportAsync(analysis, outputPath, progress, cancellationToken)` | [ExportReportWorkflow.cs](src/Elekta.IC.Tools.SoupAnalysis.Application/Features/Reporting/ExportReportWorkflow.cs) |

`ChangePlan` cannot be publicly constructed or have its changes/approval assigned. `ChangePlanner` creates an unapproved plan; `Approve` returns a new approved instance without mutating the reviewed plan. Approval binds the analysis ID, exact manifest hash, configured refs, and all Platform/normal-IC resolved commits. Empty plans have `NoChanges` status and cannot execute. Approval is deliberately absent from user settings and must never be restored from disk.

Repository/package summary actions may be `Mixed` when some usages need upgrades and others downgrades; the change plan always carries the exact per-usage action. `Unknown` test classification is not test-only. `NuGetCandidate.Version` and provisional maxima can be null; consumers must use completeness/status properties, not infer validity from a nonnull maximum. All immutable arrays must be initialized, using `[]` rather than `default`.

## Adapter Handoff Requirements

- Snapshot provider: resolve the configured ref once, then read only that full 40-character commit. Support both approved Azure URL forms and branch/tag/full-commit refs; normal IC/emulator are branch-only. `Id` is a stable configuration identity, not necessarily an Azure GUID. Return immutable repository-relative file paths, content bytes and SHA-256 hashes, including required ancestor props/imports. Do not perform target restore/build/test or download packages. Cache by repository identity + commit + path, never a stale branch tip. Progress from this port should use resolving/reading stages and must not announce overall completion.
- Scanner: `Scan` is synchronous CPU work, invoked off-thread by the workflow. Observe cancellation. Inventory only direct usages of csproj projects under `src`; exclude generated/worktree directories and transitive packages. Parse XML with DTD/external entities disabled. Support inherited refs, imports, CPM, VersionOverride and deterministic properties. Do not infer test status from SDK flags. Call `TestOnlyClassifier` using the filename stem and statically known AssemblyName. Preserve all conditions/alternatives and raw resolved version strings; uncertain presence/version must produce blocking issues rather than omissions. Unused central declarations are not usages.
- Provenance: `ReferenceOwner` locates the direct reference, `VersionOwner` locates the authoritative value to edit. Each `XmlOwner` has a source file, stable indexed XML locator (`ElementPath`) and optional line number. `Kind`, `PropertyName`, `CentralFile`, `IsUniquelyOwned` distinguish inline/property/central/override/inherited/unresolved ownership. Scanner and editor must agree on locator semantics; maintain namespaces and never guess a node by string matching.
- Candidate provider: query only public nuget.org registration metadata, including SemVer2/prerelease and paging; exclude unlisted versions. Return highest listed version with source, actual query time and status. Never treat a flat-container maximum or stale cache as current listed metadata. No nupkg downloads. Exceptions become visible failed candidates without making IC data incomplete.
- Excel writer: accept the same immutable `AnalysisResult` used by the UI, including partial inventories/issues and separate emulator rows. Implement async file IO and background synchronous ClosedXML generation inside `WriteAsync`, cancellation, formula-safe text and the planned report sheets. The Application export workflow does not hide synchronous writer work in a network-style `Task.Run`.
- Configuration/settings: deserialize string enums with integer values rejected, require supported schemas/initialized collections, reject malformed JSON/duplicate identities, calculate `ManifestHash` from the exact distributed manifest bytes using SHA-256, and use `ConfigurationPolicy` before scanning. Persist only user settings atomically with last-good recovery. Membership is manifest-only; removed-member overrides cannot resurrect repos. Default authentication uses the current user's Azure CLI login, without custom application registration; legacy registration fields/overrides are not a login gate. Never put access tokens in JSON/logs/reports.
- Executor: accept only explicitly approved plans. Re-resolve/recheck all Platform/normal-IC baselines against `BaselineCommits` before editing; reject moved refs/configuration until rescan/reapproval. Work in isolated tool-owned Git snapshots only, targeting each selected IC source's configured branch. Validate authoritative XML ownership and **all collateral consumers** before editing; reject any impact outside the approved changes. Reparse/diff to prove minimal edits. No Platform/emulator updates, direct target pushes, force pushes, auto-merge, target build/test/restore or modification of developer checkouts. Journal actual branch/commit/PR outcomes, including `PushedOnly`, partial failures and cancellation after side effects. Do not promise rollback or lose journals through an exception.
- Desktop composition: wire a single shared `OperationGate` into scan and update workflows; create progress sinks on the dispatcher and retain prior results separately from an active operation. Parsing/comparison are already off-thread. Reports are throttled to at most one every 100 ms apart from explicit stage boundaries/terminal states. Render initial busy state, use async commands, virtualize tables, and never update WPF collections from workers. Exports can use immutable previous results while scan/update conflicts are rejected. Auth readiness is separate from launching/inspecting settings.

## Configuration

[platform-repositories.json](config/platform-repositories.json) contains exactly the approved 12 Platform members, including bridge and logging.switcher `main`, with no SDK repositories. [analysis-rules.json](config/analysis-rules.json) defaults to `Test.*`, `*.Test.Unit`, `*.UnitTests` and concurrency 4 (supported range 1-32). [authentication.json](config/authentication.json) retains the legacy empty registration fields for schema compatibility; no values need to be entered for the default Azure CLI flow. Remote refs and access permissions have not been verified.

## Verified Pins

All versions were queried from the public NuGet version indexes during implementation, not guessed from neighboring repositories.

| Dependency | Version | Project |
| --- | --- | --- |
| .NET SDK | 10.0.303 | global SDK pin; .NET 10 native MTP runner |
| NuGet.Versioning | 7.9.0 | Domain |
| MSTest.Sdk | 4.5.1 | Tests SDK, MTP |
| NuGet.Protocol | 7.9.0 | Infrastructure |
| Microsoft.Identity.Client | 4.90.1 | Infrastructure |
| Microsoft.Identity.Client.Extensions.Msal | 4.90.1 | Infrastructure |
| ClosedXML | 0.105.1 | Infrastructure |
| CommunityToolkit.Mvvm | 8.4.2 | Desktop |
| Microsoft.Extensions.Hosting | 10.0.12 | Desktop |

## Validation

Run from the tool root with the installed executable (PATH is not configured on this machine):

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test --project tests/Elekta.IC.Tools.SoupAnalysis.Tests/Elekta.IC.Tools.SoupAnalysis.Tests.csproj --configuration Release
```

Focused MTP selection uses `--filter 'FullyQualifiedName~DomainRulesTests'` (no `--` separator on .NET 10). Build the solution with `dotnet build Elekta.IC.Tools.SoupAnalysis.slnx -c Release`; run the complete suite with `dotnet test --solution Elekta.IC.Tools.SoupAnalysis.slnx -c Release`. Tests build the actual layers and exercise parsing, metadata, report selections, XML edits, PR reconciliation, and local disposable Git repositories, not private remote writes.

Earlier development restores encountered a NuGet audit metadata outage and used a command-line-only audit override for those checks. Auditing is not disabled in the projects; the initial publish restored without that override. Build success is not a claim of a separate security audit. Live company-account authentication, private repository refs/permissions, and an actual remote PR remain unverified until you complete CLI login and perform approved integration checks. No IT registration for this application is needed.

Native offline UI checks run the executable with `--smoke-test <absolute PNG path> --smoke-test-scenario repositories|analysis|changes`. They exercise production DI resolution without logging in, view-model behavior, binding errors and nonblank pixel checks. Analysis/Changes fixtures are visibly labeled and exist only in smoke mode. Screenshots and JSON evidence are under `artifacts`; regular startup never injects fixture data.

Publish with `dotnet publish src/Elekta.IC.Tools.SoupAnalysis.Desktop/Elekta.IC.Tools.SoupAnalysis.Desktop.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64-target-status`. All packaged JSON configuration is copied beside the executable. The available verification covers the current workstation's DPI; multi-monitor/high-DPI acceptance still requires checking on the intended deployment machines.

Target-availability correction: 84 focused tests passed for presentation, repository issue visibility, and NuGet metadata/redirect security. Native checks reproduce a complete local IC repository with an incomplete global IC baseline and failed metadata, without contacting private repositories. Provisional targets and update permissions were not relaxed.

Bridge classification correction: 56 focused domain/configuration/settings tests passed, including automatic IC classification, case-insensitivity, branch-only restrictions, and removal from the Platform scope despite old saved overrides. Existing IC user selections are not changed or automatically expanded.

Current focused verification: 161 tests passed, covering current-repository change selection, presentation, repository views, target planning, formal NuGet metadata, settings, Excel and approval provenance. Native checks include repo selection, bulk commands and selection feedback, hidden-selection exclusion, approval invalidation, rendered cell padding across views/logs, TEST Only column order and checkbox removal, and valid/invalid Custom input bindings. Extra unselected report targets do not discard an approved plan, but remain unapproved themselves. No private calls or Git execution were performed.

Earlier CLI change verification: 110 authentication tests and 25 native desktop checks passed without acquiring real credentials. Git retry regressions intermittently failed during earlier full runs and then passed in isolated/combined diagnosis. The cause is not established; the current focused checks do not claim to fix that pre-existing test instability. Live CLI MFA and private Azure access still require user testing.

## Foundation Files

The initial contracts and project structure are listed below. Concrete adapters, WPF views/themes, native verification, and focused regression tests extend these feature directories. Generated `bin`, `obj`, screenshots and release outputs are not source files.

```text
.gitignore
global.json
Directory.Build.props
Elekta.IC.Tools.SoupAnalysis.slnx
README.md
config/
  platform-repositories.json
  analysis-rules.json
  authentication.json
src/Elekta.IC.Tools.SoupAnalysis.Domain/
  Elekta.IC.Tools.SoupAnalysis.Domain.csproj
  Repositories/RepositoryModels.cs
  Packages/PackageModels.cs
  Configuration/ConfigurationModels.cs
  Analysis/AnalysisModels.cs
  Analysis/AlignmentAnalyzer.cs
  Analysis/ChangeModels.cs
  Properties/AssemblyInfo.cs
src/Elekta.IC.Tools.SoupAnalysis.Application/
  Elekta.IC.Tools.SoupAnalysis.Application.csproj
  Contracts/Ports.cs
  Contracts/OperationModels.cs
  Contracts/OperationGate.cs
  Features/Configuration/ConfigurationPolicy.cs
  Features/Scanning/ScanWorkflow.cs
  Features/Reporting/ExportReportWorkflow.cs
  Features/Updates/ChangePlanner.cs
  Features/Updates/UpdateWorkflow.cs
src/Elekta.IC.Tools.SoupAnalysis.Infrastructure/
  Elekta.IC.Tools.SoupAnalysis.Infrastructure.csproj
src/Elekta.IC.Tools.SoupAnalysis.Desktop/
  Elekta.IC.Tools.SoupAnalysis.Desktop.csproj
tests/Elekta.IC.Tools.SoupAnalysis.Tests/
  Elekta.IC.Tools.SoupAnalysis.Tests.csproj
  TestData.cs
  DomainRulesTests.cs
  WorkflowTests.cs
  ConfigurationTests.cs
```