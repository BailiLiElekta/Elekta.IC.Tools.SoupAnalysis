namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure.Authentication;

public enum AzureCliCommand { GetAccessToken, Login }

public interface IAzureCliRunner
{
    Task<AzureCliResult> RunAsync(AzureCliCommand command, CancellationToken cancellationToken);
}

public sealed record AzureCliResult(int ExitCode, string StandardOutput = "", string StandardError = "")
{
    public override string ToString() => $"AzureCliResult (exit {ExitCode}; output redacted)";
}

public enum AzureCliAuthenticationFailure { NotInstalled, LoginRequired, AccessDenied, InvalidToken, CliFailure, Disconnected }

public sealed class AzureCliAuthenticationException(AzureCliAuthenticationFailure failure) : InvalidOperationException(failure switch
{
    AzureCliAuthenticationFailure.NotInstalled => "Azure CLI is not installed or its launch path is unsafe. Install Azure CLI using your approved IT process, then select Login.",
    AzureCliAuthenticationFailure.LoginRequired => "Azure CLI sign-in is required. Select Login to sign in with your company account and complete MFA; or run az login --allow-no-subscriptions in a terminal.",
    AzureCliAuthenticationFailure.AccessDenied => "Azure CLI access was denied. Check company account, MFA, Conditional Access and Azure DevOps permissions with IT; reauthenticate using az login --allow-no-subscriptions if needed.",
    AzureCliAuthenticationFailure.InvalidToken => "Azure CLI returned invalid or expired token metadata. Check the workstation clock and update or reauthenticate Azure CLI using az login --allow-no-subscriptions. No token was accepted.",
    AzureCliAuthenticationFailure.Disconnected => "This application is disconnected. Select Login to reuse or reauthenticate your Azure CLI account. Your shared Azure CLI session has not been removed.",
    _ => "Azure CLI authentication failed. Check connectivity and company sign-in policy in a terminal; run az login --allow-no-subscriptions to reauthenticate after a rejected token. CLI refresh may reuse its cached credential."
})
{
    public AzureCliAuthenticationFailure Failure { get; } = failure;
}