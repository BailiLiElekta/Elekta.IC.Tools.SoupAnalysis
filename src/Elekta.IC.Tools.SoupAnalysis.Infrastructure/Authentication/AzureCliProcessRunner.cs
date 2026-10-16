using System.ComponentModel;
using System.Diagnostics;

namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure.Authentication;

public sealed class AzureCliProcessRunner : IAzureCliRunner
{
    private readonly IAzureCliProcessFactory processFactory;
    private readonly Func<IEnumerable<string>> candidates;
    private readonly Func<string, bool> fileExists;

    public AzureCliProcessRunner() : this(new SystemProcessFactory(), DiscoverCandidates, File.Exists) { }
    public AzureCliProcessRunner(IAzureCliProcessFactory processFactory, Func<IEnumerable<string>> candidates, Func<string, bool> fileExists)
    {
        this.processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        this.candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        this.fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
    }

    public async Task<AzureCliResult> RunAsync(AzureCliCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var startInfo = CreateStartInfo(command);
            cancellationToken.ThrowIfCancellationRequested();
            using var process = processFactory.Start(startInfo);
            Task<string> output = Task.FromResult("");
            Task<string> error = Task.FromResult("");
            try
            {
                if (startInfo.RedirectStandardOutput) output = process.ReadStandardOutputAsync();
                if (startInfo.RedirectStandardError) error = process.ReadStandardErrorAsync();
                if (startInfo.RedirectStandardInput) process.CloseStandardInput();
                await process.WaitForExitAsync(cancellationToken);
                await Task.WhenAll(output, error).WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return new(process.ExitCode, await output, await error);
            }
            catch
            {
                process.KillTree();
                await process.WaitForExitAsync(CancellationToken.None);
                try { await Task.WhenAll(output, error); }
                catch (Exception) { }
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Azure CLI authentication was cancelled.", cancellationToken); }
        catch (AzureCliAuthenticationException) { throw; }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
        { throw new AzureCliAuthenticationException(AzureCliAuthenticationFailure.NotInstalled); }
        catch (Win32Exception error) when (error.NativeErrorCode == 5)
        { throw new AzureCliAuthenticationException(AzureCliAuthenticationFailure.AccessDenied); }
        catch (Exception) { throw new AzureCliAuthenticationException(AzureCliAuthenticationFailure.CliFailure); }
    }

    private ProcessStartInfo CreateStartInfo(AzureCliCommand command)
    {
        var cli = candidates().Select(CanonicalCliPath).FirstOrDefault(path => path is not null && fileExists(path));
        if (cli is null) throw new AzureCliAuthenticationException(AzureCliAuthenticationFailure.NotInstalled);
        string[] arguments = command switch
        {
            AzureCliCommand.GetAccessToken => ["account", "get-access-token", "--resource", "499b84ac-1321-427f-aa17-267ca6975798", "--output", "json", "--only-show-errors"],
            AzureCliCommand.Login => ["login", "--allow-no-subscriptions", "--output", "none", "--only-show-errors"],
            _ => throw new AzureCliAuthenticationException(AzureCliAuthenticationFailure.CliFailure)
        };
        var interactive = command == AzureCliCommand.Login;
        var start = new ProcessStartInfo
        {
            FileName = cli,
            UseShellExecute = false,
            CreateNoWindow = !interactive,
            RedirectStandardOutput = !interactive,
            RedirectStandardError = !interactive,
            RedirectStandardInput = !interactive,
            WorkingDirectory = Environment.SystemDirectory
        };
        start.Environment["AZURE_CORE_LOGIN_EXPERIENCE_V2"] = "off";
        start.Environment["AZURE_CORE_ENABLE_BROKER_ON_WINDOWS"] = "false";
        if (Path.GetExtension(cli).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(cli)!;
            var root = Path.GetDirectoryName(directory)!;
            var python = Path.Combine(root, "python.exe");
            if (Path.GetFileName(directory).Equals("wbin", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(root).Equals("CLI2", StringComparison.OrdinalIgnoreCase) && fileExists(python))
            {
                start.FileName = python;
                start.ArgumentList.Add("-I");
                start.ArgumentList.Add("-m");
                start.ArgumentList.Add("azure.cli");
            }
            else
            {
                start.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                start.Arguments = $"/d /v:off /s /c \"\"{cli}\" {string.Join(' ', arguments)}\"";
                return start;
            }
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static string? CanonicalCliPath(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Any(char.IsControl)
            || candidate.IndexOfAny(['&', '|', '<', '>', '^', '%', '!', '"']) >= 0
            || !Path.IsPathFullyQualified(candidate) || candidate.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        try
        {
            var fullPath = Path.GetFullPath(candidate);
            var file = Path.GetFileName(fullPath);
            if (!file.Equals("az.exe", StringComparison.OrdinalIgnoreCase) && !file.Equals("az.cmd", StringComparison.OrdinalIgnoreCase)) return null;
            if (IsInside(fullPath, Environment.CurrentDirectory) || IsInside(fullPath, AppContext.BaseDirectory)) return null;
            return fullPath;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    private static bool IsInside(string path, string directory) => path.StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> DiscoverCandidates()
    {
        foreach (var programFiles in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            if (!string.IsNullOrWhiteSpace(programFiles)) yield return Path.Combine(programFiles, "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd");
        }
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            yield return Path.Combine(directory, "az.exe");
            yield return Path.Combine(directory, "az.cmd");
        }
    }

    private sealed class SystemProcessFactory : IAzureCliProcessFactory
    {
        public IAzureCliProcess Start(ProcessStartInfo startInfo)
        {
            var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start()) throw new AzureCliAuthenticationException(AzureCliAuthenticationFailure.CliFailure);
                return new SystemProcess(process);
            }
            catch { process.Dispose(); throw; }
        }
    }

    private sealed class SystemProcess(Process process) : IAzureCliProcess
    {
        public int ExitCode => process.ExitCode;
        public Task<string> ReadStandardOutputAsync() => process.StandardOutput.ReadToEndAsync();
        public Task<string> ReadStandardErrorAsync() => process.StandardError.ReadToEndAsync();
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void CloseStandardInput() => process.StandardInput.Close();
        public void KillTree()
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
        public void Dispose() => process.Dispose();
    }
}