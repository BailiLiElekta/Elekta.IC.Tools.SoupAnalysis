using System.Diagnostics;

namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure.Git;

public sealed record GitProcessRequest(string WorkingDirectory, IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment)
{
    public override string ToString() => "Git process request (environment redacted)";
}

public sealed record GitProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public override string ToString() => $"Git process exited {ExitCode} (output redacted)";
}

public interface IGitProcessRunner
{
    Task<GitProcessResult> RunAsync(GitProcessRequest request, CancellationToken cancellationToken);
}

public sealed class GitProcessRunner : IGitProcessRunner
{
    public async Task<GitProcessResult> RunAsync(GitProcessRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = request.WorkingDirectory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        foreach (var (key, value) in request.Environment) start.Environment[key] = value;
        using var process = new System.Diagnostics.Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Git could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new(process.ExitCode, Redact(await output.ConfigureAwait(false), request),
                Redact(await error.ConfigureAwait(false), request));
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            throw;
        }
    }

    private static string Redact(string output, GitProcessRequest request)
    {
        foreach (var (key, value) in request.Environment.Where(pair => pair.Key.Contains("AUTH", StringComparison.OrdinalIgnoreCase)
            || pair.Key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)))
        {
            output = output.Replace(value, "[redacted]", StringComparison.Ordinal);
            var bearer = value.LastIndexOf("Bearer ", StringComparison.OrdinalIgnoreCase);
            if (bearer >= 0) output = output.Replace(value[(bearer + 7)..], "[redacted]", StringComparison.Ordinal);
        }
        return output;
    }
}