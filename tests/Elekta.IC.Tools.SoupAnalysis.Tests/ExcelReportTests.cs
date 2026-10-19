using ClosedXML.Excel;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Reporting;
using NuGet.Versioning;

namespace Elekta.IC.Tools.SoupAnalysis.Tests;

[TestClass]
public sealed class ExcelReportTests
{
    [TestMethod]
    [DataRow(RefKind.Tag, "29.5.0")]
    [DataRow(RefKind.Branch, "main")]
    public async Task ProducerTargetsFlowThroughGuiReviewIssuesAndExport(RefKind kind, string reference)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            await OnDispatcherAsync(async () =>
            {
                var producer = TestData.Repo("elekta.platform.identity", RepositoryCategory.Platform)
                    with { RefKind = kind, Ref = reference };
                var ic = TestData.Repo("ic");
                var emulator = TestData.Repo("emulator", RepositoryCategory.ICEmulator);
                const string packageId = "Elekta.Platform.Identity.HealthChecks";
                var manifest = new PlatformManifest(1, "3", [producer])
                {
                    PackageMappings = [new(producer.Id, ["Elekta.Platform.Identity"])]
                };
                var analysis = AlignmentAnalyzer.Analyze(Guid.NewGuid(), manifest, "hash", [producer, ic, emulator],
                    [TestData.Inventory(producer), TestData.Inventory(ic, TestData.Usage(ic, packageId, "28.0")),
                        TestData.Inventory(emulator, TestData.Usage(emulator, packageId, "30.0"))], DateTimeOffset.UtcNow);
                var ports = new GuiPorts(analysis, path, manifest);
                using var model = new Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels.MainViewModel(ports, ports, ports,
                    new ScanWorkflow(ports, ports, ports), new ExportReportWorkflow(ports), new UpdateWorkflow(ports), ports);
                await model.InitializeCommand.ExecuteAsync(null);
                await model.SetAnalysisAsync(analysis);
                Assert.IsNull(model.UserError);
                var row = model.Packages.Single();
                Assert.AreEqual("Platform package", row.SourceLabel);
                Assert.AreEqual(kind == RefKind.Tag ? "Upgrade" : "Unresolved", row.Action);
                var review = model.ReviewRows.Single();
                Assert.AreEqual(kind == RefKind.Tag, review.Eligible);
                if (kind == RefKind.Tag)
                {
                    review.IsSelected = true;
                    await model.ReviewCommand.ExecuteAsync(null);
                    Assert.IsNull(model.UserError);
                    Assert.IsTrue(model.CanExecute);
                    Assert.AreEqual(TargetSource.PlatformTag, ports.ReviewedPlan!.Changes.Single().TargetSource);
                }
                else
                {
                    Assert.IsTrue(model.Issues.Any(issue => issue.Code == "PlatformTagUnresolved"
                        && issue.RepositoryId == ic.Id && issue.Message.Contains(reference)));
                }
                model.SelectedAnalysisRepository = model.AnalysisRepositoryChoices.Single(choice => choice.Id == emulator.Id);
                Assert.AreEqual(kind == RefKind.Tag ? "Downgrade" : "Unresolved", row.Action);
                Assert.IsFalse(row.IsTargetEditable);
                await model.ExportCommand.ExecuteAsync(null);
                Assert.IsNull(model.UserError);
                using var workbook = new XLWorkbook(path);
                foreach (var sheetName in new[] { "IC Alignment", "IC Emulator" })
                {
                    var sheet = workbook.Worksheet(sheetName);
                    Assert.AreEqual(producer.Name, Value(sheet, 2, "Platform Producer"));
                    Assert.AreEqual(reference, Value(sheet, 2, "Producer Ref"));
                    Assert.AreEqual(kind == RefKind.Tag ? "29.5.0" : "", Value(sheet, 2, "Selected Target"));
                    if (kind != RefKind.Tag)
                    {
                        Assert.AreEqual("Unresolved", Value(sheet, 2, "Action"));
                        StringAssert.Contains(Value(sheet, 2, "Platform Target Issue"), reference);
                    }
                }
            });
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task PreviewWorkbookRetainsDraftTargetsNotApprovedStatusAndSnapshotProvenance()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        var other = TestData.Repo("failed");
        var analysis = TestData.Analyze(TestData.Inventory(platform, TestData.Usage(platform, "shared", "2.0")),
            TestData.Inventory(ic, TestData.Usage(ic, "shared", "1.0"), TestData.Usage(ic, "soup", "1.0"),
                TestData.Usage(ic, "elekta.extra", "1.0")), TestData.Inventory(other) with { Status = InventoryStatus.Failed, Snapshot = null });
        analysis = analysis with { Packages = [.. analysis.Packages.Select(package => package.PackageId == "soup"
            ? package with { ICMax = PackageVersions.ParseExact("10.0.12") } : package)] };
        var selections = System.Collections.Immutable.ImmutableArray.Create(new RepositoryPackageSelection(ic.Id, "shared"), new(ic.Id, "soup"));
        var targets = System.Collections.Immutable.ImmutableArray.Create(new PackageTargetSelection("shared", TargetSource.PlatformMax), new("soup", TargetSource.ICMax));
        var preview = new ChangePlanner().CreatePreview(analysis, selections, targets);
        var report = new ReportSelection(analysis.Id, targets.Add(new("elekta.extra", TargetSource.Custom, "25.1.3")), selections, preview);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            await new ExcelReportWriter().WriteAsync(analysis, path, null, default, report);
            using var workbook = new XLWorkbook(path);
            var sheet = workbook.Worksheet("IC Alignment");
            foreach (var row in sheet.RowsUsed().Skip(1))
            {
                var number = row.RowNumber();
                var package = Value(sheet, number, "Package");
                Assert.AreEqual(package == "elekta.extra" ? "Not selected" : "Preview only / Not approved", Value(sheet, number, "Approval Status"));
                Assert.AreEqual(package == "soup" ? "10.0.12" : package == "shared" ? "2.0.0" : "25.1.3", Value(sheet, number, "Selected Target"));
                Assert.AreEqual(analysis.Id.ToString(), Value(sheet, number, "Analysis ID"));
                Assert.AreEqual(analysis.ManifestHash, Value(sheet, number, "Manifest Hash"));
                Assert.AreEqual(new string('a', 40), Value(sheet, number, "Commit"));
            }
            var sources = workbook.Worksheet("Scan Sources");
            Assert.HasCount(analysis.OriginalRefs.Length + 1, sources.RowsUsed());
            Assert.IsFalse(preview.CanExecute);
            Assert.IsFalse(workbook.Worksheets.SelectMany(sheet => sheet.CellsUsed()).Any(cell => cell.HasFormula));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GuiExportPreservesSharedApprovalWithUnselectedCustomReportTarget(bool approved)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            await OnDispatcherAsync(async () =>
            {
                var platform = TestData.Repo("platform", RepositoryCategory.Platform);
                var ic = TestData.Repo("ic");
                var analysis = TestData.Analyze(TestData.Inventory(platform, TestData.Usage(platform, "shared", "2.0")),
                    TestData.Inventory(ic, TestData.Usage(ic, "shared", "1.0"), TestData.Usage(ic, "elekta.Internal", "1.0"),
                        TestData.Usage(ic, "elekta.Unfilled", "3.0")));
                var ports = new GuiPorts(analysis, path);
                using var model = new Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels.MainViewModel(ports, ports, ports,
                    new ScanWorkflow(ports, ports, ports), new ExportReportWorkflow(ports), new UpdateWorkflow(ports), ports);
                await model.InitializeCommand.ExecuteAsync(null);
                await model.SetAnalysisAsync(analysis);
                model.Packages.Single(row => row.PackageId == "elekta.Internal").CustomVersionText = "2.0-preview.1";
                model.SelectedChangeRepository = model.RepositoryChoices.Single(choice => choice.Source.Id == ic.Id);
                model.ReviewRows.Single(row => row.PackageId == "shared").IsSelected = true;
                if (approved) await model.ReviewCommand.ExecuteAsync(null);
                Assert.IsNull(model.UserError);
                Assert.AreEqual(approved, model.CanExecute);
                await model.ExportCommand.ExecuteAsync(null);
                Assert.IsNull(model.UserError);
                var exported = ports.ExportedSelection!;
                Assert.HasCount(2, exported.Targets);
                Assert.HasCount(1, exported.Selections);
                if (approved)
                {
                    Assert.IsNotNull(exported.ReviewPlan);
                    Assert.IsTrue(exported.ReviewPlan.CanExecute);
                    Assert.IsTrue(exported.ReviewPlan.Matches(analysis));
                    Assert.AreEqual(ports.ReviewedPlan!.Id, exported.ReviewPlan.Id);
                    Assert.AreEqual("shared", exported.ReviewPlan.Changes.Single().PackageId);
                    await model.ExportCommand.ExecuteAsync(null);
                    Assert.IsNull(model.UserError);
                    Assert.IsTrue(model.CanExecute);
                    Assert.AreSame(exported.ReviewPlan, ports.ExportedSelection!.ReviewPlan);
                }
                else Assert.IsNull(exported.ReviewPlan);
                using var workbook = new XLWorkbook(path);
                foreach (var name in new[] { "IC References", "IC Alignment" })
                {
                    var sheet = workbook.Worksheet(name);
                    foreach (var row in sheet.RowsUsed().Skip(1))
                    {
                        var number = row.RowNumber();
                        var package = Value(sheet, number, "Package");
                        Assert.AreEqual(package == "shared" ? approved ? "Approved" : "AwaitingApproval" : "Not selected",
                            Value(sheet, number, "Approval Status"));
                        Assert.AreEqual(package == "shared" ? "2.0.0" : package == "elekta.Internal" ? "2.0.0-preview.1" : "",
                            Value(sheet, number, "Selected Target"));
                        Assert.AreEqual(package == "shared" ? "PlatformMax" : package == "elekta.Internal" ? "Custom" : "",
                            Value(sheet, number, "Target Source"));
                        Assert.AreEqual(package == "elekta.Unfilled" ? "NotApplicable" : "Upgrade", Value(sheet, number, "Action"));
                    }
                }
                Assert.AreEqual(1, workbook.Worksheet("IC SOUP").LastRowUsed()!.RowNumber());
                Assert.IsFalse(workbook.Worksheets.SelectMany(sheet => sheet.CellsUsed()).Any(cell => cell.HasFormula));
            });
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GuiExportPreservesNuGetChoicesIncludingAlignedAndUnselectedRows(bool approved)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            await OnDispatcherAsync(async () =>
            {
                var platform = TestData.Repo("platform", RepositoryCategory.Platform);
                var ic = TestData.Repo("ic");
                var other = TestData.Repo("other");
                var analysis = TestData.Analyze(TestData.Inventory(platform),
                    TestData.Inventory(ic, TestData.Usage(ic, "changed", "1.0"), TestData.Usage(ic, "aligned", "2.0")),
                    TestData.Inventory(other, TestData.Usage(other, "changed", "1.0")));
                analysis = analysis with { Packages = [.. analysis.Packages.Select(package => package with
                    { NuGetCandidate = new(package.PackageId, NuGetVersion.Parse("2.0"), CandidateStatus.Available, "nuget.org", DateTimeOffset.UtcNow) })] };
                var ports = new GuiPorts(analysis, path);
                using var model = new Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels.MainViewModel(ports, ports, ports,
                    new ScanWorkflow(ports, ports, ports), new ExportReportWorkflow(new ExcelReportWriter()), new UpdateWorkflow(ports), ports);
                await model.InitializeCommand.ExecuteAsync(null);
                await model.SetAnalysisAsync(analysis);
                foreach (var package in model.Packages) package.SelectedTarget = package.Choices.Single(choice => choice.Source == TargetSource.NuGetCandidate);
                model.SelectedChangeRepository = model.RepositoryChoices.Single(choice => choice.Source.Id == "ic");
                foreach (var row in model.ReviewRows) row.IsSelected = true;
                if (approved) await model.ReviewCommand.ExecuteAsync(null);
                await model.ExportCommand.ExecuteAsync(null);
                Assert.IsNull(model.UserError);
                using var workbook = new XLWorkbook(path);
                var sheet = workbook.Worksheet("IC SOUP");
                foreach (var row in sheet.RowsUsed().Skip(1))
                {
                    var number = row.RowNumber();
                    Assert.AreEqual("2.0.0", Value(sheet, number, "Selected Target"));
                    Assert.AreEqual("NuGetCandidate", Value(sheet, number, "Target Source"));
                    var selected = Value(sheet, number, "Repository ID") == "ic";
                    Assert.AreEqual(selected ? approved ? "Approved" : "AwaitingApproval" : "Not selected", Value(sheet, number, "Approval Status"));
                    Assert.AreEqual(Value(sheet, number, "Package") == "aligned" ? "Aligned" : "Upgrade", Value(sheet, number, "Action"));
                }
                Assert.AreEqual("Completed", model.Stage);
            });
        }
        finally { File.Delete(path); }
    }

    private static Task OnDispatcherAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); }
            }));
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class GuiPorts(AnalysisResult analysis, string path, PlatformManifest? manifest = null) : IConfigurationService, ISettingsStore, IAccessTokenProvider,
        IRepositorySnapshotProvider, IPackageScanner, INuGetCandidateProvider, IUpdateExecutor, IExcelReportWriter,
        Elekta.IC.Tools.SoupAnalysis.Desktop.Presentation.IUserInteraction
    {
        public ReportSelection? ExportedSelection { get; private set; }
        public ChangePlan? ReviewedPlan { get; private set; }
        public Task<LoadedConfiguration> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new LoadedConfiguration(
            manifest ?? new(1, analysis.ManifestVersion, [.. analysis.OriginalRefs.Where(source => source.Category == RepositoryCategory.Platform)]),
            analysis.ManifestHash, AnalysisRules.Default, new(1, "", "", "", ["scope"])));
        Task<UserSettings> ISettingsStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(UserSettings.Default with
            { Repositories = [.. analysis.OriginalRefs.Where(source => source.Category != RepositoryCategory.Platform)] });
        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<AccessToken> GetTokenAsync(bool allowInteractive, CancellationToken cancellationToken) => throw new AssertFailedException("No login expected.");
        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<SourceSnapshot> GetSnapshotAsync(RepositorySource source, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) => throw new AssertFailedException("No scan expected.");
        public PackageScanResult Scan(SourceSnapshot snapshot, AnalysisRules rules, CancellationToken cancellationToken) => throw new AssertFailedException("No scan expected.");
        public Task<NuGetCandidate> GetCandidateAsync(string packageId, CancellationToken cancellationToken) => throw new AssertFailedException("No query expected.");
        public Task<UpdateResult> ExecuteAsync(ChangePlan approvedPlan, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) => throw new AssertFailedException("No remote writes expected.");
        public Task WriteAsync(AnalysisResult value, string outputPath, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) =>
            new ExcelReportWriter().WriteAsync(value, outputPath, progress, cancellationToken);
        public Task WriteAsync(AnalysisResult value, string outputPath, IProgress<OperationProgress>? progress, CancellationToken cancellationToken, ReportSelection? selection)
        {
            ExportedSelection = selection;
            return new ExcelReportWriter().WriteAsync(value, outputPath, progress, cancellationToken, selection);
        }
        public string? ChooseReportPath(string? directory) => path;
        public bool Approve(ChangePlan plan) { ReviewedPlan = plan; return true; }
        public void ShowSettings(Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels.MainViewModel model) => throw new AssertFailedException("No dialog expected.");
    }

    [TestMethod]
    public async Task ExportsEightSheetsAllRowsCandidatesAndProvenance()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        var emulator = TestData.Repo("emulator", RepositoryCategory.ICEmulator);
        var analysis = TestData.Analyze(
            TestData.Inventory(platform, TestData.Usage(platform, "shared", "1.0"), TestData.Usage(platform, "shared", "2.0")),
            TestData.Inventory(ic, TestData.Usage(ic, "shared", "1.0"),
                TestData.Usage(ic, "soup", "1.0", TestClassification.Test, "src/Test.One.csproj"),
                TestData.Usage(ic, "soup", "2.0", TestClassification.Production)),
            TestData.Inventory(emulator, TestData.Usage(emulator, "soup", "9.0")));
        analysis = analysis with { Packages = [.. analysis.Packages.Select(package => package.PackageId == "soup"
            ? package with { NuGetCandidate = new("soup", NuGetVersion.Parse("3.0.0-rc.1"), CandidateStatus.Available,
                "https://api.nuget.org/v3/index.json", DateTimeOffset.UtcNow) } : package)] };
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "report.xlsx");
            await new ExcelReportWriter().WriteAsync(analysis, path, null, default);
            using var workbook = new XLWorkbook(path);
            CollectionAssert.AreEqual(new[] { "Platform References", "Platform Packages", "IC References", "IC Alignment",
                "IC SOUP", "IC Emulator", "Scan Sources", "Scan Issues" }, workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
            var soup = workbook.Worksheet("IC SOUP");
            Assert.AreEqual(3, soup.LastRowUsed()!.RowNumber());
            Assert.AreEqual("False", Value(soup, 2, "TEST Only"));
            Assert.AreEqual("2.0.0", Value(soup, 2, "ICMax"));
            foreach (var name in new[] { "IC References", "IC Alignment", "IC SOUP" })
            {
                var sheet = workbook.Worksheet(name);
                foreach (var row in sheet.RowsUsed().Skip(1).Where(row => Value(sheet, row.RowNumber(), "Package") == "soup"))
                {
                    var number = row.RowNumber();
                    Assert.AreEqual("", Value(sheet, number, "NuGetMax"));
                    Assert.AreEqual("Unavailable", Value(sheet, number, "NuGet Action"));
                    Assert.AreEqual("Excluded prerelease", Value(sheet, number, "NuGet Status"));
                    Assert.AreEqual("https://api.nuget.org/v3/index.json", Value(sheet, number, "NuGet Source"));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(Value(sheet, number, "NuGet Queried At")));
                    Assert.AreEqual("", Value(sheet, number, "Selected Target"));
                }
            }
            Assert.AreEqual("AwaitingSelection", Value(soup, 2, "Action"));
            Assert.AreEqual("1.0", Value(soup, 2, "Original Version"));
            Assert.AreEqual(analysis.ManifestHash, Value(soup, 2, "Manifest Hash"));
            Assert.AreEqual(new string('a', 40), Value(soup, 2, "Commit"));
            Assert.AreEqual("True", Value(workbook.Worksheet("Platform References"), 3, "Is PlatformMax"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task PartialRowsAndFormulaLikeStringsRemainLiteral()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic") with { Name = "=HYPERLINK(\"bad\")" };
        var inventory = TestData.Inventory(ic, TestData.Usage(ic, "=evil", "unresolved")) with
            { Status = InventoryStatus.Unresolved, Issues = [new("=code", "+bad", "src/x.csproj")] };
        var analysis = TestData.Analyze(TestData.Inventory(platform, TestData.Usage(platform, "one", "1.0")), inventory);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            await new ExcelReportWriter().WriteAsync(analysis, path, null, default);
            using var workbook = new XLWorkbook(path);
            var references = workbook.Worksheet("IC References");
            Assert.AreEqual("=evil", Value(references, 2, "Package"));
            Assert.AreEqual("False", Value(workbook.Worksheet("Platform References"), 2, "Is PlatformMax"));
            Assert.AreEqual(2, workbook.Worksheet("Scan Issues").LastRowUsed()!.RowNumber());
            Assert.IsFalse(workbook.Worksheets.SelectMany(sheet => sheet.CellsUsed()).Any(cell => cell.HasFormula));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task CancelledExportDoesNotReplaceExistingFileOrLeaveTemp()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "report.xlsx");
        await File.WriteAllTextAsync(path, "existing");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => new ExcelReportWriter()
                .WriteAsync(TestData.Analyze(), path, null, cancellation.Token));
            Assert.AreEqual("existing", await File.ReadAllTextAsync(path));
            Assert.AreEqual(1, Directory.GetFiles(directory).Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task TemporarySharingLockIsRetriedAtomically()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "report.xlsx");
        await File.WriteAllTextAsync(path, "existing");
        var generated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? writing = null;
        try
        {
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                writing = new ExcelReportWriter().WriteAsync(TestData.Analyze(), path,
                    new ProgressSink(value => { if (value.Detail == "Scan Issues") generated.TrySetResult(); }), default);
                await generated.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Delay(300);
                Assert.IsFalse(writing.IsFaulted);
            }
            await writing;
            using var workbook = new XLWorkbook(path);
            Assert.HasCount(8, workbook.Worksheets);
            Assert.AreEqual(1, Directory.GetFiles(directory).Length);
        }
        finally
        {
            if (writing is not null) try { await writing; } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task CancellationDuringGenerationKeepsDestinationAndCleansTemp()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "report.xlsx");
        await File.WriteAllTextAsync(path, "existing");
        using var cancellation = new CancellationTokenSource();
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => new ExcelReportWriter().WriteAsync(TestData.Analyze(), path,
                new ProgressSink(_ => cancellation.Cancel()), cancellation.Token));
            Assert.AreEqual("existing", await File.ReadAllTextAsync(path));
            Assert.AreEqual(1, Directory.GetFiles(directory).Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InternalCustomTargetsExportIdenticallyBeforeAndAfterReview(bool approved)
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        var other = TestData.Repo("other");
        var analysis = TestData.Analyze(TestData.Inventory(platform),
            TestData.Inventory(ic, TestData.Usage(ic, "elekta.Internal", "5.0"), TestData.Usage(ic, "elekta.Unselected", "1.0")),
            TestData.Inventory(other, TestData.Usage(other, "elekta.Internal", "9.0")));
        var report = new ReportSelection(analysis.Id, [new("elekta.Internal", TargetSource.Custom, "0.5.0-rc.1")],
            [new(ic.Id, "elekta.Internal")]);
        var plan = new ChangePlanner().Create(analysis, report.Selections, report.Targets);
        if (approved) plan = plan.Approve(analysis, "reviewer", DateTimeOffset.UtcNow);
        if (approved) report = report with { ReviewPlan = plan };
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        var planPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            var writer = new ExcelReportWriter();
            await writer.WriteAsync(analysis, path, null, default, report);
            await writer.WriteAsync(analysis, plan, planPath, null, default);
            using var workbook = new XLWorkbook(path);
            using var reviewed = new XLWorkbook(planPath);
            foreach (var name in new[] { "IC References", "IC Alignment" })
            {
                var sheet = workbook.Worksheet(name);
                foreach (var row in sheet.RowsUsed().Skip(1))
                {
                    var number = row.RowNumber();
                    var hasTarget = Value(sheet, number, "Package") == "elekta.Internal";
                    Assert.AreEqual(hasTarget ? "0.5.0-rc.1" : "", Value(sheet, number, "Selected Target"));
                    Assert.AreEqual(hasTarget ? "Custom" : "", Value(sheet, number, "Target Source"));
                    Assert.AreEqual(hasTarget ? "Downgrade" : "NotApplicable", Value(sheet, number, "Action"));
                    var isSelected = hasTarget && Value(sheet, number, "Repository ID") == "ic";
                    Assert.AreEqual(isSelected ? approved ? "Approved" : "AwaitingApproval" : "Not selected", Value(sheet, number, "Approval Status"));
                    Assert.AreEqual(Value(sheet, number, "Selected Target"), Value(reviewed.Worksheet(name), number, "Selected Target"));
                    Assert.AreEqual(Value(sheet, number, "Target Source"), Value(reviewed.Worksheet(name), number, "Target Source"));
                }
            }
            Assert.AreEqual(1, workbook.Worksheet("IC SOUP").LastRowUsed()!.RowNumber());
        }
        finally { File.Delete(path); File.Delete(planPath); }
    }

    [TestMethod]
    public async Task NonSoupRowsDoNotShowICOrNuGetCandidateMaxima()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        var analysis = TestData.Analyze(TestData.Inventory(platform, TestData.Usage(platform, "shared", "2.0")),
            TestData.Inventory(ic, TestData.Usage(ic, "shared", "1.0"), TestData.Usage(ic, "elekta.Internal", "5.0"),
                TestData.Usage(ic, "soup", "3.0-rc.1")));
        analysis = analysis with { Packages = [.. analysis.Packages.Select(package => package with
            { NuGetCandidate = new(package.PackageId, NuGetVersion.Parse("4.0"), CandidateStatus.Available, "nuget.org", DateTimeOffset.UtcNow) })] };
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            await new ExcelReportWriter().WriteAsync(analysis, path, null, default);
            using var workbook = new XLWorkbook(path);
            foreach (var name in new[] { "Platform References", "Platform Packages", "IC References", "IC Alignment" })
            {
                var sheet = workbook.Worksheet(name);
                foreach (var row in sheet.RowsUsed().Skip(1))
                {
                    var number = row.RowNumber();
                    if (Value(sheet, number, "Ownership") == "Soup")
                    {
                        Assert.AreEqual("3.0.0-rc.1", Value(sheet, number, "ICMax"));
                        Assert.AreEqual("4.0.0", Value(sheet, number, "NuGetMax"));
                        continue;
                    }
                    foreach (var header in new[] { "ICMax", "IC Complete", "NuGetMax", "NuGet Status", "NuGet Source", "NuGet Queried At", "NuGet Detail" })
                        Assert.AreEqual("", Value(sheet, number, header), $"{name}: {header}");
                    Assert.AreEqual("NotApplicable", Value(sheet, number, "ICMax Action"));
                    Assert.AreEqual("NotApplicable", Value(sheet, number, "NuGet Action"));
                }
            }
        }
        finally { File.Delete(path); }
    }

    private sealed class ProgressSink(Action<OperationProgress> report) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => report(value);
    }

    private static string Value(IXLWorksheet sheet, int row, string header) =>
        sheet.Cell(row, sheet.Row(1).CellsUsed().Single(cell => cell.GetString() == header).Address.ColumnNumber).GetString();
}