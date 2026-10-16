using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Authentication;

namespace Elekta.IC.Tools.SoupAnalysis.Desktop.Presentation;

public sealed class AuthenticationSettingsStore(DesktopPaths paths) : IAuthenticationSettingsEditor
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true, Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<AuthenticationConfiguration?> LoadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!File.Exists(paths.AuthenticationOverridePath)) return null;
            var authentication = JsonSerializer.Deserialize<AuthenticationConfiguration>(await File.ReadAllBytesAsync(paths.AuthenticationOverridePath, token), Options)
                ?? throw new InvalidDataException("Authentication override is empty.");
            if (authentication.SchemaVersion != 1 || authentication.Scopes.IsDefaultOrEmpty || authentication.Scopes.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Authentication override schema or delegated scopes are invalid.");
            return authentication;
        }
        finally { gate.Release(); }
    }
    public async Task SaveAsync(AuthenticationConfiguration authentication, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        var temporary = paths.AuthenticationOverridePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(paths.UserDataDirectory);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, authentication, Options, cancellationToken);
                await file.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(paths.AuthenticationOverridePath)) File.Replace(temporary, paths.AuthenticationOverridePath, paths.AuthenticationOverridePath + ".bak");
            else File.Move(temporary, paths.AuthenticationOverridePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); gate.Release(); }
    }
}

public sealed class DesktopConfigurationService(IConfigurationService packaged, AuthenticationSettingsStore authentication, bool smokeMode = false) : IConfigurationService
{
    public async Task<LoadedConfiguration> LoadAsync(CancellationToken cancellationToken)
    {
        var configuration = await packaged.LoadAsync(cancellationToken);
        var replacement = smokeMode ? null : await authentication.LoadAsync(cancellationToken);
        return replacement is null ? configuration : configuration with { Authentication = replacement };
    }
}

public sealed class ReloadingAccessTokenProvider(IConfigurationService configurations, Func<AuthenticationConfiguration, IAccessTokenProvider> factory) : IRefreshableAccessTokenProvider
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private AuthenticationConfiguration? lastConfiguration;
    private IAccessTokenProvider? current;
    public Task<AccessToken> GetTokenAsync(bool allowInteractive, CancellationToken cancellationToken) => AcquireAsync(allowInteractive, false, cancellationToken);
    public Task<AccessToken> RefreshTokenAsync(CancellationToken cancellationToken) => AcquireAsync(false, true, cancellationToken);
    private async Task<AccessToken> AcquireAsync(bool allowInteractive, bool forceRefresh, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var authentication = (await configurations.LoadAsync(cancellationToken)).Authentication;
            if (current is null || !Equivalent(authentication, lastConfiguration))
            {
                if (current is not null) await current.SignOutAsync(cancellationToken);
                current = factory(authentication);
                lastConfiguration = authentication;
            }
            if (forceRefresh)
                return current is IRefreshableAccessTokenProvider refreshable ? await refreshable.RefreshTokenAsync(cancellationToken)
                    : throw new InvalidOperationException("The token provider cannot force a noninteractive refresh.");
            return await current.GetTokenAsync(allowInteractive, cancellationToken);
        }
        finally { gate.Release(); }
    }
    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (current is not null) await current.SignOutAsync(cancellationToken);
            current = null;
            lastConfiguration = null;
        }
        finally { gate.Release(); }
    }
    private static bool Equivalent(AuthenticationConfiguration left, AuthenticationConfiguration? right) => right is not null &&
        left.TenantId == right.TenantId && left.ClientId == right.ClientId && left.RedirectUri == right.RedirectUri && left.Scopes.SequenceEqual(right.Scopes);
}