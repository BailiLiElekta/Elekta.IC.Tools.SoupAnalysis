using System.Diagnostics;

namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure.Authentication;

public interface IAzureCliProcessFactory
{
    IAzureCliProcess Start(ProcessStartInfo startInfo);
}

public interface IAzureCliProcess : IDisposable
{
    int ExitCode { get; }
    Task<string> ReadStandardOutputAsync();
    Task<string> ReadStandardErrorAsync();
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void CloseStandardInput();
    void KillTree();
}