using ClosedXML.Excel;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using NuGet.Versioning;

namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure.Reporting;

public sealed class ExcelReportWriter : IExcelReportWriter
{
    private static readonly string[] ReferenceHeaders = ["Repository", "Repository ID", "Category", "Package", "Ownership",
        "TEST Only", "Original Version", "Normalized Version", "PlatformMax", "Is PlatformMax", "Platform Complete",
        "ICMax", "IC Complete", "NuGetMax", "NuGet Status", "NuGet Source", "NuGet Queried At", "NuGet Detail",
        "Selected Target", "Target Source", "Approval Status", "Action", "ICMax Action", "NuGet Action",
        "Project", "Assembly", "Condition", "Reference File", "Reference Locator", "Version File", "Version Locator",
        "Declaration Kind", "Property", "Central File", "Unique Owner", "Ref Kind", "Ref", "Commit", "Snapshot At",
        "Scan At", "Analysis ID", "Manifest Version", "Manifest Hash", "Inventory Status"];

    public Task WriteAsync(AnalysisResult analysis, string outputPath, IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken) => WriteCoreAsync(analysis, null, outputPath, progress, cancellationToken);

    public Task WriteAsync(AnalysisResult analysis, string outputPath, IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken, ReportSelection? selection) => WriteCoreAsync(analysis, selection, outputPath, progress, cancellationToken);

    public Task WriteAsync(AnalysisResult analysis, ChangePlan? plan, string outputPath,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken) => WriteCoreAsync(analysis, plan is null ? null
                : new(analysis.Id, plan.Targets, plan.Selections, plan), outputPath, progress, cancellationToken);

    private async Task WriteCoreAsync(AnalysisResult analysis, ReportSelection? selection, string outputPath,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        selection?.Validate(analysis);
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var content = await Task.Run(() => Generate(analysis, selection, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await FileReplacement.ReplaceAsync(temporary, destination, cancellationToken).ConfigureAwait(false);
            progress?.Report(new(OperationStage.Exporting, Detail: "Report written."));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static MemoryStream Generate(AnalysisResult analysis, ReportSelection? selection,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook();
        foreach (var (name, category, soupOnly) in new[]
        {
            ("Platform References", RepositoryCategory.Platform, false),
            ("Platform Packages", RepositoryCategory.Platform, false),
            ("IC References", RepositoryCategory.IC, false),
            ("IC Alignment", RepositoryCategory.IC, false),
            ("IC SOUP", RepositoryCategory.IC, true),
            ("IC Emulator", RepositoryCategory.ICEmulator, false)
        })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sheet = workbook.Worksheets.Add(name);
            Header(sheet, ReferenceHeaders);
            var row = 2;
            foreach (var inventory in analysis.Inventories.Where(item => item.Source.Category == category))
            {
                IEnumerable<PackageUsage> usages = inventory.Usages;
                if (name == "Platform Packages")
                    usages = usages.DistinctBy(usage => (usage.PackageId.ToUpperInvariant(), usage.Version?.ToNormalizedString() ?? usage.RawVersion));
                foreach (var usage in usages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var decision = analysis.Packages.SingleOrDefault(package =>
                        package.PackageId.Equals(usage.PackageId, StringComparison.OrdinalIgnoreCase));
                    if (soupOnly && decision?.Ownership != PackageOwnership.Soup) continue;
                    var selectedTarget = category == RepositoryCategory.IC ? selection?.Targets.FirstOrDefault(target =>
                        target.PackageId.Equals(usage.PackageId, StringComparison.OrdinalIgnoreCase)) : null;
                    var selected = category == RepositoryCategory.IC && selection?.Selections.Any(item => item.RepositoryId == inventory.Source.Id
                        && item.PackageId.Equals(usage.PackageId, StringComparison.OrdinalIgnoreCase)) == true;
                    var testOnly = inventory.Usages.Where(item => item.PackageId.Equals(usage.PackageId, StringComparison.OrdinalIgnoreCase))
                        .All(item => item.TestClassification == TestClassification.Test);
                    var distinctPlatform = analysis.Inventories.Where(item => item.Source.Category == RepositoryCategory.Platform)
                        .SelectMany(item => item.Usages).Where(item => item.PackageId.Equals(usage.PackageId, StringComparison.OrdinalIgnoreCase))
                        .Select(item => item.Version).OfType<NuGetVersion>().Distinct(VersionComparer.VersionRelease).Count() > 1;
                    var isMaximum = category == RepositoryCategory.Platform && distinctPlatform && usage.Version is { } version
                        && decision?.PlatformMax is { } maximum && PackageVersions.Equal(version, maximum);
                    var target = selectedTarget is not null && decision is not null ? selection!.ResolveTarget(decision) : (category == RepositoryCategory.IC && decision?.Ownership == PackageOwnership.SharedWithPlatform
                        && decision.IsPlatformMaxComplete ? decision.PlatformMax : null);
                    var action = category == RepositoryCategory.IC
                        ? target is not null ? AlignmentAnalyzer.GetAction(usage, target)
                            : decision?.Ownership == PackageOwnership.Soup ? AlignmentAction.AwaitingSelection
                            : decision?.Ownership == PackageOwnership.Unconfirmed ? AlignmentAction.Unresolved : AlignmentAction.NotApplicable
                        : AlignmentAction.NotApplicable;
                    var provenance = usage.Declaration;
                    var soup = decision?.Ownership == PackageOwnership.Soup ? decision : null;
                    var stableCandidate = soup?.NuGetCandidate is { IsAvailable: true, Version: { IsPrerelease: false } candidate }
                        ? candidate : null;
                    var candidateStatus = soup?.NuGetCandidate is { IsAvailable: true, Version.IsPrerelease: true }
                        ? "Excluded prerelease" : soup?.NuGetCandidate?.Status.ToString();
                    Row(sheet, row, [inventory.Source.Name, inventory.Source.Id, category, usage.PackageId, decision?.Ownership,
                        testOnly, usage.RawVersion, usage.Version?.ToNormalizedString(), decision?.PlatformMax?.ToNormalizedString(), isMaximum,
                        decision?.IsPlatformMaxComplete, soup?.ICMax?.ToNormalizedString(), soup?.IsICMaxComplete,
                        stableCandidate?.ToNormalizedString(), candidateStatus,
                        soup?.NuGetCandidate?.Source, soup?.NuGetCandidate?.QueriedAt, soup?.NuGetCandidate?.Detail,
                        target?.ToNormalizedString(), selectedTarget?.Source.ToString() ?? (target is null ? "" : "PlatformMax"),
                        !selected ? "Not selected" : selection?.ReviewPlan?.Status == ChangePlanStatus.PreviewOnly || !analysis.CanUpdate
                            ? "Preview only / Not approved" : selection?.ReviewPlan?.CanExecute == true ? "Approved" : "AwaitingApproval", action,
                        soup is null ? AlignmentAction.NotApplicable : CandidateAction(usage, soup.ICMax),
                        soup is null ? AlignmentAction.NotApplicable : CandidateAction(usage, stableCandidate),
                        usage.ProjectPath, usage.AssemblyName, usage.Condition, provenance.ReferenceOwner.SourceFile,
                        provenance.ReferenceOwner.ElementPath, provenance.VersionOwner.SourceFile, provenance.VersionOwner.ElementPath,
                        provenance.Kind, provenance.PropertyName, provenance.CentralFile, provenance.IsUniquelyOwned,
                        inventory.Source.RefKind, inventory.Source.Ref, inventory.Snapshot?.Commit, inventory.Snapshot?.CapturedAt,
                        analysis.ScannedAt, analysis.Id, analysis.ManifestVersion, analysis.ManifestHash, inventory.Status]);
                    if (isMaximum) sheet.Cell(row, 10).Style.Fill.BackgroundColor = XLColor.LightGreen;
                    if (decision?.Ownership == PackageOwnership.Soup) sheet.Cell(row, 5).Style.Fill.BackgroundColor = XLColor.LightYellow;
                    if (decision?.Ownership == PackageOwnership.SharedWithPlatform) sheet.Cell(row, 5).Style.Fill.BackgroundColor = XLColor.LightCyan;
                    row++;
                }
            }
            Finish(sheet, row - 1, ReferenceHeaders.Length);
            progress?.Report(new(OperationStage.Exporting, Detail: name));
        }
        var sources = workbook.Worksheets.Add("Scan Sources");
        string[] sourceHeaders = ["Repository", "Repository ID", "URL", "Category", "Ref Kind", "Ref", "Commit", "Snapshot At",
            "Status", "Complete", "Scan At", "Analysis ID", "Manifest Version", "Manifest Hash"];
        Header(sources, sourceHeaders);
        var sourceRow = 2;
        foreach (var source in analysis.OriginalRefs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inventory = analysis.Inventories.SingleOrDefault(item => item.Source.Id == source.Id);
            Row(sources, sourceRow++, [source.Name, source.Id, source.Url, source.Category, source.RefKind, source.Ref,
                inventory?.Snapshot?.Commit, inventory?.Snapshot?.CapturedAt, inventory?.Status.ToString() ?? "Missing",
                inventory?.IsComplete ?? false, analysis.ScannedAt, analysis.Id, analysis.ManifestVersion, analysis.ManifestHash]);
        }
        Finish(sources, sourceRow - 1, sourceHeaders.Length);
        var issues = workbook.Worksheets.Add("Scan Issues");
        string[] issueHeaders = ["Repository", "Repository ID", "Category", "Code", "Message", "Source File", "Blocks Updates",
            "Scan At", "Manifest Hash"];
        Header(issues, issueHeaders);
        var issueRow = 2;
        foreach (var inventory in analysis.Inventories)
            foreach (var issue in inventory.Issues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Row(issues, issueRow++, [inventory.Source.Name, inventory.Source.Id, inventory.Source.Category, issue.Code,
                    issue.Message, issue.SourceFile, issue.BlocksUpdates, analysis.ScannedAt, analysis.ManifestHash]);
            }
        Finish(issues, issueRow - 1, issueHeaders.Length);
        progress?.Report(new(OperationStage.Exporting, Detail: "Scan Issues"));
        cancellationToken.ThrowIfCancellationRequested();
        var content = new MemoryStream();
        try
        {
            workbook.SaveAs(content);
            cancellationToken.ThrowIfCancellationRequested();
            content.Position = 0;
            return content;
        }
        catch { content.Dispose(); throw; }
    }

    private static object CandidateAction(PackageUsage usage, NuGetVersion? version) => version is null
        ? "Unavailable" : AlignmentAnalyzer.GetAction(usage, version);

    private static void Header(IXLWorksheet sheet, string[] headers)
    {
        Row(sheet, 1, headers.Cast<object?>().ToArray());
        sheet.Row(1).Style.Font.Bold = true;
        sheet.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#E6F1F3");
        sheet.SheetView.FreezeRows(1);
    }

    private static void Row(IXLWorksheet sheet, int row, object?[] values)
    {
        for (var column = 0; column < values.Length; column++)
        {
            var text = values[column] is DateTimeOffset timestamp ? timestamp.ToString("O") : values[column]?.ToString() ?? "";
            var cell = sheet.Cell(row, column + 1);
            cell.SetValue(text);
            cell.Style.NumberFormat.Format = "@";
        }
    }

    private static void Finish(IXLWorksheet sheet, int lastRow, int columns)
    {
        sheet.Columns(1, columns).Width = 22;
        if (lastRow > 1) sheet.Range(1, 1, lastRow, columns).SetAutoFilter();
    }
}