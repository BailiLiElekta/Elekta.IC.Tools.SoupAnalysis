using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure.Authentication;

public sealed class MicrosoftAccountLogin(AuthenticationConfiguration configuration, string? cacheDirectory = null) : IRefreshableAccessTokenProvider
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IPublicClientApplication? client;
    private MsalCacheHelper? cache;
    public string? AccountLabel { get; private set; }

    public Task<AccessToken> GetTokenAsync(bool allowInteractive, CancellationToken cancellationToken) => AcquireAsync(allowInteractive, false, cancellationToken);
    public Task<AccessToken> RefreshTokenAsync(CancellationToken cancellationToken) => AcquireAsync(false, true, cancellationToken);

    private async Task<AccessToken> AcquireAsync(bool allowInteractive, bool forceRefresh, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await InitializeAsync(cancellationToken);
            var accounts = (await client!.GetAccountsAsync()).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            AuthenticationResult result;
            try
            {
                if (accounts.Length != 1) throw new MsalUiRequiredException("account_selection", "Select your company Microsoft account.");
                result = await client.AcquireTokenSilent(configuration.Scopes, accounts[0]).WithForceRefresh(forceRefresh).ExecuteAsync(cancellationToken);
            }
            catch (MsalUiRequiredException) when (allowInteractive)
            {
                result = await client.AcquireTokenInteractive(configuration.Scopes)
                    .WithUseEmbeddedWebView(false).WithPrompt(Prompt.SelectAccount).ExecuteAsync(cancellationToken);
            }
            AccountLabel = result.Account?.Username;
            return new(result.AccessToken, result.ExpiresOn);
        }
        catch (MsalException error) when (error.ErrorCode == "authentication_canceled")
        {
            throw new OperationCanceledException("Microsoft sign-in was cancelled.", cancellationToken);
        }
        catch (MsalException error)
        {
            throw new InvalidOperationException($"Microsoft sign-in failed ({error.ErrorCode}). Check Entra consent, company account/MFA and public-client redirect registration.");
        }
        finally { gate.Release(); }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (client is null) { AccountLabel = null; return; }
            foreach (var account in await client.GetAccountsAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await client.RemoveAsync(account);
            }
            AccountLabel = null;
        }
        finally { gate.Release(); }
    }

    private async Task InitializeAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!configuration.IsConfigured || configuration.SchemaVersion != 1 || configuration.Scopes.IsDefaultOrEmpty
            || configuration.Scopes.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Sign-in requires TenantId, ClientId, a registered RedirectUri and Azure DevOps delegated scopes in authentication.json. Ask IT for an Entra public-client registration.");
        if (!Guid.TryParse(configuration.ClientId, out _) || configuration.TenantId is "." or ".."
            || configuration.TenantId.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '.')
            || !Guid.TryParse(configuration.TenantId, out _) && Uri.CheckHostName(configuration.TenantId) != UriHostNameType.Dns)
            throw new InvalidOperationException("Use a valid Entra ClientId GUID and tenant ID/domain.");
        var redirect = new Uri(configuration.RedirectUri);
        if (!string.IsNullOrEmpty(redirect.UserInfo) || !string.IsNullOrEmpty(redirect.Query) || !string.IsNullOrEmpty(redirect.Fragment)
            || redirect.Scheme == Uri.UriSchemeHttp && !redirect.IsLoopback)
            throw new InvalidOperationException("The registered redirect must not contain credentials/query/fragment; HTTP redirects must be loopback-only.");
        if (client is not null) return;
        var directory = cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Elekta", "SoupAnalysis", "auth");
        Directory.CreateDirectory(directory);
        var application = PublicClientApplicationBuilder.Create(configuration.ClientId)
            .WithAuthority("https://login.microsoftonline.com/" + configuration.TenantId)
            .WithRedirectUri(configuration.RedirectUri).Build();
        cache = await MsalCacheHelper.CreateAsync(new StorageCreationPropertiesBuilder("msal.cache", directory).Build());
        cache.VerifyPersistence();
        token.ThrowIfCancellationRequested();
        cache.RegisterCache(application.UserTokenCache);
        client = application;
    }
}