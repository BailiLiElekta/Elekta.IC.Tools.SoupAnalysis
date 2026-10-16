using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Updates;

namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure.Git;

public sealed class GitUpdateExecutor : IUpdateExecutor
{
    private readonly IRepositorySnapshotProvider snapshots;
    private readonly IPackageScanner scanner;
    private readonly IConfigurationService configuration;
    private readonly IAccessTokenProvider tokens;
    private readonly IGitProcessRunner processes;
    private readonly AzurePullRequestService pullRequests;
    private readonly string appDataRoot;

    public GitUpdateExecutor(IRepositorySnapshotProvider snapshots, IPackageScanner scanner,
        IConfigurationService configuration, IAccessTokenProvider tokens, HttpClient httpClient,
        IGitProcessRunner? processes = null, string? appDataRoot = null)
    {
        this.snapshots = snapshots;
        this.scanner = scanner;
        this.configuration = configuration;
        this.tokens = tokens;
        this.processes = processes ?? new GitProcessRunner();
        pullRequests = new(httpClient);
        this.appDataRoot = Path.GetFullPath(appDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Elekta", "SoupAnalysis"));
    }

    public async Task<UpdateResult> ExecuteAsync(ChangePlan approvedPlan,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var results = new List<RepositoryUpdateResult>();
        var repositoryIds = approvedPlan.Changes.Select(change => change.RepositoryId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!approvedPlan.CanExecute) throw new InvalidOperationException("A current explicit write approval is required.");
            var config = await configuration.LoadAsync(cancellationToken).ConfigureAwait(false);
            ValidatePlan(approvedPlan, config);
            var baselines = new Dictionary<string, SourceSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var baseline in approvedPlan.BaselineCommits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = approvedPlan.OriginalRefs.Single(source => source.Id == baseline.RepositoryId);
                var current = await snapshots.GetSnapshotAsync(source, progress, cancellationToken).ConfigureAwait(false);
                if (current.Source != source || current.Commit != baseline.Commit)
                    throw new InvalidOperationException("A Platform or IC baseline moved; rescan and reapprove.");
                baselines.Add(source.Id, current);
            }
            var runId = approvedPlan.Id.ToString("N");
            var runDirectory = Path.Combine(appDataRoot, "work", runId);
            EnsureNoReparsePoints(runDirectory);
            Directory.CreateDirectory(runDirectory);
            EnsureNoReparsePoints(runDirectory);
            EnsureNoReparsePoints(Path.Combine(runDirectory, "execution.lock"));
            using var executionLock = new FileStream(Path.Combine(runDirectory, "execution.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var journal = await Journal.LoadAsync(Path.Combine(runDirectory, "journal.json"), approvedPlan.Id, repositoryIds).ConfigureAwait(false);
            var hookDirectory = Path.Combine(runDirectory, "disabled-hooks");
            Directory.CreateDirectory(hookDirectory);
            var baseArguments = new[] { "-c", "core.hooksPath=" + hookDirectory, "-c", "core.autocrlf=false", "-c", "core.fsmonitor=false",
                "-c", "core.longpaths=true",
                "-c", "credential.helper=", "-c", "http.followRedirects=false", "-c", "protocol.allow=never", "-c", "protocol.https.allow=always" };
            await GitAsync(runDirectory, baseArguments, ["--version"], new Dictionary<string, string>(), cancellationToken).ConfigureAwait(false);
            var name = (await GitAsync(runDirectory, baseArguments, ["config", "--get", "user.name"], new Dictionary<string, string>(), cancellationToken).ConfigureAwait(false)).Trim();
            var email = (await GitAsync(runDirectory, baseArguments, ["config", "--get", "user.email"], new Dictionary<string, string>(), cancellationToken).ConfigureAwait(false)).Trim();
            if (name.Length == 0 || email.Length == 0 || name.Any(char.IsControl) || email.Any(char.IsControl))
                throw new InvalidOperationException("Configure Git user.name and user.email before updating.");
            var emptyConfig = Path.Combine(runDirectory, "empty.gitconfig");
            await File.WriteAllTextAsync(emptyConfig, "", cancellationToken).ConfigureAwait(false);
            var environment = new Dictionary<string, string> { ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = emptyConfig };
            baseArguments = [.. baseArguments, "-c", "user.name=" + name, "-c", "user.email=" + email, "-c", "init.templateDir=" + hookDirectory];
            await journal.SaveAsync().ConfigureAwait(false);
            foreach (var repositoryId in repositoryIds)
            {
                var source = approvedPlan.OriginalRefs.Single(source => source.Id == repositoryId);
                var baseline = baselines[repositoryId];
                var changes = approvedPlan.Changes.Where(change => change.RepositoryId == repositoryId).ToArray();
                var branch = "soup-align/" + runId;
                var directory = Path.Combine(runDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repositoryId)))[..24], Guid.NewGuid().ToString("N"));
                var previous = journal.Find(repositoryId);
                string? commit = null, prUrl = null;
                var pushed = false;
                var pushStarted = false;
                var prConfirmed = false;
                string? prStatus = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new(OperationStage.Updating, repositoryId, Detail: "Preparing isolated approved changes."));
                    Directory.CreateDirectory(directory);
                    if (previous is null) await journal.RecordAsync(repositoryId, "Preparing", branch, commit, null).ConfigureAwait(false);
                    await GitAsync(directory, baseArguments, ["init"], environment, cancellationToken).ConfigureAwait(false);
                    await GitAsync(directory, baseArguments, ["remote", "add", "origin", source.Url], environment, cancellationToken).ConfigureAwait(false);
                    var access = await tokens.GetTokenAsync(false, cancellationToken).ConfigureAwait(false);
                    var authenticated = Authenticate(environment, access);
                    await GitAsync(directory, baseArguments, ["--config-env=http.extraheader=SOUP_GIT_AUTHORIZATION", "fetch", "--no-tags", "--depth=1", "origin", baseline.Commit], authenticated, cancellationToken).ConfigureAwait(false);
                    var fetched = (await GitAsync(directory, baseArguments, ["rev-parse", "FETCH_HEAD"], environment, cancellationToken).ConfigureAwait(false)).Trim();
                    if (fetched != baseline.Commit) throw new InvalidOperationException("Git fetch did not return the approved exact base SHA.");
                    await GitAsync(directory, baseArguments, ["checkout", "-f", "-b", branch, "FETCH_HEAD"], environment, cancellationToken).ConfigureAwait(false);
                    var tracked = await GitAsync(directory, baseArguments, ["ls-files", "-z"], environment, cancellationToken).ConfigureAwait(false);
                    var files = ImmutableArray.CreateBuilder<SnapshotFile>();
                    foreach (var path in tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                    {
                        PackageFileEditor.SafeRelativePath(path);
                        var localPath = LocalPath(directory, path);
                        if (!PackageFileEditor.IsBuildXml(path)) continue;
                        var bytes = await File.ReadAllBytesAsync(localPath, cancellationToken).ConfigureAwait(false);
                        files.Add(new(path, [.. bytes], Convert.ToHexString(SHA256.HashData(bytes))));
                    }
                    var checkout = baseline with { Files = files.ToImmutable() };
                    foreach (var original in baseline.Files.Where(file => PackageFileEditor.IsBuildXml(file.Path)))
                    {
                        var actual = checkout.Files.SingleOrDefault(file => file.Path == original.Path);
                        if (actual is null || !actual.Content.AsSpan().SequenceEqual(original.Content.AsSpan()))
                            throw new InvalidOperationException("Git checkout differs from the reviewed snapshot bytes.");
                    }
                    var updated = await Task.Run(() => new PackageFileEditor(scanner).Edit(checkout, changes, config.Rules, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    var changedFiles = updated.Files.Where(file => !file.Content.AsSpan().SequenceEqual(checkout.Files.Single(original => original.Path == file.Path).Content.AsSpan())).ToArray();
                    if (changedFiles.Length == 0) throw new InvalidOperationException("Approved edit produced no changes.");
                    await RecheckAsync(approvedPlan, progress, cancellationToken).ConfigureAwait(false);
                    var remoteRef = "refs/heads/" + branch;
                    var remote = (await GitAsync(directory, baseArguments,
                        ["--config-env=http.extraheader=SOUP_GIT_AUTHORIZATION", "ls-remote", "--heads", "origin", remoteRef], authenticated, cancellationToken).ConfigureAwait(false)).Trim();
                    if (remote.Length != 0)
                    {
                        var fields = remote.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (fields.Length != 2 || fields[1] != remoteRef || previous?.Commit != fields[0])
                            throw new InvalidOperationException("Existing source branch does not match this approved plan journal.");
                        commit = fields[0];
                        ValidateSha(commit);
                        pushed = true;
                        await GitAsync(directory, baseArguments, ["--config-env=http.extraheader=SOUP_GIT_AUTHORIZATION", "fetch", "--no-tags", "--depth=2", "origin", commit], authenticated, cancellationToken).ConfigureAwait(false);
                        var parent = (await GitAsync(directory, baseArguments, ["rev-parse", commit + "^"], environment, cancellationToken).ConfigureAwait(false)).Trim();
                        var remoteDiff = await GitAsync(directory, baseArguments, ["diff", "--no-ext-diff", "--name-only", "-z", baseline.Commit, commit], environment, cancellationToken).ConfigureAwait(false);
                        if (parent != baseline.Commit || !remoteDiff.Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)
                            .SequenceEqual(changedFiles.Select(file => file.Path).Order(StringComparer.Ordinal)))
                            throw new InvalidOperationException("Existing remote commit differs from the approved change scope.");
                        await GitAsync(directory, baseArguments, ["checkout", "--detach", commit], environment, cancellationToken).ConfigureAwait(false);
                        var remoteFiles = (await GitAsync(directory, baseArguments, ["ls-files", "-z"], environment, cancellationToken).ConfigureAwait(false))
                            .Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(PackageFileEditor.IsBuildXml).Order(StringComparer.Ordinal).ToArray();
                        if (!remoteFiles.SequenceEqual(updated.Files.Select(file => file.Path).Order(StringComparer.Ordinal)))
                            throw new InvalidOperationException("Remote build consumers differ from the approved tree.");
                        foreach (var file in updated.Files)
                            if (!(await File.ReadAllBytesAsync(LocalPath(directory, file.Path), cancellationToken).ConfigureAwait(false)).AsSpan().SequenceEqual(file.Content.AsSpan()))
                                throw new InvalidOperationException("Remote package bytes differ from the approved edit.");
                        if (previous?.Step is not ("PRStarted" or "PRUnknown" or "PRCreated"))
                            await journal.RecordAsync(repositoryId, "PushedOnly", branch, commit, null).ConfigureAwait(false);
                    }
                    else
                    {
                    if (previous?.Step is "PushedOnly" or "PRStarted" or "PRUnknown" or "PRCreated")
                        throw new InvalidOperationException("Previously pushed source branch is missing; refusing to recreate it.");
                    foreach (var file in changedFiles)
                        await File.WriteAllBytesAsync(LocalPath(directory, file.Path), file.Content.ToArray(), cancellationToken).ConfigureAwait(false);
                    var diff = await GitAsync(directory, baseArguments, ["diff", "--no-ext-diff", "--name-only", "-z"], environment, cancellationToken).ConfigureAwait(false);
                    if (!diff.Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)
                        .SequenceEqual(changedFiles.Select(file => file.Path).Order(StringComparer.Ordinal)))
                        throw new InvalidOperationException("Git diff contains files outside the approved edits.");
                    await GitAsync(directory, baseArguments, ["add", "--", .. changedFiles.Select(file => file.Path)], environment, cancellationToken).ConfigureAwait(false);
                    await GitAsync(directory, baseArguments, ["commit", "-m", "Align explicitly approved SOUP package versions"], environment, cancellationToken).ConfigureAwait(false);
                    commit = (await GitAsync(directory, baseArguments, ["rev-parse", "HEAD"], environment, cancellationToken).ConfigureAwait(false)).Trim();
                    ValidateSha(commit);
                    await journal.RecordAsync(repositoryId, "Committed", branch, commit, null).ConfigureAwait(false);
                    await RecheckAsync(approvedPlan, progress, cancellationToken).ConfigureAwait(false);
                    access = await tokens.GetTokenAsync(false, cancellationToken).ConfigureAwait(false);
                    authenticated = Authenticate(environment, access);
                    await journal.RecordAsync(repositoryId, "PushStarted", branch, commit, null).ConfigureAwait(false);
                    pushStarted = true;
                    await GitAsync(directory, baseArguments, ["--config-env=http.extraheader=SOUP_GIT_AUTHORIZATION", "push", "origin", "HEAD:refs/heads/" + branch], authenticated, cancellationToken).ConfigureAwait(false);
                    pushed = true;
                    await journal.RecordAsync(repositoryId, "PushedOnly", branch, commit, null).ConfigureAwait(false);
                    }
                    await RecheckAsync(approvedPlan, progress, cancellationToken).ConfigureAwait(false);
                    var description = await DescriptionAsync(directory, source, baseline.Commit, changes, cancellationToken).ConfigureAwait(false);
                    access = await tokens.GetTokenAsync(false, cancellationToken).ConfigureAwait(false);
                    var evidence = await pullRequests.CreateOrFindDetailedAsync(source, branch, description, access, cancellationToken,
                        allowCreate: previous?.PullRequestUrl is null && previous?.Step is not ("PRStarted" or "PRUnknown" or "PRCreated"),
                        creationStarted: () => journal.RecordAsync(repositoryId, "PRStarted", branch, commit, null)).ConfigureAwait(false);
                    if (previous?.PullRequestUrl is not null && previous.PullRequestUrl != evidence.Url)
                        throw new InvalidOperationException("PR lookup differs from previously confirmed evidence; inspect the recorded PR.");
                    prUrl = evidence.Url;
                    prStatus = evidence.Status;
                    prConfirmed = true;
                    var prDetail = prStatus == "active" ? null : "Existing PR is " + prStatus + "; no new PR was created.";
                    await journal.RecordAsync(repositoryId, "PRCreated", branch, commit, prUrl, prDetail).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    results.Add(new(repositoryId, prStatus == "active" ? RepositoryUpdateStatus.Completed : RepositoryUpdateStatus.PushedOnly,
                        branch, commit, prUrl, prDetail));
                }
                catch (Exception exception)
                {
                    var cancelled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
                    var recorded = journal.Find(repositoryId);
                    prUrl ??= recorded?.PullRequestUrl ?? previous?.PullRequestUrl;
                    commit ??= recorded?.Commit ?? previous?.Commit;
                    var prUnknown = !prConfirmed && recorded?.Step is "PRStarted" or "PRUnknown"
                        && exception is not AzurePullRequestService.PullRequestCreationException { WasRejected: true };
                    var status = prConfirmed && prStatus == "active" ? RepositoryUpdateStatus.Completed : pushed ? RepositoryUpdateStatus.PushedOnly
                        : cancelled ? RepositoryUpdateStatus.Cancelled : RepositoryUpdateStatus.Failed;
                    var detail = pushStarted && !pushed ? "Push outcome unknown; inspect the recorded source branch before retrying."
                        : prUnknown ? "PR possibly created; creation outcome unknown. " + exception.Message + " Inspect the recorded source branch and reconcile before retrying."
                        : !prConfirmed && prUrl is not null ? "Retry failed to verify previously confirmed PR evidence; recorded URL and commit retained. " + exception.Message + " Inspect and reconcile before retrying."
                        : prConfirmed && prStatus != "active" ? "Existing PR is " + prStatus + "; no new PR was created."
                        : cancelled ? "Cancelled; recorded remote side effects were not rolled back."
                        : $"Update failed ({exception.GetType().Name}: {exception.Message}); inspect the isolated work tree and journal. No rollback was attempted.";
                    results.Add(new(repositoryId, status, branch, commit, prUrl, detail));
                    try
                    {
                        if (previous is null || !ReferenceEquals(journal.Find(repositoryId), previous))
                            await journal.RecordAsync(repositoryId, prUrl is not null || recorded?.Step == "PRCreated" ? "PRCreated"
                                : prUnknown ? "PRUnknown" : pushed ? "PushedOnly"
                                : pushStarted ? "PushOutcomeUnknown" : cancelled ? "FailedCancelled" : "Failed", branch, commit, prUrl, detail).ConfigureAwait(false);
                    }
                    catch (Exception journalFailure) when (journalFailure is IOException or UnauthorizedAccessException)
                    {
                        results[^1] = results[^1] with
                        {
                            Detail = detail + " Execution journal could not be persisted (" + journalFailure.GetType().Name
                                + "); retain the displayed branch/commit/PR evidence and inspect it before retrying."
                        };
                    }
                    if (cancelled)
                    {
                        results.AddRange(repositoryIds.Where(id => results.All(result => result.RepositoryId != id))
                            .Select(id => new RepositoryUpdateResult(id, RepositoryUpdateStatus.Cancelled, Detail: "Not started: operation cancelled.")));
                        return new(approvedPlan.Id, UpdateStatus.Cancelled, [.. results]);
                    }
                }
            }
            return new(approvedPlan.Id, results.All(result => result.Status == RepositoryUpdateStatus.Completed) ? UpdateStatus.Completed
                : results.Any(result => result.Status is RepositoryUpdateStatus.Completed or RepositoryUpdateStatus.PushedOnly)
                    ? UpdateStatus.PartiallyFailed : UpdateStatus.Failed, [.. results]);
        }
        catch (Exception exception)
        {
            var cancelled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
            var detail = cancelled ? "Cancelled before repository updates." : "Preflight rejected: " + exception.GetType().Name
                + ". Recheck approval, manifest, baseline refs, Git installation and identity.";
            results.AddRange(repositoryIds.Where(id => results.All(result => result.RepositoryId != id))
                .Select(id => new RepositoryUpdateResult(id, cancelled ? RepositoryUpdateStatus.Cancelled : RepositoryUpdateStatus.Failed, Detail: detail)));
            return new(approvedPlan.Id, cancelled ? UpdateStatus.Cancelled : UpdateStatus.Failed, [.. results]);
        }
    }

    private static void ValidatePlan(ChangePlan plan, LoadedConfiguration config)
    {
        foreach (var source in plan.OriginalRefs) ConfigurationPolicy.ValidateRepositoryIdentity(source);
        if (config.ManifestHash != plan.ManifestHash || !plan.CanExecute
            || plan.OriginalRefs.GroupBy(source => source.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1)
            || plan.OriginalRefs.GroupBy(source => AzureGitAddress.Parse(source.Url).Api, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Approval or manifest/source identities no longer match.");
        var sources = plan.OriginalRefs.Where(source => source.Category != RepositoryCategory.ICEmulator).ToArray();
        if (sources.Length != plan.BaselineCommits.Length || plan.BaselineCommits.GroupBy(item => item.RepositoryId).Any(group => group.Count() != 1)
            || sources.Any(source => plan.BaselineCommits.Count(item => item.RepositoryId == source.Id) != 1)
            || config.Manifest.Repositories.Length != sources.Count(source => source.Category == RepositoryCategory.Platform)
            || config.Manifest.Repositories.Any(member => !sources.Any(source => source.Id == member.Id && source.Url == member.Url
                && source.Category == RepositoryCategory.Platform)))
            throw new InvalidOperationException("All authoritative Platform and IC baselines are required.");
        foreach (var baseline in plan.BaselineCommits) ValidateSha(baseline.Commit);
        foreach (var change in plan.Changes)
        {
            var source = plan.OriginalRefs.Single(item => item.Id == change.RepositoryId);
            if (source.Category != RepositoryCategory.IC || source.RefKind != RefKind.Branch)
                throw new InvalidOperationException("Only unique normal IC branch sources may be modified.");
            ValidateBranch(source.Ref);
        }
    }

    private async Task RecheckAsync(ChangePlan plan,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var config = await configuration.LoadAsync(cancellationToken).ConfigureAwait(false);
        ValidatePlan(plan, config);
        foreach (var baseline in plan.BaselineCommits)
        {
            var source = plan.OriginalRefs.Single(item => item.Id == baseline.RepositoryId);
            var current = await snapshots.GetSnapshotAsync(source, progress, cancellationToken).ConfigureAwait(false);
            if (current.Source != source || current.Commit != baseline.Commit)
                throw new InvalidOperationException("Platform or IC target moved; rescan and reapprove.");
        }
    }

    private async Task<string> GitAsync(string directory, IReadOnlyList<string> options, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync(new(directory, [.. options, .. arguments], environment), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var commandDesc = string.Join(" ", arguments);
            var err = string.IsNullOrWhiteSpace(result.StandardError) ? "" : $": {result.StandardError.Trim()}";
            throw new InvalidOperationException($"Git operation failed (exit {result.ExitCode}) executing '{commandDesc}'{err}");
        }
        return result.StandardOutput;
    }

    private static Dictionary<string, string> Authenticate(Dictionary<string, string> environment, AccessToken access)
    {
        if (access.ExpiresAt <= DateTimeOffset.UtcNow || string.IsNullOrWhiteSpace(access.Token) || access.Token.Any(char.IsControl))
            throw new InvalidOperationException("A current noninteractive Azure token is required.");
        return new(environment) { ["SOUP_GIT_AUTHORIZATION"] = "Authorization: Bearer " + access.Token };
    }

    private static async Task<string> DescriptionAsync(string directory, RepositorySource source, string sha,
        PlannedPackageChange[] changes, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        foreach (var template in new[] { ".azuredevops/pull_request_template.md", ".github/pull_request_template.md", "pull_request_template.md" })
        {
            var path = LocalPath(directory, template);
            if (!File.Exists(path)) continue;
            text.AppendLine(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
            break;
        }
        text.AppendLine("## Explicitly approved package changes");
        text.AppendLine("Scan source: " + source.Url + "; ref: " + source.Ref + "; base SHA: " + sha);
        foreach (var change in changes)
            foreach (var usage in change.Usages)
                text.AppendLine($"- {change.PackageId}: {usage.Usage.RawVersion} -> {usage.Target.ToNormalizedString()}; target source: {change.TargetSource}; action: {usage.Action}; TEST Only: {change.TestOnly}; project: {usage.Usage.ProjectPath}; owner: {usage.Usage.Declaration.VersionOwner.SourceFile}");
        text.AppendLine("Not restored, built or tested. Target-code compatibility is unverified. Only reviewed declarations were changed; unselected mismatches may remain. No automatic merge.");
        return text.ToString();
    }

    private static void ValidateSha(string value)
    {
        if (value.Length != 40 || value.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidOperationException("A full exact 40-character base SHA is required.");
    }

    private static void ValidateBranch(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-') || value.Contains("..", StringComparison.Ordinal)
            || value.Contains("@{", StringComparison.Ordinal) || value.Contains("//", StringComparison.Ordinal)
            || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || "~^:?*[\\".Contains(character))
            || value.Split('/').Any(part => part.Length == 0 || part.StartsWith('.') || part.EndsWith('.') || part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Invalid configured IC target branch.");
    }

    private static string LocalPath(string directory, string relative)
    {
        PackageFileEditor.SafeRelativePath(relative);
        var path = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("File path escapes the isolated work tree.");
        EnsureNoReparsePoints(path);
        return path;
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Symlink/reparse-point work paths are not supported.");
    }

    private sealed class Journal(string path, Guid planId)
    {
        private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
        public Entry? Find(string repository) => entries.GetValueOrDefault(repository);
        public static async Task<Journal> LoadAsync(string path, Guid planId, string[] repositories)
        {
            EnsureNoReparsePoints(path);
            var journal = new Journal(path, planId);
            if (!File.Exists(path)) return journal;
            await using var stream = File.OpenRead(path);
            var stored = await JsonSerializer.DeserializeAsync<JournalDocument>(stream).ConfigureAwait(false)
                ?? throw new InvalidDataException("Empty execution journal.");
            if (stored.PlanId != planId || stored.Repositories is null)
                throw new InvalidDataException("Journal plan identity mismatch.");
            foreach (var entry in stored.Repositories)
            {
                if (entry is null || !repositories.Contains(entry.RepositoryId, StringComparer.OrdinalIgnoreCase)
                    || entry.Branch != "soup-align/" + planId.ToString("N")
                    || !new[] { "Preparing", "Committed", "PushStarted", "PushedOnly", "PRStarted", "PRUnknown", "PRCreated", "FailedCancelled", "PushOutcomeUnknown", "Failed" }.Contains(entry.Step)
                    || !journal.entries.TryAdd(entry.RepositoryId, entry))
                    throw new InvalidDataException("Journal repository or branch identity mismatch.");
                if (entry.Commit is not null) ValidateSha(entry.Commit);
                if (entry.Step is "Committed" or "PushStarted" or "PushedOnly" or "PRStarted" or "PRUnknown" or "PRCreated" or "PushOutcomeUnknown" && entry.Commit is null)
                    throw new InvalidDataException("Journal is missing the recorded commit.");
            }
            return journal;
        }
        public async Task RecordAsync(string repository, string step, string branch, string? commit, string? pr, string? detail = null)
        {
            entries[repository] = new(repository, step, branch, commit, pr, detail, DateTimeOffset.UtcNow);
            await SaveAsync().ConfigureAwait(false);
        }
        public async Task SaveAsync()
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(stream, new JournalDocument(planId, entries.Values.ToArray()),
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                await FileReplacement.ReplaceAsync(temporary, path, CancellationToken.None).ConfigureAwait(false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public sealed record Entry(string RepositoryId, string Step, string Branch, string? Commit, string? PullRequestUrl,
            string? Detail, DateTimeOffset RecordedAt);
        private sealed record JournalDocument(Guid PlanId, Entry[] Repositories);
    }
}