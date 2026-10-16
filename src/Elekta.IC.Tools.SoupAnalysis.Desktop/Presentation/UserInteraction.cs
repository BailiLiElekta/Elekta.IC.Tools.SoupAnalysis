using System.Windows;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels;
using Microsoft.Win32;

namespace Elekta.IC.Tools.SoupAnalysis.Desktop.Presentation;

public interface IUserInteraction
{
    string? ChooseReportPath(string? directory);
    bool Approve(ChangePlan plan);
    void ShowPreview(ChangePlan plan, string blockers) { }
    void ShowSettings(MainViewModel model);
}

public sealed class WpfUserInteraction : IUserInteraction
{
    public string? ChooseReportPath(string? directory)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export SOUP analysis", Filter = "Excel workbook (*.xlsx)|*.xlsx", DefaultExt = ".xlsx",
            FileName = $"SOUP-Analysis-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx", AddExtension = true,
            InitialDirectory = directory ?? "", OverwritePrompt = true
        };
        return dialog.ShowDialog(System.Windows.Application.Current.MainWindow) == true ? dialog.FileName : null;
    }
    public bool Approve(ChangePlan plan)
    {
        var usages = plan.Changes.SelectMany(change => change.Usages).ToArray();
        var message = $"Approve {plan.Changes.Select(change => change.RepositoryId).Distinct().Count()} repositories, " +
            $"{plan.Changes.Select(change => change.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).Count()} packages and {usages.Length} declaration edits?\n\n" +
            $"Upgrades: {usages.Count(change => change.Action == AlignmentAction.Upgrade)}\n" +
            $"Downgrades: {usages.Count(change => change.Action == AlignmentAction.Downgrade)}\n\n" +
            "Compatibility has NOT been validated. Target projects will not be restored, built or tested.\n" +
            "Execution creates one pull request per selected IC repository. No Platform or emulator changes.\n\n" +
            $"Analysis: {plan.AnalysisId}\nApproval does not execute the plan. Continue?";
        return MessageBox.Show(System.Windows.Application.Current.MainWindow, message, "Approve exact change plan",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
    public void ShowPreview(ChangePlan plan, string blockers)
    {
        var changes = string.Join("\n", plan.SelectedUsages.Select(usage =>
        {
            var choice = plan.Targets.Single(target => target.PackageId.Equals(usage.PackageId, StringComparison.OrdinalIgnoreCase));
            var change = plan.Changes.FirstOrDefault(item => item.RepositoryId == usage.Repository.Id &&
                item.PackageId.Equals(usage.PackageId, StringComparison.OrdinalIgnoreCase));
            var version = change?.Target ?? usage.Version!;
            return $"{usage.Repository.Id} | {usage.PackageId} | {usage.RawVersion} -> {version.ToNormalizedString()} | {choice.Source} | {AlignmentAnalyzer.GetAction(usage, version)}\n" +
                $"  {usage.Declaration.VersionOwner.SourceFile}: {usage.Declaration.VersionOwner.ElementPath}";
        }));
        var sources = string.Join("\n", plan.OriginalRefs.Select(source => $"{source.Name} | {source.RefKind}: {source.Ref} | " +
            (plan.BaselineCommits.FirstOrDefault(commit => commit.RepositoryId == source.Id)?.Commit ?? "No source SHA")));
        MessageBox.Show(System.Windows.Application.Current.MainWindow,
            $"Preview only / Not approved\n\nGLOBAL BLOCKERS: {blockers}\n\n{changes}\n\n" +
            $"Analysis: {plan.AnalysisId}\nPlan: {plan.Id}\nManifest: {plan.ManifestHash}\n{sources}\n\n" +
            "No approval, execution or pull request is available. Compatibility unverified. Rescan and create a new plan after all snapshots are complete.",
            "Read-only change preview", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    public void ShowSettings(MainViewModel model) => new SettingsWindow { Owner = System.Windows.Application.Current.MainWindow, DataContext = model }.ShowDialog();
}

public interface IAuthenticationSettingsEditor
{
    Task SaveAsync(AuthenticationConfiguration authentication, CancellationToken cancellationToken);
}