using Elekta.IC.Tools.SoupAnalysis.Application;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure.Authentication;

public sealed class AzureCliAccessTokenProvider : IRefreshableAccessTokenProvider
{
    private static readonly Regex InteractionRequiredCode = new(@"\bAADSTS(?:700082|70043|50076|50079|50058|50173)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private readonly IAzureCliRunner runner;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private AccessToken? cached;
    private bool disconnected;

    public AzureCliAccessTokenProvider() : this(new AzureCliProcessRunner()) { }
    public AzureCliAccessTokenProvider(IAzureCliRunner runner, TimeProvider? timeProvider = null)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        clock = timeProvider ?? TimeProvider.System;
    }

    public Task<AccessToken> GetTokenAsync(bool allowInteractive, CancellationToken cancellationToken) => AcquireAsync(allowInteractive, false, cancellationToken);
    public Task<AccessToken> RefreshTokenAsync(CancellationToken cancellationToken) => AcquireAsync(false, true, cancellationToken);

    private async Task<AccessToken> AcquireAsync(bool allowInteractive, bool refresh, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (disconnected && !allowInteractive) throw new AzureCliAuthenticationException(AzureCliAuthenticationFailure.Disconnected);
            if (!refresh && cached is not null && cached.ExpiresAt - clock.GetUtcNow() > TimeSpan.FromMinutes(2)) return cached;
            cached = null;
            var result = await RunAsync(AzureCliCommand.GetAccessToken, cancellationToken);
            if (result.ExitCode != 0)
            {
                var failure = Classify(result.StandardError);
                if (!allowInteractive || failure != AzureCliAuthenticationFailure.LoginRequired)
                    throw new AzureCliAuthenticationException(failure);
                var login = await RunAsync(AzureCliCommand.Login, cancellationToken);
                if (login.ExitCode != 0) throw new AzureCliAuthenticationException(Classify(login.StandardError));
                result = await RunAsync(AzureCliCommand.GetAccessToken, cancellationToken);
                if (result.ExitCode != 0) throw new AzureCliAuthenticationException(Classify(result.StandardError));
            }
            cancellationToken.ThrowIfCancellationRequested();
            cached = ParseToken(result.StandardOutput);
            disconnected = false;
            return cached;
        }
        finally { gate.Release(); }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            cached = null;
            disconnected = true;
        }
        finally { gate.Release(); }
    }

    private async Task<AzureCliResult> RunAsync(AzureCliCommand command, CancellationToken cancellationToken)
    {
        try { return await runner.RunAsync(command, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Azure CLI authentication was cancelled.", cancellationToken); }
        catch (AzureCliAuthenticationException) { throw; }
        catch (Exception) { throw new AzureCliAuthenticationException(AzureCliAuthenticationFailure.CliFailure); }
    }

    private static AzureCliAuthenticationFailure Classify(string error)
    {
        if (new[] { "AADSTS53000", "AADSTS53001", "AADSTS53002", "AADSTS53003", "AADSTS50105", "AADSTS65001" }
                .Any(code => error.Contains(code, StringComparison.OrdinalIgnoreCase))
            || error.Contains("AuthorizationFailed", StringComparison.OrdinalIgnoreCase)
            || error.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            || error.Contains("forbidden", StringComparison.OrdinalIgnoreCase)) return AzureCliAuthenticationFailure.AccessDenied;
        try
        {
            if (InteractionRequiredCode.IsMatch(error)) return AzureCliAuthenticationFailure.LoginRequired;
        }
        catch (RegexMatchTimeoutException) { return AzureCliAuthenticationFailure.CliFailure; }
        var prompt = error.Trim();
        if (prompt.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) prompt = prompt[6..].Trim();
        if (prompt.Equals("Please run 'az login' to setup account.", StringComparison.OrdinalIgnoreCase)
            || prompt.Equals("Please run \"az login\" to setup account.", StringComparison.OrdinalIgnoreCase)) return AzureCliAuthenticationFailure.LoginRequired;
        return AzureCliAuthenticationFailure.CliFailure;
    }

    private AccessToken ParseToken(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!fields.Add(property.Name)) throw InvalidToken();
            var token = root.GetProperty("accessToken").GetString();
            var type = root.GetProperty("tokenType").GetString();
            if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl)
                || !string.Equals(type, "Bearer", StringComparison.OrdinalIgnoreCase)) throw InvalidToken();
            DateTimeOffset expiry;
            if (root.TryGetProperty("expires_on", out var unix))
            {
                long seconds;
                if (unix.ValueKind == JsonValueKind.String)
                {
                    if (!long.TryParse(unix.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds)) throw InvalidToken();
                }
                else if (unix.ValueKind != JsonValueKind.Number || !unix.TryGetInt64(out seconds)) throw InvalidToken();
                expiry = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            else
            {
                var text = root.GetProperty("expiresOn").GetString();
                if (!DateTime.TryParseExact(text, ["yyyy-MM-dd HH:mm:ss.ffffff", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.ffffff", "yyyy-MM-dd'T'HH:mm:ss"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
                    || TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local)) throw InvalidToken();
                expiry = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified)));
            }
            if (expiry <= clock.GetUtcNow()) throw InvalidToken();
            return new(token, expiry);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException)
        { throw InvalidToken(); }
    }

    private static AzureCliAuthenticationException InvalidToken() => new(AzureCliAuthenticationFailure.InvalidToken);
}