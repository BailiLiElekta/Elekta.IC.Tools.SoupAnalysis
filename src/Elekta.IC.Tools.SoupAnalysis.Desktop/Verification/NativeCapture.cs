using System.Diagnostics;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels;
using Elekta.IC.Tools.SoupAnalysis.Desktop.Views;
using Elekta.IC.Tools.SoupAnalysis.Domain;

namespace Elekta.IC.Tools.SoupAnalysis.Desktop.Verification;

internal sealed class BindingErrorListener : TraceListener
{
    public List<string> Errors { get; } = [];
    public override void Write(string? message) { }
    public override void WriteLine(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
}

internal static class NativeCapture
{
    public static async Task CaptureAsync(Window window, string path, string scenario, BindingErrorListener bindings, IReadOnlyList<string> checks)
    {
        var allChecks = checks.ToList();
        CheckApplicationIcon(window, allChecks);
        await CheckTablePresentationAsync(window, allChecks);
        if (scenario == "analysis")
        {
            window.Width = Math.Max(window.Width, 1800);
            try { await CheckPreviewTargetPresentationAsync(window, allChecks); }
            catch (Exception exception) { throw new InvalidOperationException($"Native preview verification: {exception}", exception); }
        }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.Render);
        var dpi = VisualTreeHelper.GetDpi(window);
        var content = window.Content as FrameworkElement ?? throw new InvalidOperationException("Window content is missing.");
        var width = (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY);
        if (width < 800 || height < 500) throw new InvalidOperationException("Smoke window is too small.");
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var colors = new HashSet<int>();
        var visiblePixels = 0;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset + 3] > 0) visiblePixels++;
            colors.Add(BitConverter.ToInt32(pixels, offset));
        }
        if (visiblePixels < width * height / 2 || colors.Count < 20) throw new InvalidOperationException("Native window capture is blank.");
        if (bindings.Errors.Count > 0) throw new InvalidOperationException("WPF binding errors: " + string.Join("\n", bindings.Errors));
        bitmap.Freeze();
        var bytes = await Task.Run(() =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var memory = new MemoryStream();
            encoder.Save(memory);
            return memory.ToArray();
        });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllBytesAsync(path, bytes);
        if (File.Exists(path + ".error.txt")) File.Delete(path + ".error.txt");
        await File.WriteAllTextAsync(path + ".verification.json", JsonSerializer.Serialize(new
        {
            Scenario = scenario, Width = width, Height = height, Dpi = dpi.PixelsPerInchX,
            Colors = colors.Count, VisiblePixels = visiblePixels, BindingErrors = bindings.Errors, Checks = allChecks,
            RemoteOperations = "Disabled by smoke ports"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CheckApplicationIcon(Window window, List<string> checks)
    {
        if (window.Icon is null) throw new InvalidOperationException("Application title/taskbar icon is missing.");
        if (window.FindName("ApplicationLogo") is not Image { Source: DrawingImage, Width: 36, Height: 36 })
            throw new InvalidOperationException("Application header must display the vector SOUP logo at 36 DIPs.");
        var resource = System.Windows.Application.GetResourceStream(new Uri(
            "pack://application:,,,/Elekta.IC.Tools.SoupAnalysis.Desktop;component/Assets/SoupAnalysis.ico"))
            ?? throw new InvalidOperationException("Application icon resource is missing.");
        using var stream = resource.Stream;
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        int[] expectedSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        if (!decoder.Frames.Select(frame => frame.PixelWidth).Order().SequenceEqual(expectedSizes) ||
            decoder.Frames.Any(frame => frame.PixelWidth != frame.PixelHeight))
            throw new InvalidOperationException("Application icon must contain all nine square 16-256px DPI variants.");
        var settings = new SettingsWindow();
        if (settings.Icon is null) throw new InvalidOperationException("Settings window must inherit the application icon.");
        checks.Add("Main and Settings windows have application icons; header uses a 36-DIP vector logo");
        checks.Add("Embedded ICO contains 16, 20, 24, 32, 40, 48, 64, 128 and 256px frames");
    }

    private static async Task CheckTablePresentationAsync(Window window, List<string> checks)
    {
        var model = (MainViewModel)window.DataContext;
        var originalTab = model.SelectedTab;
        var originalRepository = model.SelectedAnalysisRepository;
        var originalChangeRepository = model.SelectedChangeRepository;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Native table check failed: " + name);
            checks.Add(name);
        }
        async Task LayoutAsync()
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }
        for (var tab = 0; tab < 3; tab++)
        {
            model.SelectedTab = tab;
            await LayoutAsync();
            var summary = window.FindName("ConfigurationSummary") as TextBlock;
            Check(summary is { IsVisible: true } &&
                new System.Windows.Documents.TextRange(summary.ContentStart, summary.ContentEnd).Text ==
                    $"Analysis For: {model.AnalysisFor} | Platform Config Version: {model.PlatformConfigVersion}",
                $"Tab {tab} displays the loaded release and Platform configuration version");
            var cells = Descendants<DataGridCell>(window).Where(cell => cell.IsVisible && cell.ActualWidth > 24).ToArray();
            if (tab > 0 && model.Analysis is null)
                Check(Descendants<DataGrid>(window).Any(grid => grid.IsVisible && grid.ActualWidth > 24),
                    $"Tab {tab} renders an empty-state table before scanning");
            else
                Check(cells.Length > 0, $"Tab {tab} renders native table cells");
            foreach (var cell in cells) CheckCellPadding(cell);
            if (cells.Length > 0)
                Check(cells.All(cell => cell.Padding == new Thickness(10, 4, 10, 4)), $"Tab {tab} applies global padded cell template to text and controls");
        }
        var log = Descendants<Expander>(window).Single(expander => Equals(expander.Header, "Operation Log"));
        log.IsExpanded = true;
        await LayoutAsync();
        var logCells = Descendants<DataGrid>(log).SelectMany(grid => Descendants<DataGridCell>(grid)).Where(cell => cell.IsVisible).ToArray();
        Check(logCells.Length > 0, "Operation log renders native cells");
        foreach (var cell in logCells) CheckCellPadding(cell);
        checks.Add("Operation log uses actual padded content presenter offsets");
        log.IsExpanded = false;
        if (model.Analysis is not null)
        {
            model.SelectedTab = 1;
            model.SelectedAnalysisRepository = model.AnalysisRepositoryChoices.First(choice => choice.Category == RepositoryCategory.IC);
            await LayoutAsync();
            var view = Descendants<AnalysisView>(window).Single();
            var grid = Descendants<DataGrid>(view).Single(table => table.Columns.Any(column => Equals(column.Header, "Target Choice")));
            var headers = grid.Columns.Select(column => column.Header?.ToString()).ToArray();
            Check(Array.IndexOf(headers, "TEST Only") == Array.IndexOf(headers, "Source / Ownership") + 1 &&
                Array.IndexOf(headers, "Current Versions") == Array.IndexOf(headers, "TEST Only") + 1,
                "TEST Only column is exactly between ownership and current versions");
            Check(!Descendants<CheckBox>(view).Any(box => BindingOperations.GetBindingExpression(box, CheckBox.IsCheckedProperty)?.ParentBinding.Path?.Path == "TestOnlyFilter"),
                "Analysis has no TEST Only filter checkbox");
            Check(headers.Contains("NuGetMax (Formal releases)"), "NuGetMax header labels formal releases explicitly");
            foreach (var ownership in new[] { PackageOwnership.SharedWithPlatform, PackageOwnership.Soup, PackageOwnership.Internal })
            {
                var row = model.PackagesView.Cast<PackageRow>().FirstOrDefault(package => package.Decision.Ownership == ownership)
                    ?? throw new InvalidOperationException($"Native fixture is missing {ownership} row.");
                grid.ScrollIntoView(row, grid.Columns.Single(column => Equals(column.Header, "Target Choice")));
                await LayoutAsync();
                var cell = Descendants<DataGridCell>(grid).SingleOrDefault(item => ReferenceEquals(item.DataContext, row) && Equals(item.Column.Header, "Target Choice"))
                    ?? throw new InvalidOperationException($"Native target cell not realized for {ownership}.");
                CheckCellPadding(cell);
                var inputs = Descendants<TextBox>(cell).Where(input => BindingOperations.GetBindingExpression(input, TextBox.TextProperty)?.ParentBinding.Path?.Path == "CustomVersionText").ToArray();
                var combos = Descendants<ComboBox>(cell).ToArray();
                Check(ownership == PackageOwnership.Internal ? inputs.Length == 1 && combos.Length == 0
                    : ownership == PackageOwnership.Soup ? inputs.Length == 0 && combos.Length == 1 : inputs.Length == 0 && combos.Length == 0,
                    $"{ownership} target uses its exclusive native control template");
                Check(combos.Cast<Control>().Concat(inputs).All(control => control.ActualHeight >= 28 && control.ActualHeight <= cell.ActualHeight - 8),
                    $"{ownership} target control fits padded 42 DIP row");
                if (ownership == PackageOwnership.Internal)
                {
                    inputs.Single().Text = "2.0.0-preview.1";
                    Check(row.SelectedTarget?.Source == TargetSource.Custom && row.Target == "2.0.0-preview.1", "Native internal TextBox binding updates global custom target immediately");
                    inputs.Single().Text = "[1.0,2.0)";
                    Check(row.SelectedTarget is null && row.TargetError is not null, "Native invalid input clears target rather than retaining last valid version");
                    inputs.Single().Text = "2.0.0-preview.1";
                }
            }
            await LayoutAsync();
            grid.ScrollIntoView(grid.Items[0], grid.Columns.Single(column => Equals(column.Header, "Source / Ownership")));
            await LayoutAsync();
            var selectedCell = Descendants<DataGridCell>(grid).First(cell => cell.IsVisible && Equals(cell.Column.Header, "Source / Ownership"));
            grid.SelectedItem = selectedCell.DataContext;
            selectedCell.UpdateLayout();
            var surface = (Border)selectedCell.Template.FindName("CellSurface", selectedCell);
            Check(Equals(surface.BorderBrush, window.FindResource("AccentBrush")) && selectedCell.Foreground == window.FindResource("TextBrush"),
                "Selected cell retains readable text and visible cyan border");
            selectedCell.Focus();
            await LayoutAsync();
            Check(selectedCell.IsKeyboardFocusWithin && Equals(surface.BorderBrush, window.FindResource("TextBrush")),
                "Keyboard-focused cell has contrasting visible border");
            foreach (var category in new[] { RepositoryCategory.Platform, RepositoryCategory.ICEmulator })
            {
                model.SelectedAnalysisRepository = model.AnalysisRepositoryChoices.First(choice => choice.Category == category);
                await LayoutAsync();
                Check(!Descendants<ComboBox>(grid).Any(combo => combo.IsVisible) && !Descendants<TextBox>(grid).Any(input => input.IsVisible),
                    $"{category} target presentation is readonly with no version editor");
            }
            model.SelectedAnalysisRepository = originalRepository;
            model.SelectedPackage = null;
            grid.SelectedItem = null;
            grid.ScrollIntoView(grid.Items[0], grid.Columns[0]);

            model.SelectedTab = 2;
            await LayoutAsync();
            var changes = Descendants<ChangeReviewView>(window).Single();
            var selector = Descendants<ComboBox>(changes).Single(combo =>
                BindingOperations.GetBindingExpression(combo, ComboBox.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.SelectedChangeRepository));
            var changeGrid = Descendants<DataGrid>(changes).Single(table => table.Columns.Any(column => Equals(column.Header, "Package")));
            var choice = model.RepositoryChoices.First();
            selector.SelectedItem = choice;
            await LayoutAsync();
            Check(ReferenceEquals(model.SelectedChangeRepository, choice) && changeGrid.Items.Cast<ReviewRow>().All(row => row.Repository.Id == choice.Source.Id),
                "Native repository-name selector binds the Changes table strictly by repository ID");
            Check(!Descendants<ItemsControl>(changes).Any(control => control is not ComboBox &&
                BindingOperations.GetBindingExpression(control, ItemsControl.ItemsSourceProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.RepositoryChoices)),
                "Changes has no repository checkbox renderer");
            Check(Descendants<CheckBox>(changes).Where(box => box.IsVisible).All(box => box.DataContext is ReviewRow),
                "Changes checkboxes belong only to package rows");
            var bulkButtons = Descendants<Button>(changes).Where(button => button.Command is not null).ToArray();
            var selectAll = bulkButtons.Single(button => ReferenceEquals(button.Command, model.SelectAllPackagesCommand));
            var upgrades = bulkButtons.Single(button => ReferenceEquals(button.Command, model.SelectUpgradesCommand));
            var clear = bulkButtons.Single(button => ReferenceEquals(button.Command, model.ClearPackageSelectionCommand));
            void Click(Button button) => ((System.Windows.Automation.Provider.IInvokeProvider)
                new System.Windows.Automation.Peers.ButtonAutomationPeer(button).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
            Click(selectAll);
            await LayoutAsync();
            Check(changeGrid.Items.Cast<ReviewRow>().Where(row => row.Eligible).All(row => row.IsSelected) &&
                changeGrid.Items.Cast<ReviewRow>().Where(row => !row.Eligible).All(row => !row.IsSelected),
                "Native Select All button replaces selection with all eligible current-repository packages");
            var savedSelections = changeGrid.Items.Cast<ReviewRow>().Where(row => row.IsSelected).ToArray();
            var analysisSelection = model.SelectedAnalysisRepository;
            selector.SelectedItem = model.RepositoryChoices.Last();
            await LayoutAsync();
            Check(changeGrid.Items.Cast<ReviewRow>().All(row => row.Repository.Id == model.SelectedChangeRepository!.Source.Id) &&
                ReferenceEquals(model.SelectedAnalysisRepository, analysisSelection) && savedSelections.All(row => row.IsSelected),
                "Native Changes browsing updates scoped rows without changing Analysis or saved selections");
            Click(upgrades);
            await LayoutAsync();
            Check(changeGrid.Items.Cast<ReviewRow>().All(row => row.IsSelected == (row.Eligible && row.Action == "Upgrade")),
                "Native Select Upgrades chooses only upgrade actions in the displayed repository");
            Click(clear);
            await LayoutAsync();
            Check(changeGrid.Items.Cast<ReviewRow>().All(row => !row.IsSelected) && savedSelections.All(row => row.IsSelected),
                "Native Clear Selection leaves hidden repository selections intact");
            selector.SelectedItem = originalChangeRepository;
            Click(selectAll);
            await LayoutAsync();
            Check(model.ChangeTableTitle.Contains($"{changeGrid.Items.Cast<ReviewRow>().Count(row => row.IsSelected)} selected", StringComparison.Ordinal),
                "Native scoped table header immediately reports the selected package count");
            Check(Descendants<DataGridCell>(changeGrid).Where(cell => cell.IsVisible).All(cell => cell.Padding == new Thickness(10, 4, 10, 4) && Math.Abs(cell.ActualHeight - 42) < 1),
                "Changes selection controls retain actual 10,4 padding and stable 42 DIP rows");
        }
        model.SelectedTab = originalTab;
        await LayoutAsync();
    }

    private static async Task CheckPreviewTargetPresentationAsync(Window window, List<string> checks)
    {
        var model = (MainViewModel)window.DataContext;
        var original = model.Analysis ?? throw new InvalidOperationException("Analysis fixture is missing.");
        var local = original.OriginalRefs.First(source => source.Category == RepositoryCategory.IC);
        var failed = original.OriginalRefs.Last(source => source.Category == RepositoryCategory.IC);
        var soupIds = original.Packages.Where(package => package.IsSoup).Select(package => package.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var localSoup = original.Inventories.Single(inventory => inventory.Source.Id == local.Id).Usages
            .Where(usage => soupIds.Contains(usage.PackageId)).Select(usage => usage.PackageId).Distinct().ToArray();
        var inventories = original.Inventories.Select(inventory => inventory.Source.Id == failed.Id || inventory.Source.Category == RepositoryCategory.ICEmulator
            ? inventory with { Status = InventoryStatus.Failed, Snapshot = null, Usages = [], Issues = [new("offline-source-failed", "Offline fixture source unavailable")] }
            : inventory.Source.Id == local.Id ? inventory with { Usages = [.. inventory.Usages.Select(usage => !soupIds.Contains(usage.PackageId) ? usage : usage with
                { PackageId = usage.PackageId == localSoup[0] ? "Microsoft.AspNetCore.SignalR.Client" : "Microsoft.AspNetCore.SignalR.Protocols.NewtonsoftJson", RawVersion = "10.0.12" })] }
            : inventory).ToImmutableArray();
        var analysis = AlignmentAnalyzer.Analyze(Guid.NewGuid(),
            new(1, original.ManifestVersion, [.. original.OriginalRefs.Where(source => source.Category == RepositoryCategory.Platform)]),
            original.ManifestHash, original.OriginalRefs, inventories, DateTimeOffset.UtcNow);
        analysis = analysis with { Packages = [.. analysis.Packages.Select(package => !package.IsSoup ? package : package with
            { NuGetCandidate = new(package.PackageId, null, CandidateStatus.Failed, "OFFLINE FIXTURE (not queried)", DateTimeOffset.UtcNow, "NuGet HTTP failure: Found (302).") })] };
        await model.SetAnalysisAsync(analysis);
        model.SelectedTab = 1;
        model.ShowAllScanIssues = false;
        async Task LayoutAsync()
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Native availability check failed: " + name);
            checks.Add(name);
        }
        await LayoutAsync();
        var view = Descendants<AnalysisView>(window).Single();
        var grid = Descendants<DataGrid>(view).Single(table => table.Columns.Any(column => Equals(column.Header, "Target Choice")));
        Check(analysis.CompletePlatform && !analysis.CompleteIC && inventories.Single(inventory => inventory.Source.Id == local.Id).IsComplete,
            "Offline availability fixture has complete local inventory and Platform but incomplete global IC authority");
        Check(analysis.OriginalRefs.SequenceEqual(original.OriginalRefs) && model.CanSelectChangePackages
            && !model.CanReview && !model.CanExecute && !analysis.CanUpdate,
            "Offline fixture preserves source identities and local selection while smoke and incomplete baseline retain approval/execution guards");
        Check(model.GlobalBaselineBlockReasons.Contains(failed.Name, StringComparison.Ordinal) &&
            !model.GlobalBaselineBlockReasons.Contains("elekta.ic.emulator", StringComparison.Ordinal), "Header tooltip names failed normal IC sources but excludes Emulator blockers");
        foreach (var row in model.PackagesView.Cast<PackageRow>().Where(package => package.Decision.IsSoup).ToArray())
        {
            grid.ScrollIntoView(row, grid.Columns.Single(column => Equals(column.Header, "Target Choice")));
            await LayoutAsync();
            var cell = Descendants<DataGridCell>(grid).Single(item => ReferenceEquals(item.DataContext, row) && Equals(item.Column.Header, "Target Choice"));
            CheckCellPadding(cell);
            Check(row.Target == "Awaiting selection" && row.ICMax == "10.0.12 (provisional)" && row.SelectedTarget is null,
                $"{row.PackageId} offers a known provisional maximum without automatically choosing it");
            var combo = Descendants<ComboBox>(cell).Single();
            Check(combo.IsEnabled && combo.IsReadOnly && Math.Abs(cell.ActualHeight - 42) < 1,
                $"{row.PackageId} native draft selector stays source-only and fits the padded 42 DIP row");
            combo.SelectedItem = row.Choices.Single(choice => choice.Source == TargetSource.ICMax);
            await LayoutAsync();
            Check(row.SelectedTarget?.IsProvisional == true && row.Target == "10.0.12" && row.Action == "Aligned" &&
                row.SelectedTarget.Label.Contains("provisional - preview only", StringComparison.Ordinal),
                $"{row.PackageId} real ComboBox binding selects ICMax as a draft only");
            grid.ScrollIntoView(row, grid.Columns.Single(column => Equals(column.Header, "Target Choice")));
            await LayoutAsync();
            cell = Descendants<DataGridCell>(grid).Single(item => ReferenceEquals(item.DataContext, row) && Equals(item.Column.Header, "Target Choice"));
            var host = Descendants<ContentControl>(cell).Single(control => ReferenceEquals(control.Content, row));
            Check(Equals(host.ToolTip, row.TargetTooltip) && row.TargetTooltip.Contains("Approval and execution", StringComparison.Ordinal)
                && row.CandidateStatus.Contains("Found (302)", StringComparison.Ordinal), $"{row.PackageId} preview warning retains failed metadata provenance");
        }
        var internalRow = model.PackagesView.Cast<PackageRow>().Single(row => row.Decision.Ownership == PackageOwnership.Internal);
        grid.ScrollIntoView(internalRow, grid.Columns.Single(column => Equals(column.Header, "Target Choice")));
        await LayoutAsync();
        var input = Descendants<TextBox>(grid).Single(control => ReferenceEquals(control.DataContext, internalRow) &&
            BindingOperations.GetBindingExpression(control, TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(PackageRow.CustomVersionText));
        input.Text = "25.";
        Check(internalRow.SelectedTarget is null && internalRow.TargetError is not null && internalRow.Action == "Unresolved",
            "Native incomplete Internal input stays invalid and clears the previous target");
        input.Text = "25.1.3";
        Check(internalRow.SelectedTarget?.Source == TargetSource.Custom && internalRow.Action == "Upgrade" &&
            model.ReviewRows.Single(row => row.Repository.Id == local.Id && row.PackageId == internalRow.PackageId).NewVersion == "25.1.3",
            "Native exact Internal input immediately updates the current Changes row and action");
        var issuesTab = Descendants<TabItem>(view).Single(tab => Equals(tab.Header, "Completeness / Issues"));
        issuesTab.IsSelected = true;
        await LayoutAsync();
        var toggle = Descendants<CheckBox>(view).Single(box => BindingOperations.GetBindingExpression(box, CheckBox.IsCheckedProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.ShowAllScanIssues));
        var scopedRepository = model.SelectedAnalysisRepository;
        Check(model.IssuesView.Cast<IssueRow>().Count() == 2 && model.IssuesView.Cast<IssueRow>().All(issue => issue.RepositoryId == local.Id && !issue.BlocksUpdates && issue.Code == "NuGetCandidateFailed"),
            "Complete current repository shows nonblocking metadata issues by source ID while hiding cross-repository source failures");
        ((System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.CheckBoxAutomationPeer(toggle)
            .GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
        await LayoutAsync();
        Check(model.ShowAllScanIssues && model.IssuesView.Cast<IssueRow>().Any(issue => issue.RepositoryId == failed.Id) &&
            ReferenceEquals(model.SelectedAnalysisRepository, scopedRepository) && ReferenceEquals(model.Analysis, analysis) && !model.IsAnalysisStale,
            "Native issue toggle exposes other repository IDs without changing repository scope or snapshot");
        issuesTab.IsSelected = false;
        Descendants<TabItem>(view).Single(tab => Equals(tab.Header, "Package Comparison")).IsSelected = true;
        model.ShowAllScanIssues = false;
        model.SelectedPackage = null;
        model.SelectedTab = 2;
        await LayoutAsync();
        var changes = Descendants<ChangeReviewView>(window).Single();
        void Click(Button button) => ((System.Windows.Automation.Provider.IInvokeProvider)
            new System.Windows.Automation.Peers.ButtonAutomationPeer(button).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        var selectUpgrades = Descendants<Button>(changes).Single(button => ReferenceEquals(button.Command, model.SelectUpgradesCommand));
        var selectAll = Descendants<Button>(changes).Single(button => ReferenceEquals(button.Command, model.SelectAllPackagesCommand));
        Click(selectUpgrades);
        await LayoutAsync();
        Check(model.VisibleReviewRows.Cast<ReviewRow>().Count(row => row.IsSelected) == 2 &&
            model.VisibleReviewRows.Cast<ReviewRow>().Where(row => row.IsSelected).All(row => row.Action == "Upgrade"),
            "Native partial-baseline Select Upgrades selects locked Shared and valid Internal upgrades only");
        Click(selectAll);
        await LayoutAsync();
        var selected = model.VisibleReviewRows.Cast<ReviewRow>().Where(row => row.IsSelected).ToArray();
        Check(selected.Length == 4 && selected.All(row => row.PreviewEligible) && !model.CanExecute,
            "Native Select All includes aligned draft SOUP targets but never approves or executes");
        var preview = new ChangePlanner().CreatePreview(analysis,
            [.. selected.Select(row => new RepositoryPackageSelection(row.Repository.Id, row.PackageId))],
            [.. selected.Select(row => new PackageTargetSelection(row.PackageId, row.Package.SelectedTarget!.Source,
                row.Package.SelectedTarget.Source == TargetSource.Custom ? row.Package.SelectedTarget.Version.ToNormalizedString() : null))]);
        Check(preview.Status == ChangePlanStatus.PreviewOnly && !preview.CanExecute && preview.MatchesSnapshot(analysis),
            "Native offline evidence constructs a snapshot-bound PreviewOnly plan with no approval");
        var reviewButton = Descendants<Button>(changes).Single(button => ReferenceEquals(button.Command, model.ReviewCommand));
        Check(Equals(reviewButton.Content, "Review Preview") && !reviewButton.IsEnabled,
            "Native dynamic Review Preview label renders while smoke-mode review remains disabled");
        model.Status = "SMOKE TEST | OFFLINE 14-SOURCE FIXTURE | Selection and preview available; approval/execution blocked | No network or remote writes";
        model.ReviewStatus = "SMOKE TEST | Preview ready; not approved; IC baseline incomplete; failed NuGet HTTP 302. New complete scan required for approval.";
        await LayoutAsync();
    }

    private static void CheckCellPadding(DataGridCell cell)
    {
        if (cell.Template.FindName("CellSurface", cell) is not Border border || border.Padding != cell.Padding ||
            border.Child is not ContentPresenter presenter)
            throw new InvalidOperationException("Cell does not use the reusable padded border template.");
        var origin = presenter.TransformToAncestor(cell).Transform(new Point());
        if (origin.X < 10 || origin.Y < 4 || origin.X + presenter.ActualWidth > cell.ActualWidth - 9 ||
            origin.Y + presenter.ActualHeight > cell.ActualHeight - 3)
            throw new InvalidOperationException("Cell content does not have actual horizontal and vertical padding.");
    }

    private static IEnumerable<TElement> Descendants<TElement>(DependencyObject root) where TElement : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is TElement element) yield return element;
            foreach (var descendant in Descendants<TElement>(child)) yield return descendant;
        }
    }
}