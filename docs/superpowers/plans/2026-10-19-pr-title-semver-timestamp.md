# PR Title Semver and Timestamp Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every newly created Azure Repos PR a title containing the configured release, the UTC submission timestamp, and a final patch semver marker.

**Architecture:** Validate the loaded platform manifest's `AnalysisFor` value in `GitUpdateExecutor` before any remote update operations. Pass that release name to `AzurePullRequestService`, which formats the title immediately before its PR POST; the existing description and PR reconciliation flow remain unchanged.

**Tech Stack:** C# / .NET 10, Azure Repos REST API, MSTest.

## Global Constraints

- New PR titles use `SOUP update for {analysisFor} {timestamp} + semver: patch`.
- `{analysisFor}` is the loaded platform configuration's `AnalysisFor` value.
- `{timestamp}` is the UTC time when the PR creation request is submitted, formatted as `yyyyMMddHHmmss` using invariant digits.
- ` + semver: patch` is fixed and must remain the final text in the title.
- If `AnalysisFor` is null, empty, or whitespace, stop with an explicit configuration error before any remote update begins; do not substitute a placeholder.
- Apply the title only when creating a new PR. Keep the PR description unchanged and do not modify an existing PR returned during reconciliation.
- Preserve existing PR reconciliation and duplicate-creation protections.

---

### Task 1: Add focused PR title and preflight tests

**Files:**
- Modify: `tests/Elekta.IC.Tools.SoupAnalysis.Tests/GitUpdateTests.cs`

**Interfaces:**
- Consumes: Existing `Fixture`, `Configuration`, and `PrHandler` test helpers.
- Produces: Regression tests for the exact generated title and for rejecting a missing release before a Git push or PR POST.

- [ ] **Step 1: Add imports and title-format test**

Add `using System.Globalization;` and `using System.Text.Json;`, then add this test to `GitUpdateTests`:

```csharp
[TestMethod]
public async Task CreatedPrTitleUsesConfiguredReleaseUtcTimestampAndFinalSemver()
{
    using var fixture = new Fixture();
    fixture.Configuration.AnalysisFor = "IC Next Release";

    var result = await fixture.Execute();

    Assert.AreEqual(UpdateStatus.Completed, result.Status, result.Repositories.Single().Detail);
    using var body = JsonDocument.Parse(fixture.Http.PostBody!);
    var title = body.RootElement.GetProperty("title").GetString()!;
    const string prefix = "SOUP update for IC Next Release ";
    const string suffix = " + semver: patch";
    Assert.IsTrue(title.StartsWith(prefix, StringComparison.Ordinal), title);
    Assert.IsTrue(title.EndsWith(suffix, StringComparison.Ordinal), title);

    var timestamp = title[prefix.Length..^suffix.Length];
    Assert.AreEqual(14, timestamp.Length);
    Assert.IsTrue(DateTime.TryParseExact(timestamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed));
    Assert.AreEqual(DateTimeKind.Utc, parsed.Kind);
}
```

Update the `Configuration` test helper so its `AnalysisFor` is mutable and is included in the manifest it returns:

```csharp
public string? AnalysisFor { get; set; } = "IC Berlin Release";

public Task<LoadedConfiguration> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new LoadedConfiguration(
    new(1, "1", [platform], AnalysisFor), Hash, AnalysisRules.Default, new(1, "", "", "", [])));
```

- [ ] **Step 2: Add the missing-release preflight test**

Add this test:

```csharp
[TestMethod]
public async Task MissingAnalysisForFailsBeforeRemoteUpdate()
{
    using var fixture = new Fixture();
    fixture.Configuration.AnalysisFor = null;

    var result = await fixture.Execute();

    Assert.AreEqual(UpdateStatus.Failed, result.Status);
    StringAssert.Contains(result.Repositories.Single().Detail!, "analysisFor");
    Assert.AreEqual(0, fixture.Http.Posts);
    Assert.IsFalse(fixture.Process.Commands.Any(command => command.Arguments.Contains("push")));
}
```

- [ ] **Step 3: Run the focused tests and confirm they fail for the expected reasons**

Run:

```powershell
dotnet test tests\Elekta.IC.Tools.SoupAnalysis.Tests\Elekta.IC.Tools.SoupAnalysis.Tests.csproj --filter "FullyQualifiedName~GitUpdateTests"
```

Expected before implementation: the title test sees the old fixed title, and the missing-release test observes the current execution does not reject the configuration during preflight.

### Task 2: Implement release-aware PR title generation and document it

**Files:**
- Modify: `src/Elekta.IC.Tools.SoupAnalysis.Infrastructure/Git/GitUpdateExecutor.cs`
- Modify: `src/Elekta.IC.Tools.SoupAnalysis.Infrastructure/Git/AzurePullRequestService.cs`
- Modify: `tests/Elekta.IC.Tools.SoupAnalysis.Tests/GitUpdateTests.cs`
- Modify: `README.md`

**Interfaces:**
- Consumes: `LoadedConfiguration.Manifest.AnalysisFor` and the Task 1 tests.
- Produces: Required nonblank release preflight validation; release passed to `AzurePullRequestService`; PR title generated at POST time in the exact required format.

- [ ] **Step 1: Reject a missing release during executor preflight**

In `GitUpdateExecutor.ExecuteAsync`, immediately after `configuration.LoadAsync` and before `ValidatePlan` or snapshot fetches, validate the release:

```csharp
var config = await configuration.LoadAsync(cancellationToken).ConfigureAwait(false);
if (string.IsNullOrWhiteSpace(config.Manifest.AnalysisFor))
    throw new MissingAnalysisForException();
ValidatePlan(approvedPlan, config);
```

Add this private exception inside `GitUpdateExecutor`:

```csharp
private sealed class MissingAnalysisForException : InvalidOperationException
{
    public MissingAnalysisForException()
        : base("The platform configuration must specify analysisFor before updating.")
    {
    }
}
```

In the existing outer catch, change only the preflight detail expression so this specific error is visible and generic error reporting remains unchanged:

```csharp
var detail = cancelled ? "Cancelled before repository updates."
    : exception is MissingAnalysisForException missingAnalysisFor
        ? "Preflight rejected: " + missingAnalysisFor.Message
        : "Preflight rejected: " + exception.GetType().Name
            + ". Recheck approval, manifest, baseline refs, Git installation and identity.";
```

Retain the non-null release value and pass it into `CreateOrFindDetailedAsync` at the existing call site. This validation must precede baseline reads and all Git update operations.

- [ ] **Step 2: Generate the title only for a new PR POST**

In `AzurePullRequestService.cs`, add `using System.Globalization;`. Add a required `string release` argument to `CreateOrFindAsync` and `CreateOrFindDetailedAsync`, and forward it through the wrapper and the `GitUpdateExecutor` call.

Keep the existing PR lookup and early return unchanged. After lookup confirms no PR exists and before serializing the POST body, construct:

```csharp
var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
var title = $"SOUP update for {release} {timestamp} + semver: patch";
```

Use `title` in the serialized request instead of `"SOUP alignment: approved package versions"`. Do not change the serialized `description`.

- [ ] **Step 3: Run both focused tests and confirm they pass**

Run the Task 1 `dotnet test` command again.

Expected: both new tests pass, including an exact 14-digit UTC timestamp and ` + semver: patch` at the end of the title.

- [ ] **Step 4: Document the generated title and missing-release behavior**

In the `README.md` PR Failure Recovery section, add a concise paragraph documenting:

```text
Newly created PR titles use `SOUP update for {analysisFor} {yyyyMMddHHmmss UTC} + semver: patch`. If `analysisFor` is unset, execution stops during preflight before remote updates. Existing PR reconciliation does not alter an existing title.
```

- [ ] **Step 5: Run the full test project and inspect the final diff**

Run:

```powershell
dotnet test tests\Elekta.IC.Tools.SoupAnalysis.Tests\Elekta.IC.Tools.SoupAnalysis.Tests.csproj
```

Expected: the test project passes. Then run `git diff --check` and inspect the implementation diff to confirm its changes are limited to the four files listed in this task and that the PR description, existing-PR reconciliation, and retry behavior are unchanged. The worktree currently has a separate, pre-existing addition of `System.Security.Cryptography.Pkcs` in `src/Elekta.IC.Tools.SoupAnalysis.Infrastructure/Elekta.IC.Tools.SoupAnalysis.Infrastructure.csproj`; do not edit, stage, or revert that unrelated change.

- [ ] **Step 6: Commit the implementation**

```powershell
git add -- src\Elekta.IC.Tools.SoupAnalysis.Infrastructure\Git\GitUpdateExecutor.cs src\Elekta.IC.Tools.SoupAnalysis.Infrastructure\Git\AzurePullRequestService.cs tests\Elekta.IC.Tools.SoupAnalysis.Tests\GitUpdateTests.cs README.md
git commit -m "feat: add semver and timestamp to PR titles" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```
