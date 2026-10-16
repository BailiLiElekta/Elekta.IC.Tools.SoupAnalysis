using System.Collections.Concurrent;
using System.Collections.Immutable;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Elekta.IC.Tools.SoupAnalysis.Tests;

[TestClass]
public sealed class WorkflowTests
{
    [TestMethod]
    public async Task ScanIsBoundedParsesOffThreadAndQueriesOnlySoup()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var client = TestData.Repo("client");
        var emulator = TestData.Repo("emulator", RepositoryCategory.ICEmulator);
        var fake = new ScanFake([TestData.Usage(platform, "Shared", "2.0"),
            TestData.Usage(client, "Shared", "1.0"), TestData.Usage(client, "Soup", "1.0"),
            TestData.Usage(client, "elekta.Internal", "1.0"), TestData.Usage(emulator, "EmulatorOnly", "99.0")]);
        var callerThread = 0;
        var progress = new ProgressLog();
        var started = new TaskCompletionSource<Task<AnalysisResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            started.SetResult(new ScanWorkflow(fake, fake, fake).ScanAsync(Request(platform, client, emulator), progress));
        }).Start();
        var task = await started.Task;
        var result = await task;
        Assert.IsTrue(result.CanUpdate);
        Assert.IsTrue(fake.MaximumConcurrency <= 2);
        Assert.IsTrue(fake.ParseThreads.All(thread => thread != callerThread));
        CollectionAssert.AreEquivalent(new[] { "Soup" }, fake.Queries.ToArray());
        Assert.AreEqual(OperationStage.Ready, progress.Events.Last().Stage);
        Assert.HasCount(3, result.Inventories);
    }

    [TestMethod]
    public async Task FailuresRemainVisibleAndNuGetFailureDoesNotBlockICChoice()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var client = TestData.Repo("client");
        var emulator = TestData.Repo("emulator", RepositoryCategory.ICEmulator);
        var fake = new ScanFake([TestData.Usage(client, "Soup", "1.0")])
            { FailedRepository = emulator.Id, FailNuGet = true };
        var result = await new ScanWorkflow(fake, fake, fake).ScanAsync(Request(platform, client, emulator));
        Assert.IsTrue(result.CanUpdate);
        Assert.AreEqual(InventoryStatus.Failed, result.Inventories.Single(item => item.Source == emulator).Status);
        Assert.AreEqual(CandidateStatus.Failed, result.Packages[0].NuGetCandidate?.Status);
        var plan = new ChangePlanner().Create(result, [new(client.Id, "Soup")], [new("Soup", TargetSource.ICMax)]);
        Assert.AreEqual(ChangePlanStatus.NoChanges, plan.Status);
        Assert.ThrowsExactly<InvalidOperationException>(() => new ChangePlanner().Create(result,
            [new(client.Id, "Soup")], [new("Soup", TargetSource.NuGetCandidate)]));
        fake.FailedRepository = platform.Id;
        var partial = await new ScanWorkflow(fake, fake, fake).ScanAsync(Request(platform, client));
        Assert.IsFalse(partial.CanUpdate);
        Assert.AreEqual(PackageOwnership.Unconfirmed, partial.Packages[0].Ownership);
    }

    [TestMethod]
    public async Task OmittingAManifestMemberIsRejectedBeforeIO()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var client = TestData.Repo("client");
        var fake = new ScanFake([]);
        var request = Request(platform, client) with { Sources = [client] };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => new ScanWorkflow(fake, fake, fake).ScanAsync(request));
        Assert.AreEqual(0, fake.SnapshotCalls);
    }

    [TestMethod]
    public async Task CancellationIsNotConvertedIntoPartialSuccess()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var fake = new ScanFake([]);
        using var cancellation = new CancellationTokenSource();
        var progress = new ProgressLog();
        var task = new ScanWorkflow(fake, fake, fake).ScanAsync(Request(platform), progress, cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        Assert.AreEqual(OperationStage.Cancelled, progress.Events.Last().Stage);
        Assert.IsFalse(progress.Events.Any(item => item.Stage == OperationStage.Ready));
    }

    [TestMethod]
    public void SharedPlansSkipNoOpsAndRequireCurrentApproval()
    {
        var (analysis, client) = SharedAnalysis();
        var planner = new ChangePlanner();
        var plan = planner.Create(analysis, [new(client.Id, "Shared")], [new("Shared", TargetSource.PlatformMax)]);
        Assert.HasCount(1, plan.Changes);
        Assert.HasCount(1, plan.Changes[0].Usages);
        Assert.AreEqual(AlignmentAction.Upgrade, plan.Changes[0].Usages[0].Action);
        Assert.IsFalse(plan.CanExecute);
        var approved = plan.Approve(analysis, "reviewer", DateTimeOffset.UtcNow);
        Assert.IsTrue(approved.CanExecute);
        Assert.IsFalse(plan.CanExecute);
        Assert.ThrowsExactly<InvalidOperationException>(() => plan.Approve(analysis with { ManifestHash = "changed" },
            "reviewer", DateTimeOffset.UtcNow));
        var moved = analysis with { Inventories = [.. analysis.Inventories.Select(inventory =>
            inventory.Source == client ? inventory with { Snapshot = inventory.Snapshot! with { Commit = new string('b', 40) } } : inventory)] };
        Assert.IsFalse(approved.Matches(moved));
    }

    [TestMethod]
    public void InvalidSelectionsAndLockedTargetsAreRejected()
    {
        var (analysis, client) = SharedAnalysis();
        var planner = new ChangePlanner();
        Assert.ThrowsExactly<ArgumentException>(() => planner.Create(analysis,
            [new("missing", "Shared")], [new("Shared", TargetSource.PlatformMax)]));
        Assert.ThrowsExactly<ArgumentException>(() => planner.Create(analysis,
            [new("platform", "Shared")], [new("Shared", TargetSource.PlatformMax)]));
        Assert.ThrowsExactly<ArgumentException>(() => planner.Create(analysis,
            [new(client.Id, "missing")], [new("missing", TargetSource.ICMax)]));
        Assert.ThrowsExactly<ArgumentException>(() => planner.Create(analysis,
            [new(client.Id, "Shared")], [new("Shared", TargetSource.NuGetCandidate)]));
        Assert.ThrowsExactly<ArgumentException>(() => planner.Create(analysis,
            [new(client.Id, "Shared")], [new("Shared", TargetSource.PlatformMax), new("shared", TargetSource.ICMax)]));
        Assert.ThrowsExactly<InvalidOperationException>(() => planner.Create(analysis with { CompleteIC = false },
            [new(client.Id, "Shared")], [new("Shared", TargetSource.PlatformMax)]));
    }

    [TestMethod]
    public async Task ExecutorIsNeverCalledForUnapprovedOrStalePlans()
    {
        var (analysis, client) = SharedAnalysis();
        var plan = new ChangePlanner().Create(analysis, [new(client.Id, "Shared")], [new("Shared", TargetSource.PlatformMax)]);
        var executor = new OutputFake();
        var workflow = new UpdateWorkflow(executor);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => workflow.ExecuteAsync(plan, analysis));
        var approved = plan.Approve(analysis, "reviewer", DateTimeOffset.UtcNow);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => workflow.ExecuteAsync(approved, analysis with { Id = Guid.NewGuid() }));
        Assert.AreEqual(0, executor.UpdateCalls);
        var result = await workflow.ExecuteAsync(approved, analysis);
        Assert.AreEqual(UpdateStatus.Completed, result.Status);
        Assert.AreEqual(1, executor.UpdateCalls);
    }

    [TestMethod]
    public async Task SelectedInternalCustomChangeUsesTheSameCurrentApprovalWorkflow()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        var other = TestData.Repo("other");
        var analysis = TestData.Analyze(TestData.Inventory(platform),
            TestData.Inventory(ic, TestData.Usage(ic, "elekta.Internal", "5.0"), TestData.Usage(ic, "elekta.Unselected", "1.0")),
            TestData.Inventory(other, TestData.Usage(other, "elekta.Internal", "9.0")));
        var plan = new ChangePlanner().Create(analysis, [new(ic.Id, "elekta.Internal")],
            [new("elekta.Internal", TargetSource.Custom, "0.5-rc.1")]);
        Assert.HasCount(1, plan.Changes);
        Assert.AreEqual(ic.Id, plan.Changes[0].RepositoryId);
        Assert.AreEqual(TargetSource.Custom, plan.Changes[0].TargetSource);
        Assert.AreEqual("0.5.0-rc.1", plan.Changes[0].Target.ToNormalizedString());
        var executor = new OutputFake();
        var workflow = new UpdateWorkflow(executor);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => workflow.ExecuteAsync(plan, analysis));
        var approved = plan.Approve(analysis, "reviewer", DateTimeOffset.UtcNow);
        var moved = analysis with { Inventories = [.. analysis.Inventories.Select(inventory => inventory.Source == ic
            ? inventory with { Snapshot = inventory.Snapshot! with { Commit = new string('b', 40) } } : inventory)] };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => workflow.ExecuteAsync(approved, moved));
        Assert.AreEqual(0, executor.UpdateCalls);
        Assert.AreEqual(UpdateStatus.Completed, (await workflow.ExecuteAsync(approved, analysis)).Status);
        Assert.AreEqual(1, executor.UpdateCalls);
    }

    [TestMethod]
    public async Task ExportPassesTheSameImmutableAnalysis()
    {
        var (analysis, _) = SharedAnalysis();
        var writer = new OutputFake();
        await new ExportReportWorkflow(writer).ExportAsync(analysis, "report.xlsx");
        Assert.AreSame(analysis, writer.Exported);
    }

    [TestMethod]
    public void SoupChoiceIsExplicitAndOneTargetPerPackageAcrossRepositories()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var client = TestData.Repo("client");
        var other = TestData.Repo("other");
        var analysis = TestData.Analyze(TestData.Inventory(platform),
            TestData.Inventory(client, TestData.Usage(client, "Soup", "1.0")),
            TestData.Inventory(other, TestData.Usage(other, "soup", "3.0")));
        analysis = analysis with { Packages = [analysis.Packages[0] with
        {
            NuGetCandidate = new("Soup", PackageVersions.ParseExact("2.0"), CandidateStatus.Available,
                "https://api.nuget.org/v3/index.json", DateTimeOffset.UtcNow)
        }] };
        Assert.AreEqual(AlignmentAction.AwaitingSelection, analysis.Packages[0].Repositories[0].Action);
        var planner = new ChangePlanner();
        var nuget = planner.Create(analysis, [new(client.Id, "Soup"), new(other.Id, "Soup")],
            [new("Soup", TargetSource.NuGetCandidate)]);
        Assert.HasCount(2, nuget.Changes);
        Assert.IsTrue(nuget.Changes.All(change => change.Target.ToNormalizedString() == "2.0.0"));
        Assert.AreEqual(AlignmentAction.Upgrade, nuget.Changes[0].Usages[0].Action);
        Assert.AreEqual(AlignmentAction.Downgrade, nuget.Changes[1].Usages[0].Action);
        var current = planner.Create(analysis, [new(client.Id, "Soup"), new(other.Id, "Soup")],
            [new("Soup", TargetSource.ICMax)]);
        Assert.HasCount(1, current.Changes);
        Assert.AreEqual("3.0.0", current.Changes[0].Target.ToNormalizedString());
        Assert.AreEqual("3.0.0", analysis.Packages[0].ICMax?.ToNormalizedString());
    }

    [TestMethod]
    public void AmbiguousDeclarationOwnershipBlocksPlanning()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var client = TestData.Repo("client");
        var usage = TestData.Usage(client, "Shared", "1.0");
        usage = usage with { Declaration = usage.Declaration with { IsUniquelyOwned = false } };
        var analysis = TestData.Analyze(TestData.Inventory(platform, TestData.Usage(platform, "Shared", "2.0")),
            TestData.Inventory(client, usage));
        Assert.ThrowsExactly<InvalidOperationException>(() => new ChangePlanner().Create(analysis,
            [new(client.Id, "Shared")], [new("Shared", TargetSource.PlatformMax)]));
    }

    [TestMethod]
    public async Task SharedOperationGateRejectsOverlappingScans()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var fake = new ScanFake([]);
        var gate = new OperationGate();
        var workflow = new ScanWorkflow(fake, fake, fake, gate);
        var first = workflow.ScanAsync(Request(platform));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => workflow.ScanAsync(Request(platform)));
        await first;
        var next = await workflow.ScanAsync(Request(platform));
        Assert.IsTrue(next.CompletePlatform);
    }

    private static ScanRequest Request(params RepositorySource[] sources) => new(
        new(1, "1", [.. sources.Where(source => source.Category == RepositoryCategory.Platform)]),
        "manifest-hash", [.. sources], AnalysisRules.Default with { MaxConcurrency = 2 });

    private static (AnalysisResult Analysis, RepositorySource Client) SharedAnalysis()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var client = TestData.Repo("client");
        return (TestData.Analyze(TestData.Inventory(platform, TestData.Usage(platform, "Shared", "2.0")),
            TestData.Inventory(client, TestData.Usage(client, "Shared", "1.0"), TestData.Usage(client, "Shared", "2.0"))), client);
    }

    internal sealed class ProgressLog : IProgress<OperationProgress>
    {
        public ConcurrentQueue<OperationProgress> Events { get; } = new();
        public void Report(OperationProgress value) => Events.Enqueue(value);
    }

    internal sealed class ScanFake(ImmutableArray<PackageUsage> usages) : IRepositorySnapshotProvider, IPackageScanner, INuGetCandidateProvider
    {
        private int active;
        private int maximum;
        private int snapshotCalls;
        public int MaximumConcurrency => maximum;
        public int SnapshotCalls => snapshotCalls;
        public string? FailedRepository { get; set; }
        public bool FailNuGet { get; set; }
        public ConcurrentQueue<int> ParseThreads { get; } = new();
        public ConcurrentQueue<string> Queries { get; } = new();

        public async Task<SourceSnapshot> GetSnapshotAsync(RepositorySource source, IProgress<OperationProgress>? progress,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref snapshotCalls);
            var count = Interlocked.Increment(ref active);
            int observed;
            do { observed = maximum; } while (count > observed && Interlocked.CompareExchange(ref maximum, count, observed) != observed);
            try
            {
                await Task.Delay(40, cancellationToken);
                if (source.Id == FailedRepository) throw new IOException("simulated snapshot failure");
                return new(source, new string('a', 40), [], DateTimeOffset.UtcNow);
            }
            finally { Interlocked.Decrement(ref active); }
        }

        public PackageScanResult Scan(SourceSnapshot snapshot, AnalysisRules rules, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ParseThreads.Enqueue(Environment.CurrentManagedThreadId);
            return new([.. usages.Where(usage => usage.Repository == snapshot.Source)], []);
        }

        public Task<NuGetCandidate> GetCandidateAsync(string packageId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries.Enqueue(packageId);
            if (FailNuGet) throw new IOException("simulated metadata failure");
            return Task.FromResult(new NuGetCandidate(packageId, PackageVersions.ParseExact("2.0-rc.1"),
                CandidateStatus.Available, "https://api.nuget.org/v3/index.json", DateTimeOffset.UtcNow));
        }
    }

    private sealed class OutputFake : IUpdateExecutor, IExcelReportWriter
    {
        public int UpdateCalls { get; private set; }
        public AnalysisResult? Exported { get; private set; }
        public Task<UpdateResult> ExecuteAsync(ChangePlan approvedPlan, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
        {
            UpdateCalls++;
            return Task.FromResult(new UpdateResult(approvedPlan.Id, UpdateStatus.Completed, []));
        }
        public Task WriteAsync(AnalysisResult analysis, string outputPath, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
        {
            Exported = analysis;
            return Task.CompletedTask;
        }
    }
}