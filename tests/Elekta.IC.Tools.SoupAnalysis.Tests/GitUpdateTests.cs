using System.Net;
using System.Text;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Git;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Packages;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Updates;
using NuGet.Versioning;

namespace Elekta.IC.Tools.SoupAnalysis.Tests;

[TestClass]
public sealed class GitUpdateTests
{
    [TestMethod]
    public async Task TransientJournalLockAfterPushDoesNotPreventPrCreation()
    {
        using var fixture = new Fixture();
        FileStream? locked = null;
        Task? release = null;
        fixture.Process.AfterPush = () =>
        {
            var journal = Directory.GetFiles(Path.Combine(fixture.Root, "work"), "journal.json", SearchOption.AllDirectories).Single();
            locked = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read);
            release = Task.Run(async () =>
            {
                await Task.Delay(200);
                locked.Dispose();
            });
        };
        try
        {
            var result = await fixture.Execute();
            Assert.AreEqual(UpdateStatus.Completed, result.Status, result.Repositories.Single().Detail);
            Assert.AreEqual(1, fixture.Http.Posts);
            Assert.AreEqual(1, fixture.Process.Commands.Count(command => command.Arguments.Contains("push")));
        }
        finally
        {
            if (release is not null) await release;
            locked?.Dispose();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PrResponseWithoutOptionalWebLinkUsesValidatedRepositoryUrl(bool existing)
    {
        using var fixture = new Fixture();
        fixture.Http.Existing = existing;
        fixture.Http.OmitWebLink = true;
        var result = await fixture.Execute();
        Assert.AreEqual(UpdateStatus.Completed, result.Status, result.Repositories.Single().Detail);
        Assert.AreEqual("https://dev.azure.com/example/platform/_git/ic/pullrequest/1", result.Repositories.Single().PullRequestUrl);
        Assert.AreEqual(existing ? 0 : 1, fixture.Http.Posts);
    }

    [TestMethod]
    public async Task RejectedPrCreationPreservesHttpStatusAndServiceErrorWithoutLeakingBody()
    {
        using var fixture = new Fixture();
        fixture.Http.FailPost = true;
        fixture.Http.PostFailureStatus = HttpStatusCode.Forbidden;
        var result = await fixture.Execute();
        var detail = result.Repositories.Single().Detail!;
        Assert.AreEqual(RepositoryUpdateStatus.PushedOnly, result.Repositories.Single().Status);
        StringAssert.Contains(detail, "403");
        StringAssert.Contains(detail, "GitPullRequestAccessDeniedException");
        Assert.IsFalse(detail.Contains("test-secret", StringComparison.Ordinal));
        Assert.AreEqual(1, fixture.Http.Posts);
    }

    [TestMethod]
    public async Task FailedPostReconciliationPreservesBothFailures()
    {
        using var fixture = new Fixture();
        fixture.Http.FailPost = true;
        fixture.Http.FailGetsAfterPost = true;
        var result = await fixture.Execute();
        var detail = result.Repositories.Single().Detail!;
        StringAssert.Contains(detail, "403");
        StringAssert.Contains(detail, "503");
        Assert.AreEqual(1, fixture.Http.Posts);
    }

    [TestMethod]
    public async Task UnknownCreationWithEmptyLookupNeverPostsAgain()
    {
        using var fixture = new Fixture();
        fixture.Http.LosePostResponse = true;
        fixture.Http.FailGetsAfterPost = true;
        Assert.AreEqual(UpdateStatus.PartiallyFailed, (await fixture.Execute()).Status);
        fixture.Http.FailGetsAfterPost = false;
        fixture.Http.Existing = false;
        fixture.Http.LosePostResponse = false;
        var retry = await fixture.Execute();
        Assert.AreEqual(UpdateStatus.PartiallyFailed, retry.Status);
        StringAssert.Contains(retry.Repositories.Single().Detail!, "unknown");
        Assert.AreEqual(1, fixture.Http.Posts, "An empty lookup is not proof that a lost POST never created a PR.");
        fixture.Http.Existing = true;
        Assert.AreEqual(UpdateStatus.Completed, (await fixture.Execute()).Status);
        Assert.AreEqual(1, fixture.Http.Posts);
    }

    [TestMethod]
    public async Task DefiniteRejectionCanBeExplicitlyRetriedWithoutUnknownOutcome()
    {
        using var fixture = new Fixture();
        fixture.Http.FailPost = true;
        var rejected = await fixture.Execute();
        Assert.AreEqual(UpdateStatus.PartiallyFailed, rejected.Status);
        var path = Directory.GetFiles(Path.Combine(fixture.Root, "work"), "journal.json", SearchOption.AllDirectories).Single();
        var recorded = await File.ReadAllTextAsync(path);
        Assert.IsFalse(recorded.Contains("PRUnknown", StringComparison.Ordinal), "A definite 403 rejection is not an unknown write.");
        fixture.Http.FailPost = false;
        Assert.AreEqual(UpdateStatus.Completed, (await fixture.Execute()).Status);
        Assert.AreEqual(2, fixture.Http.Posts);
        Assert.AreEqual(1, fixture.Process.Commands.Count(command => command.Arguments.Contains("push")));
    }

    [TestMethod]
    [DataRow("abandoned")]
    [DataRow("completed")]
    public async Task InactivePrRetryReportsExistingStatusWithoutAnotherPost(string status)
    {
        using var fixture = new Fixture();
        var first = await fixture.Execute();
        Assert.AreEqual(UpdateStatus.Completed, first.Status, first.Repositories.Single().Detail);
        fixture.Http.Existing = true;
        fixture.Http.Status = status;
        var retry = await fixture.Execute();
        var repository = retry.Repositories.Single();
        Assert.AreEqual(UpdateStatus.PartiallyFailed, retry.Status, repository.Detail);
        Assert.AreEqual(RepositoryUpdateStatus.PushedOnly, repository.Status, repository.Detail);
        Assert.AreEqual(1, fixture.Http.Posts, repository.Detail);
        Assert.AreEqual(1, fixture.Process.Commands.Count(command => command.Arguments.Contains("push")));
        Assert.AreEqual(first.Repositories.Single().PullRequestUrl, repository.PullRequestUrl);
        StringAssert.Contains(repository.Detail!, status);
    }

    [TestMethod]
    public async Task LatePrLookupFailurePreservesConfirmedEvidenceAndFutureRecovery()
    {
        using var fixture = new Fixture();
        var first = (await fixture.Execute()).Repositories.Single();
        fixture.Http.Existing = true;
        fixture.Http.FailedGets = 3;
        var failed = await fixture.Execute();
        var repository = failed.Repositories.Single();
        Assert.AreEqual(UpdateStatus.PartiallyFailed, failed.Status);
        Assert.AreEqual(RepositoryUpdateStatus.PushedOnly, repository.Status);
        Assert.AreEqual(first.PullRequestUrl, repository.PullRequestUrl);
        Assert.AreEqual(first.Commit, repository.Commit);
        StringAssert.Contains(repository.Detail!, "previously confirmed");
        StringAssert.Contains(repository.Detail!, "503");
        var path = Directory.GetFiles(Path.Combine(fixture.Root, "work"), "journal.json", SearchOption.AllDirectories).Single();
        var journal = await File.ReadAllTextAsync(path);
        StringAssert.Contains(journal, "PRCreated");
        StringAssert.Contains(journal, first.PullRequestUrl!);
        StringAssert.Contains(journal, first.Commit!);
        var recovered = await fixture.Execute();
        Assert.AreEqual(UpdateStatus.Completed, recovered.Status);
        Assert.AreEqual(first.PullRequestUrl, recovered.Repositories.Single().PullRequestUrl);
        Assert.AreEqual(1, fixture.Http.Posts);
        Assert.AreEqual(1, fixture.Process.Commands.Count(command => command.Arguments.Contains("push")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnknownPrCreationSurvivesFailedReconciliationAndRecoversWithoutAnotherPost(bool cancel)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Http.LosePostResponse = true;
        fixture.Http.FailGetsAfterPost = true;
        if (cancel) fixture.Http.CancelDuringPost = cancellation.Cancel;
        var first = await fixture.Execute(cancellation.Token);
        Assert.AreEqual(cancel ? UpdateStatus.Cancelled : UpdateStatus.PartiallyFailed, first.Status);
        Assert.AreEqual(RepositoryUpdateStatus.PushedOnly, first.Repositories.Single().Status);
        Assert.IsNull(first.Repositories.Single().PullRequestUrl);
        StringAssert.Contains(first.Repositories.Single().Detail!, "possibly created");
        var path = Directory.GetFiles(Path.Combine(fixture.Root, "work"), "journal.json", SearchOption.AllDirectories).Single();
        StringAssert.Contains(await File.ReadAllTextAsync(path), "PRUnknown");
        fixture.Http.FailedGets = 3;
        fixture.Http.FailGetsAfterPost = false;
        Assert.AreEqual(UpdateStatus.PartiallyFailed, (await fixture.Execute()).Status);
        StringAssert.Contains(await File.ReadAllTextAsync(path), "PRUnknown");
        fixture.Http.CancelDuringPost = null;
        var recovered = await fixture.Execute();
        Assert.AreEqual(UpdateStatus.Completed, recovered.Status);
        Assert.IsNotNull(recovered.Repositories.Single().PullRequestUrl);
        Assert.AreEqual(first.Repositories.Single().Branch, recovered.Repositories.Single().Branch);
        Assert.AreEqual(1, fixture.Http.Posts);
        Assert.AreEqual(1, fixture.Process.Commands.Count(command => command.Arguments.Contains("push")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PaginatedPrLookupRequiresOneUniqueMatch(bool matchOnFirstPage)
    {
        using var fixture = new Fixture();
        fixture.Http.Existing = true;
        fixture.Http.FullFirstPage = true;
        fixture.Http.MatchOnFirstPage = matchOnFirstPage;
        Assert.AreEqual(UpdateStatus.Completed, (await fixture.Execute()).Status);
        Assert.AreEqual(0, fixture.Http.Posts);
        Assert.HasCount(2, fixture.Http.GetQueries);
        Assert.IsTrue(fixture.Http.GetQueries.All(query => query.Contains("searchCriteria.status=all", StringComparison.Ordinal)));
        StringAssert.Contains(fixture.Http.GetQueries[1], "$skip=100");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MultiplePrMatchesFailClosedWithoutPosting(bool paginated)
    {
        using var fixture = new Fixture();
        fixture.Http.Existing = true;
        fixture.Http.FullFirstPage = paginated;
        fixture.Http.MatchOnFirstPage = true;
        fixture.Http.DuplicateMatches = true;
        var result = await fixture.Execute();
        Assert.AreEqual(UpdateStatus.PartiallyFailed, result.Status);
        Assert.AreEqual(RepositoryUpdateStatus.PushedOnly, result.Repositories.Single().Status);
        Assert.IsNull(result.Repositories.Single().PullRequestUrl);
        Assert.AreEqual(0, fixture.Http.Posts);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingOrInconsistentKnownPrFailsClosedWithRecordedEvidence(bool inconsistent)
    {
        using var fixture = new Fixture();
        var first = (await fixture.Execute()).Repositories.Single();
        fixture.Http.Existing = inconsistent;
        fixture.Http.PrId = 2;
        var retry = await fixture.Execute();
        Assert.AreEqual(UpdateStatus.PartiallyFailed, retry.Status);
        var repository = retry.Repositories.Single();
        Assert.AreEqual(RepositoryUpdateStatus.PushedOnly, repository.Status);
        Assert.AreEqual(first.PullRequestUrl, repository.PullRequestUrl);
        StringAssert.Contains(repository.Detail!, "previously confirmed");
        var path = Directory.GetFiles(Path.Combine(fixture.Root, "work"), "journal.json", SearchOption.AllDirectories).Single();
        StringAssert.Contains(await File.ReadAllTextAsync(path), first.PullRequestUrl!);
        Assert.AreEqual(1, fixture.Http.Posts);
        Assert.AreEqual(1, fixture.Process.Commands.Count(command => command.Arguments.Contains("push")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedRetryPreparationDoesNotEraseRecoverableRemoteJournal(bool initiallyCompleted)
    {
        using var fixture = new Fixture();
        fixture.Http.FailPost = !initiallyCompleted;
        var first = await fixture.Execute();
        Assert.AreEqual(initiallyCompleted ? UpdateStatus.Completed : UpdateStatus.PartiallyFailed, first.Status);
        var path = Directory.GetFiles(Path.Combine(fixture.Root, "work"), "journal.json", SearchOption.AllDirectories).Single();
        var recorded = await File.ReadAllTextAsync(path);
        fixture.Process.FailFetch = true;
        var failedRetry = await fixture.Execute();
        Assert.AreEqual(UpdateStatus.Failed, failedRetry.Status);
        Assert.AreEqual(RepositoryUpdateStatus.Failed, failedRetry.Repositories.Single().Status);
        Assert.AreEqual(recorded, await File.ReadAllTextAsync(path));
        Assert.AreEqual(1, fixture.Http.Posts);
        fixture.Process.FailFetch = false;
        fixture.Http.FailPost = false;
        fixture.Http.Existing = initiallyCompleted;
        var recovered = await fixture.Execute();
        Assert.AreEqual(UpdateStatus.Completed, recovered.Status);
        Assert.AreEqual(first.Repositories.Single().Branch, recovered.Repositories.Single().Branch);
        Assert.AreEqual(first.Repositories.Single().Commit, recovered.Repositories.Single().Commit);
        Assert.AreEqual(1, fixture.Process.Commands.Count(command => command.Arguments.Contains("push")));
        Assert.AreEqual(initiallyCompleted ? 1 : 2, fixture.Http.Posts);
    }

    [TestMethod]
    public async Task JournalBranchTraversalCannotBeReused()
    {
        using var fixture = new Fixture();
        var result = await fixture.Execute();
        var path = Directory.GetFiles(Path.Combine(fixture.Root, "work"), "journal.json", SearchOption.AllDirectories).Single();
        var text = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, text.Replace(result.Repositories.Single().Branch!, "../../outside", StringComparison.Ordinal));
        Assert.AreEqual(UpdateStatus.Failed, (await fixture.Execute()).Status);
        Assert.AreEqual(1, fixture.Process.Commands.Count(command => command.Arguments.Contains("push")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReentryReusesApprovedPlanBranchCommitAndPr(bool initiallyCompleted)
    {
        using var fixture = new Fixture();
        fixture.Http.FailPost = !initiallyCompleted;
        var first = await fixture.Execute();
        Assert.AreEqual(initiallyCompleted ? UpdateStatus.Completed : UpdateStatus.PartiallyFailed, first.Status, first.Repositories.Single().Detail);
        fixture.Http.FailPost = false;
        fixture.Http.Existing = initiallyCompleted;
        var second = await fixture.Execute();
        Assert.AreEqual(UpdateStatus.Completed, second.Status);
        Assert.AreEqual("soup-align/" + fixture.Approved.Id.ToString("N"), first.Repositories.Single().Branch);
        Assert.AreEqual(first.Repositories.Single().Branch, second.Repositories.Single().Branch);
        Assert.AreEqual(first.Repositories.Single().Commit, second.Repositories.Single().Commit);
        Assert.AreEqual(1, fixture.Process.Commands.Count(command => command.Arguments.Contains("push")));
        Assert.AreEqual(initiallyCompleted ? 1 : 2, fixture.Http.Posts);
        fixture.Snapshots.MovePlatform = true;
        Assert.AreEqual(UpdateStatus.Failed, (await fixture.Execute()).Status);
    }

    [TestMethod]
    public async Task UnknownPushReentryReconcilesRemoteWithoutSecondPush()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Process.AfterPush = () => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); };
        Assert.AreEqual(UpdateStatus.Cancelled, (await fixture.Execute(cancellation.Token)).Status);
        fixture.Process.AfterPush = null;
        var path = Directory.GetFiles(Path.Combine(fixture.Root, "work"), "journal.json", SearchOption.AllDirectories).Single();
        var recorded = await File.ReadAllTextAsync(path);
        StringAssert.Contains(recorded, "PushOutcomeUnknown");
        fixture.Process.FailFetch = true;
        var failedRetry = await fixture.Execute();
        Assert.AreEqual(UpdateStatus.Failed, failedRetry.Status);
        Assert.AreEqual(RepositoryUpdateStatus.Failed, failedRetry.Repositories.Single().Status);
        Assert.AreEqual(recorded, await File.ReadAllTextAsync(path));
        Assert.AreEqual(0, fixture.Http.Posts);
        fixture.Process.FailFetch = false;
        Assert.AreEqual(UpdateStatus.Completed, (await fixture.Execute()).Status);
        Assert.AreEqual(1, fixture.Process.Commands.Count(command => command.Arguments.Contains("push")));
    }

    [TestMethod]
    public async Task TamperedJournalIdentityBlocksReentryBeforeRemoteWrites()
    {
        using var fixture = new Fixture();
        await fixture.Execute();
        var path = Directory.GetFiles(Path.Combine(fixture.Root, "work"), "journal.json", SearchOption.AllDirectories).Single();
        var text = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, text.Replace(fixture.Approved.Id.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal));
        var pushes = fixture.Process.Commands.Count(command => command.Arguments.Contains("push"));
        Assert.AreEqual(UpdateStatus.Failed, (await fixture.Execute()).Status);
        Assert.AreEqual(pushes, fixture.Process.Commands.Count(command => command.Arguments.Contains("push")));
    }

    [TestMethod]
    [DataRow("elekta.platform.iam")]
    [DataRow("elekta.ic.emulator.ois")]
    public async Task ExecutorRejectsDisguisedRestrictedUrlBeforeGit(string actualName)
    {
        using var fixture = new Fixture(icUrl: "https://dev.azure.com/org/project/_git/" + actualName);
        Assert.AreEqual(UpdateStatus.Failed, (await fixture.Execute()).Status);
        Assert.HasCount(0, fixture.Process.Commands);
    }

    [TestMethod]
    [DataRow("samples/B.vbproj")]
    [DataRow("samples/B.fsproj")]
    public async Task SharedCentralConsumerDiscoveredInCheckoutBlocksEditAndPush(string consumer)
    {
        using var fixture = new Fixture(central: true);
        fixture.Process.Consumer = consumer;
        var result = await fixture.Execute();
        Assert.AreEqual(UpdateStatus.Failed, result.Status);
        Assert.IsFalse(fixture.Process.Commands.Any(command => command.Arguments.Contains("commit") || command.Arguments.Contains("push")));
        Assert.AreEqual(0, fixture.Http.Posts);
    }

    [TestMethod]
    public async Task ApprovedUpdateUsesIsolatedBranchEnvironmentSecretAndConfiguredTarget()
    {
        using var fixture = new Fixture();
        var result = await fixture.Execute();
        Assert.AreEqual(UpdateStatus.Completed, result.Status);
        var repository = result.Repositories.Single();
        Assert.AreEqual(RepositoryUpdateStatus.Completed, repository.Status);
        StringAssert.StartsWith(repository.Branch!, "soup-align/");
        Assert.IsTrue(fixture.Process.Commands.All(command => !string.Join(' ', command.Arguments).Contains("secret", StringComparison.Ordinal)));
        var push = fixture.Process.Commands.Single(command => command.Arguments.Contains("push"));
        Assert.IsTrue(push.Arguments.Contains("HEAD:refs/heads/" + repository.Branch));
        Assert.IsFalse(push.Arguments.Contains("--force"));
        Assert.IsTrue(push.Environment.Values.Contains("Authorization: Bearer test-secret"));
        Assert.IsTrue(push.Arguments.Contains("--config-env=http.extraheader=SOUP_GIT_AUTHORIZATION"));
        Assert.IsTrue(fixture.Process.Commands.All(command => command.WorkingDirectory.StartsWith(fixture.Root, StringComparison.Ordinal)));
        Assert.IsFalse(fixture.Process.Commands.Any(command => command.Arguments.Any(argument => argument is "restore" or "build" or "test")));
        StringAssert.Contains(fixture.Http.PostBody!, "refs/heads/release/test");
        StringAssert.Contains(fixture.Http.PostBody!, "Not restored, built or tested");
        StringAssert.Contains(fixture.Http.PostBody!, "NuGetCandidate");
        Assert.AreEqual(1, fixture.Http.Posts);
        Assert.IsTrue(Directory.GetFiles(Path.Combine(fixture.Root, "work"), "journal.json", SearchOption.AllDirectories).Length == 1);
    }

    [TestMethod]
    public async Task RejectsUnapprovedAndChangedManifestBeforeAnyGit()
    {
        using var fixture = new Fixture();
        var unapproved = await fixture.Executor.ExecuteAsync(fixture.Unapproved, null, default);
        Assert.AreEqual(UpdateStatus.Failed, unapproved.Status);
        Assert.AreEqual(0, fixture.Process.Commands.Count);
        fixture.Configuration.Hash = "changed";
        Assert.AreEqual(UpdateStatus.Failed, (await fixture.Execute()).Status);
        Assert.AreEqual(0, fixture.Process.Commands.Count);
    }

    [TestMethod]
    public async Task MovedPlatformBaselineBlocksAllWrites()
    {
        using var fixture = new Fixture();
        fixture.Snapshots.MovePlatform = true;
        Assert.AreEqual(UpdateStatus.Failed, (await fixture.Execute()).Status);
        Assert.AreEqual(0, fixture.Process.Commands.Count);
        Assert.AreEqual(0, fixture.Http.Posts);
    }

    [TestMethod]
    public async Task MissingGitIdentityBlocksEditorAndPush()
    {
        using var fixture = new Fixture();
        fixture.Process.NoIdentity = true;
        Assert.AreEqual(UpdateStatus.Failed, (await fixture.Execute()).Status);
        Assert.IsFalse(fixture.Process.Commands.Any(command => command.Arguments.Contains("checkout") || command.Arguments.Contains("push")));
    }

    [TestMethod]
    public async Task FailedPrReturnsPushedOnlyAndJournalDoesNotLeakToken()
    {
        using var fixture = new Fixture();
        fixture.Http.FailPost = true;
        fixture.Http.PostFailureStatus = HttpStatusCode.ServiceUnavailable;
        var result = await fixture.Execute();
        Assert.AreEqual(UpdateStatus.PartiallyFailed, result.Status);
        Assert.AreEqual(RepositoryUpdateStatus.PushedOnly, result.Repositories.Single().Status);
        Assert.AreEqual(1, fixture.Http.Posts);
        var journal = await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(fixture.Root, "work"), "journal.json", SearchOption.AllDirectories).Single());
        StringAssert.Contains(journal, "PRUnknown");
        Assert.IsFalse(journal.Contains("test-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CancellationAfterPushPreservesActualPushedOutcome()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Process.AfterPush = cancellation.Cancel;
        var result = await fixture.Execute(cancellation.Token);
        Assert.AreEqual(UpdateStatus.Cancelled, result.Status);
        Assert.AreEqual(RepositoryUpdateStatus.PushedOnly, result.Repositories.Single().Status);
        Assert.AreEqual(0, fixture.Http.Posts);
    }

    [TestMethod]
    public async Task ExistingPrAndLostPostResponseDoNotCauseDuplicatePosts()
    {
        using var fixture = new Fixture();
        fixture.Http.Existing = true;
        Assert.AreEqual(UpdateStatus.Completed, (await fixture.Execute()).Status);
        Assert.AreEqual(0, fixture.Http.Posts);
        using var retry = new Fixture();
        retry.Http.LosePostResponse = true;
        Assert.AreEqual(UpdateStatus.Completed, (await retry.Execute()).Status);
        Assert.AreEqual(1, retry.Http.Posts);
    }

    [TestMethod]
    public async Task TraversalFromGitTreeBlocksEditAndPush()
    {
        using var fixture = new Fixture();
        fixture.Process.Traversal = true;
        Assert.AreEqual(UpdateStatus.Failed, (await fixture.Execute()).Status);
        Assert.IsFalse(fixture.Process.Commands.Any(command => command.Arguments.Contains("push")));
    }

    [TestMethod]
    public async Task PlatformMovementDuringPreparationStillBlocksPush()
    {
        using var fixture = new Fixture();
        fixture.Snapshots.MovePlatformAfterFirstRead = true;
        Assert.AreEqual(UpdateStatus.Failed, (await fixture.Execute()).Status);
        Assert.IsFalse(fixture.Process.Commands.Any(command => command.Arguments.Contains("push")));
    }

    [TestMethod]
    public async Task CancellationAfterPrCreationReconcilesTheActualPr()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Http.CancelDuringPost = cancellation.Cancel;
        var result = await fixture.Execute(cancellation.Token);
        Assert.AreEqual(UpdateStatus.Cancelled, result.Status);
        Assert.AreEqual(RepositoryUpdateStatus.Completed, result.Repositories.Single().Status);
        Assert.IsNotNull(result.Repositories.Single().PullRequestUrl);
        Assert.AreEqual(1, fixture.Http.Posts);
    }

    [TestMethod]
    public async Task SecondRepositoryFailurePreservesFirstCompletedPr()
    {
        using var fixture = new Fixture(secondRepository: true);
        fixture.Http.FailOnPost = 2;
        var result = await fixture.Execute();
        Assert.AreEqual(UpdateStatus.PartiallyFailed, result.Status);
        Assert.AreEqual(RepositoryUpdateStatus.Completed, result.Repositories.Single(repository => repository.RepositoryId == "ic").Status);
        Assert.AreEqual(RepositoryUpdateStatus.PushedOnly, result.Repositories.Single(repository => repository.RepositoryId == "ic2").Status);
        Assert.AreEqual(2, fixture.Http.Posts);
    }

    [TestMethod]
    public async Task RealProcessRunnerWorksWithDisposableLocalGitAndCancels()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var runner = new GitProcessRunner();
            var result = await runner.RunAsync(new(directory, ["-c", "core.hooksPath=", "init"], new Dictionary<string, string>()), default);
            Assert.AreEqual(0, result.ExitCode);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(
                new(directory, ["status"], new Dictionary<string, string>()), cancellation.Token));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "soup-adapters-" + Guid.NewGuid().ToString("N"));
        public Process Process { get; } = new();
        public PrHandler Http { get; } = new();
        public Configuration Configuration { get; }
        public Snapshots Snapshots { get; }
        public ChangePlan Unapproved { get; }
        public ChangePlan Approved { get; }
        public GitUpdateExecutor Executor { get; }
        private readonly HttpClient client;
        public Fixture(bool secondRepository = false, bool central = false, string? icUrl = null)
        {
            Directory.CreateDirectory(Root);
            Process.Central = central;
            var ic = TestData.Repo("ic") with { Ref = "release/test" };
            if (icUrl is not null) ic = ic with { Url = icUrl };
            var snapshot = PackageFileEditorTests.Snapshot(central ? Process.CentralXml : Process.Xml) with { Source = ic };
            if (central) snapshot = snapshot with { Files = [.. snapshot.Files, PackageFileEditorTests.File("Directory.Packages.props", Process.CentralProps)] };
            var scanner = new PackageReferenceScanner();
            var usage = scanner.Scan(snapshot, AnalysisRules.Default, default).Usages.Single();
            var platform = TestData.Repo("platform", RepositoryCategory.Platform);
            var platformSnapshot = TestData.Inventory(platform).Snapshot!;
            var analysis = TestData.Analyze(new(ic, snapshot, [usage], [], InventoryStatus.Complete), TestData.Inventory(platform));
            SourceSnapshot? second = null;
            if (secondRepository)
            {
                var other = TestData.Repo("ic2") with { Ref = "release/test" };
                second = snapshot with { Source = other };
                analysis = TestData.Analyze(new(ic, snapshot, [usage], [], InventoryStatus.Complete), TestData.Inventory(platform),
                    new(other, second, scanner.Scan(second, AnalysisRules.Default, default).Usages, [], InventoryStatus.Complete));
            }
            analysis = analysis with { Packages = [.. analysis.Packages.Select(package => package with
                { NuGetCandidate = new(package.PackageId, NuGetVersion.Parse("2.0.0"), CandidateStatus.Available, "nuget.org", DateTimeOffset.UtcNow) })] };
            Unapproved = new ChangePlanner().Create(analysis, secondRepository ? [new("ic", "X"), new("ic2", "X")] : [new("ic", "X")],
                [new("X", TargetSource.NuGetCandidate)]);
            Approved = Unapproved.Approve(analysis, "tester", DateTimeOffset.UtcNow);
            Configuration = new(platform);
            Snapshots = new(snapshot, platformSnapshot) { OtherIC = second };
            client = new(Http);
            Executor = new(Snapshots, scanner, Configuration, new Tokens(), client, Process, Root);
        }
        public Task<UpdateResult> Execute(CancellationToken cancellationToken = default) => Executor.ExecuteAsync(Approved, null, cancellationToken);
        public void Dispose() { client.Dispose(); Directory.Delete(Root, true); }
    }

    private sealed class Configuration(RepositorySource platform) : IConfigurationService
    {
        public string Hash { get; set; } = "manifest-sha256";
        public Task<LoadedConfiguration> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new LoadedConfiguration(
            new(1, "1", [platform]), Hash, AnalysisRules.Default, new(1, "", "", "", [])));
    }
    private sealed class Tokens : IAccessTokenProvider
    {
        public Task<AccessToken> GetTokenAsync(bool allowInteractive, CancellationToken cancellationToken)
        { Assert.IsFalse(allowInteractive); return Task.FromResult(new AccessToken("test-secret", DateTimeOffset.UtcNow.AddHours(1))); }
        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class Snapshots(SourceSnapshot ic, SourceSnapshot platform) : IRepositorySnapshotProvider
    {
        public bool MovePlatform { get; set; }
        public bool MovePlatformAfterFirstRead { get; set; }
        public SourceSnapshot? OtherIC { get; init; }
        private int platformReads;
        public Task<SourceSnapshot> GetSnapshotAsync(RepositorySource source, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Category != RepositoryCategory.Platform) return Task.FromResult(OtherIC?.Source.Id == source.Id ? OtherIC : ic);
            platformReads++;
            return Task.FromResult(platform with { Commit = MovePlatform || MovePlatformAfterFirstRead && platformReads > 1
                ? new string('b', 40) : platform.Commit });
        }
    }
    private sealed class Process : IGitProcessRunner
    {
        public const string Xml = "<Project><ItemGroup><PackageReference Include='X' Version='1.0'/></ItemGroup></Project>";
        public const string CentralXml = "<Project><ItemGroup><PackageReference Include='X'/></ItemGroup></Project>";
        public const string CentralProps = "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include='X' Version='1.0'/></ItemGroup></Project>";
        public bool Central { get; set; }
        public string? Consumer { get; set; }
        public List<GitProcessRequest> Commands { get; } = [];
        public bool NoIdentity { get; set; }
        public bool Traversal { get; set; }
        public bool FailFetch { get; set; }
        public Action? AfterPush { get; set; }
        private readonly Dictionary<string, string> remoteHeads = new(StringComparer.Ordinal);
        public async Task<GitProcessResult> RunAsync(GitProcessRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(request);
            var arguments = request.Arguments;
            if (arguments.Contains("fetch") && FailFetch) return new(1, "", "Simulated offline retry");
            var remoteIdentity = Path.GetDirectoryName(request.WorkingDirectory) + "|";
            if (arguments.Contains("ls-remote"))
            {
                var reference = arguments.Last();
                return new(0, remoteHeads.TryGetValue(remoteIdentity + reference, out var head) ? head + "\t" + reference + "\n" : "", "");
            }
            if (arguments.Contains("user.name") || arguments.Contains("user.email")) return new(NoIdentity ? 1 : 0, NoIdentity ? "" : "test", "");
            if (arguments.Contains("checkout"))
            {
                Directory.CreateDirectory(Path.Combine(request.WorkingDirectory, "src"));
                var checkoutXml = Central ? CentralXml : arguments.Contains("--detach") ? Xml.Replace("1.0", "2.0.0", StringComparison.Ordinal) : Xml;
                await File.WriteAllTextAsync(Path.Combine(request.WorkingDirectory, "src", "A.csproj"), checkoutXml, cancellationToken);
                if (Central) await File.WriteAllTextAsync(Path.Combine(request.WorkingDirectory, "Directory.Packages.props"), CentralProps, cancellationToken);
                if (Consumer is not null)
                {
                    var consumerPath = Path.Combine(request.WorkingDirectory, Consumer.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(consumerPath)!);
                    await File.WriteAllTextAsync(consumerPath, CentralXml, cancellationToken);
                }
            }
            if (arguments.Contains("ls-files")) return new(0, Traversal ? "../escape.props\0" : "src/A.csproj\0" + (Central ? "Directory.Packages.props\0" : "") + (Consumer is null ? "" : Consumer + "\0"), "");
            if (arguments.Contains("rev-parse")) return new(0, new string(arguments.Contains("FETCH_HEAD") || arguments.Last().EndsWith('^') ? 'a' : 'c', 40), "");
            if (arguments.Contains("diff") && arguments.Contains("--name-only")) return new(0, Central ? "Directory.Packages.props\0" : "src/A.csproj\0", "");
            if (arguments.Contains("push"))
            {
                remoteHeads[remoteIdentity + arguments.Last()["HEAD:".Length..]] = new string('c', 40);
                AfterPush?.Invoke();
            }
            return new(0, "", "");
        }
    }
    private sealed class PrHandler : HttpMessageHandler
    {
        public int Posts { get; private set; }
        public string? PostBody { get; private set; }
        public bool FailPost { get; set; }
        public HttpStatusCode PostFailureStatus { get; set; } = HttpStatusCode.Forbidden;
        public bool OmitWebLink { get; set; }
        public int FailOnPost { get; set; }
        public bool Existing { get; set; }
        public string Status { get; set; } = "active";
        public int FailedGets { get; set; }
        public bool FailGetsAfterPost { get; set; }
        public bool FullFirstPage { get; set; }
        public bool MatchOnFirstPage { get; set; }
        public bool DuplicateMatches { get; set; }
        public int PrId { get; set; } = 1;
        public List<string> GetQueries { get; } = [];
        public bool LosePostResponse { get; set; }
        public Action? CancelDuringPost { get; set; }
        private string source = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
            StringAssert.Contains(request.RequestUri!.Query, "api-version=7.1");
            if (request.Method == HttpMethod.Get)
            {
                GetQueries.Add(request.RequestUri.Query);
                source = Uri.UnescapeDataString(request.RequestUri.Query.Split('&').Single(part => part.Contains("sourceRefName=", StringComparison.Ordinal)).Split('=')[1]);
                if (FailGetsAfterPost && Posts > 0) return new(HttpStatusCode.ServiceUnavailable);
                if (FailedGets > 0) { FailedGets--; return new(HttpStatusCode.ServiceUnavailable); }
                var visible = Existing && (Status == "active" || request.RequestUri.Query.Contains("searchCriteria.status=all", StringComparison.Ordinal));
                if (FullFirstPage)
                {
                    if (request.RequestUri.Query.Contains("$skip=0", StringComparison.Ordinal))
                    {
                        var unrelated = Pr().Replace(source, "refs/heads/unrelated", StringComparison.Ordinal);
                        var items = Enumerable.Repeat(unrelated, visible && MatchOnFirstPage ? 99 : 100);
                        if (visible && MatchOnFirstPage) items = items.Append(Pr());
                        return Json("{\"value\":[" + string.Join(',', items) + "]}");
                    }
                    if (MatchOnFirstPage && !DuplicateMatches) visible = false;
                }
                else if (visible && DuplicateMatches) return Json("{\"value\":[" + Pr() + "," + Pr() + "]}");
                return Json(visible ? "{\"value\":[" + Pr() + "]}" : "{\"value\":[]}");
            }
            Posts++;
            PostBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (CancelDuringPost is not null) { Existing = true; CancelDuringPost(); throw new OperationCanceledException(cancellationToken); }
            if (LosePostResponse) { Existing = true; throw new HttpRequestException("Lost response"); }
            return FailPost || Posts == FailOnPost
                ? new(PostFailureStatus) { Content = new StringContent("{\"typeKey\":\"GitPullRequestAccessDeniedException\",\"message\":\"test-secret\"}", Encoding.UTF8, "application/json") }
                : Json(Pr());
        }
        private string Pr() => "{\"pullRequestId\":" + PrId + ",\"status\":\"" + Status + "\",\"sourceRefName\":\"" + source
            + "\",\"targetRefName\":\"refs/heads/release/test\""
            + (OmitWebLink ? "" : ",\"_links\":{\"web\":{\"href\":\"https://dev.azure.com/example/platform/_git/ic/pullrequest/" + PrId + "\"}}") + "}";
        private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}