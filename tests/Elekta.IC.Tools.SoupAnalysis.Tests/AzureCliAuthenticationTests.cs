using System.Text.Json;
using System.Diagnostics;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Authentication;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Elekta.IC.Tools.SoupAnalysis.Tests;

[TestClass]
public sealed class AzureCliAuthenticationTests
{
    [TestMethod]
    public async Task ParameterlessProviderImplementsRefreshAndCancelsBeforeAnyCliWork()
    {
        IRefreshableAccessTokenProvider provider = new AzureCliAccessTokenProvider();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.GetTokenAsync(false, cancelled.Token));
    }

    [TestMethod]
    public async Task ReusesCliCredentialAndMemoryCacheWithoutLogin()
    {
        var runner = new FakeRunner(Success());
        var provider = new AzureCliAccessTokenProvider(runner);
        var token = await provider.GetTokenAsync(true, default);
        Assert.AreEqual("secret-token", token.Token);
        Assert.AreEqual(token, await provider.GetTokenAsync(false, default));
        CollectionAssert.AreEqual(new[] { AzureCliCommand.GetAccessToken }, runner.Commands.ToArray());
        Assert.IsFalse(token.ToString().Contains("secret-token", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MissingLoginUsesVisibleLoginOnlyForExplicitInteractiveRequest()
    {
        var runner = new FakeRunner(MissingLogin(), new(0), Success());
        var token = await new AzureCliAccessTokenProvider(runner).GetTokenAsync(true, default);
        Assert.AreEqual("secret-token", token.Token);
        CollectionAssert.AreEqual(new[] { AzureCliCommand.GetAccessToken, AzureCliCommand.Login, AzureCliCommand.GetAccessToken }, runner.Commands.ToArray());
    }

    [TestMethod]
    public async Task NoninteractiveMissingLoginNeverSignsIn()
    {
        var runner = new FakeRunner(MissingLogin());
        var error = await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => new AzureCliAccessTokenProvider(runner).GetTokenAsync(false, default));
        Assert.AreEqual(AzureCliAuthenticationFailure.LoginRequired, error.Failure);
        Assert.HasCount(1, runner.Commands);
    }

    [TestMethod]
    [DataRow("700082")]
    [DataRow("70043")]
    [DataRow("50076")]
    [DataRow("50079")]
    [DataRow("50058")]
    [DataRow("50173")]
    public async Task KnownInteractionCodeRecoversWithOneExplicitLogin(string code)
    {
        var runner = new FakeRunner(InteractionRequired(code), new(0), Success());
        var token = await new AzureCliAccessTokenProvider(runner).GetTokenAsync(true, default);
        Assert.AreEqual("secret-token", token.Token);
        CollectionAssert.AreEqual(new[] { AzureCliCommand.GetAccessToken, AzureCliCommand.Login, AzureCliCommand.GetAccessToken }, runner.Commands.ToArray());
    }

    [TestMethod]
    [DataRow("700082")]
    [DataRow("70043")]
    [DataRow("50076")]
    [DataRow("50079")]
    [DataRow("50058")]
    [DataRow("50173")]
    public async Task KnownInteractionCodeNeverLogsInNoninteractively(string code)
    {
        var runner = new FakeRunner(InteractionRequired(code));
        var error = await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => new AzureCliAccessTokenProvider(runner).GetTokenAsync(false, default));
        Assert.AreEqual(AzureCliAuthenticationFailure.LoginRequired, error.Failure);
        CollectionAssert.AreEqual(new[] { AzureCliCommand.GetAccessToken }, runner.Commands.ToArray());
        Assert.IsFalse(error.ToString().Contains("secret-token", StringComparison.Ordinal));
        Assert.IsFalse(error.ToString().Contains("private-cli-error", StringComparison.Ordinal));
        Assert.IsNull(error.InnerException);
    }

    [TestMethod]
    [DataRow("700082")]
    [DataRow("70043")]
    [DataRow("50076")]
    [DataRow("50079")]
    [DataRow("50058")]
    [DataRow("50173")]
    public async Task KnownInteractionCodeRefreshNeverLogsInOrReusesCache(string code)
    {
        var runner = new FakeRunner(Success(), InteractionRequired(code), Success("reacquired"));
        var provider = new AzureCliAccessTokenProvider(runner);
        await provider.GetTokenAsync(false, default);
        var error = await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => provider.RefreshTokenAsync(default));
        Assert.AreEqual(AzureCliAuthenticationFailure.LoginRequired, error.Failure);
        Assert.AreEqual("reacquired", (await provider.GetTokenAsync(false, default)).Token);
        CollectionAssert.AreEqual(Enumerable.Repeat(AzureCliCommand.GetAccessToken, 3).ToArray(), runner.Commands.ToArray());
    }

    [TestMethod]
    [DataRow("AADSTS700082 AADSTS53000", AzureCliAuthenticationFailure.AccessDenied)]
    [DataRow("AADSTS53000 AADSTS700082", AzureCliAuthenticationFailure.AccessDenied)]
    [DataRow("AADSTS50076 AADSTS53001", AzureCliAuthenticationFailure.AccessDenied)]
    [DataRow("AADSTS53001 AADSTS50076", AzureCliAuthenticationFailure.AccessDenied)]
    [DataRow("AADSTS70043 AADSTS53002", AzureCliAuthenticationFailure.AccessDenied)]
    [DataRow("AADSTS53002 AADSTS70043", AzureCliAuthenticationFailure.AccessDenied)]
    [DataRow("AADSTS50079 AADSTS53003", AzureCliAuthenticationFailure.AccessDenied)]
    [DataRow("AADSTS53003 AADSTS50079", AzureCliAuthenticationFailure.AccessDenied)]
    [DataRow("AADSTS50058 AADSTS65001", AzureCliAuthenticationFailure.AccessDenied)]
    [DataRow("AADSTS65001 AADSTS50058", AzureCliAuthenticationFailure.AccessDenied)]
    [DataRow("AADSTS50173 AADSTS50105", AzureCliAuthenticationFailure.AccessDenied)]
    [DataRow("AADSTS50105 AADSTS50173", AzureCliAuthenticationFailure.AccessDenied)]
    [DataRow("AADSTS700082 access denied", AzureCliAuthenticationFailure.AccessDenied)]
    [DataRow("AADSTS50076 forbidden", AzureCliAuthenticationFailure.AccessDenied)]
    [DataRow("AADSTS50079 AuthorizationFailed", AzureCliAuthenticationFailure.AccessDenied)]
    [DataRow("AADSTS65001 consent required", AzureCliAuthenticationFailure.AccessDenied)]
    [DataRow("network timeout", AzureCliAuthenticationFailure.CliFailure)]
    [DataRow("Interactive authentication is needed. Please run: az login", AzureCliAuthenticationFailure.CliFailure)]
    public async Task KnownInteractionCodeDenialsTakePriorityAndGenericErrorsFailClosed(string stderr, AzureCliAuthenticationFailure expected)
    {
        var runner = new FakeRunner(new AzureCliResult(1, "secret-token", stderr + " private-cli-error"));
        var error = await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => new AzureCliAccessTokenProvider(runner).GetTokenAsync(true, default));
        Assert.AreEqual(expected, error.Failure);
        CollectionAssert.AreEqual(new[] { AzureCliCommand.GetAccessToken }, runner.Commands.ToArray());
        Assert.IsFalse(error.ToString().Contains("secret-token", StringComparison.Ordinal));
        Assert.IsFalse(error.ToString().Contains("private-cli-error", StringComparison.Ordinal));
        Assert.IsNull(error.InnerException);
    }

    [TestMethod]
    [DataRow("AADSTS7000820")]
    [DataRow("AADSTS700430")]
    [DataRow("AADSTS500760")]
    [DataRow("AADSTS500790")]
    [DataRow("AADSTS500580")]
    [DataRow("AADSTS501730")]
    [DataRow("junkAADSTS700082")]
    [DataRow("AADSTS50076junk")]
    [DataRow("_AADSTS50079")]
    [DataRow("AADSTS50173_suffix")]
    [DataRow("AADSTS 700082")]
    [DataRow("700082")]
    public async Task KnownInteractionCodeLookalikesNeverAuthorizeLogin(string fakeCode)
    {
        var runner = new FakeRunner(new AzureCliResult(1, "secret-token", $"ERROR: {fakeCode}: Interactive authentication is needed. Please run: az login"));
        var error = await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => new AzureCliAccessTokenProvider(runner).GetTokenAsync(true, default));
        Assert.AreEqual(AzureCliAuthenticationFailure.CliFailure, error.Failure);
        CollectionAssert.AreEqual(new[] { AzureCliCommand.GetAccessToken }, runner.Commands.ToArray());
    }

    [TestMethod]
    [DataRow("ERROR: (aadsts700082): refresh token expired")]
    [DataRow("ERROR: [AADSTS50076], MFA required")]
    public async Task KnownInteractionCodeAcceptsDelimitedCaseInsensitiveTokens(string stderr)
    {
        var runner = new FakeRunner(new AzureCliResult(1, "", stderr), new(0), Success());
        Assert.AreEqual("secret-token", (await new AzureCliAccessTokenProvider(runner).GetTokenAsync(true, default)).Token);
        CollectionAssert.AreEqual(new[] { AzureCliCommand.GetAccessToken, AzureCliCommand.Login, AzureCliCommand.GetAccessToken }, runner.Commands.ToArray());
    }

    [TestMethod]
    [DataRow("700082")]
    [DataRow("50076")]
    public async Task KnownInteractionCodeFailedLoginNeverLoopsOrLeaksStreams(string code)
    {
        var runner = new FakeRunner(InteractionRequired(code), InteractionRequired(code));
        var error = await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => new AzureCliAccessTokenProvider(runner).GetTokenAsync(true, default));
        Assert.AreEqual(AzureCliAuthenticationFailure.LoginRequired, error.Failure);
        CollectionAssert.AreEqual(new[] { AzureCliCommand.GetAccessToken, AzureCliCommand.Login }, runner.Commands.ToArray());
        Assert.IsFalse(error.ToString().Contains("secret-token", StringComparison.Ordinal));
        Assert.IsFalse(error.ToString().Contains("private-cli-error", StringComparison.Ordinal));
        Assert.IsNull(error.InnerException);
    }

    [TestMethod]
    [DataRow("700082")]
    [DataRow("50076")]
    public async Task KnownInteractionCodeStillRequiredAfterLoginNeverLoops(string code)
    {
        var runner = new FakeRunner(InteractionRequired(code), new(0), InteractionRequired(code));
        var error = await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => new AzureCliAccessTokenProvider(runner).GetTokenAsync(true, default));
        Assert.AreEqual(AzureCliAuthenticationFailure.LoginRequired, error.Failure);
        CollectionAssert.AreEqual(new[] { AzureCliCommand.GetAccessToken, AzureCliCommand.Login, AzureCliCommand.GetAccessToken }, runner.Commands.ToArray());
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(3)]
    public async Task KnownInteractionCodeCancellationDuringRecoveryReleasesGate(int cancelOnCommandCount)
    {
        var runner = new FakeRunner(InteractionRequired("50076"), new(0), Success(), Success()) { CancelOnCommandCount = cancelOnCommandCount };
        var provider = new AzureCliAccessTokenProvider(runner);
        using var cancellation = new CancellationTokenSource();
        var pending = provider.GetTokenAsync(true, cancellation.Token);
        await Task.WhenAny(runner.CancellationStarted.Task, pending);
        if (pending.IsCompleted) await pending;
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.HasCount(cancelOnCommandCount, runner.Commands);
        Assert.AreEqual(1, runner.Commands.Count(command => command == AzureCliCommand.Login));
        var recovered = await provider.GetTokenAsync(false, default);
        Assert.AreEqual("secret-token", recovered.Token);
        Assert.AreEqual(AzureCliCommand.GetAccessToken, runner.Commands[^1]);
    }

    [TestMethod]
    public async Task RefreshClearsOwnCacheAndReacquiresWithoutLogin()
    {
        var runner = new FakeRunner(Success(), Success("new-token"), MissingLogin());
        var provider = new AzureCliAccessTokenProvider(runner);
        await provider.GetTokenAsync(false, default);
        Assert.AreEqual("new-token", (await provider.RefreshTokenAsync(default)).Token);
        await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => provider.RefreshTokenAsync(default));
        Assert.IsTrue(runner.Commands.All(command => command == AzureCliCommand.GetAccessToken));
    }

    [TestMethod]
    public async Task DisconnectIsLocalAndBlocksSilentUseUntilExplicitLogin()
    {
        var runner = new FakeRunner(Success(), Success("reconnected"));
        var provider = new AzureCliAccessTokenProvider(runner);
        await provider.GetTokenAsync(false, default);
        await provider.SignOutAsync(default);
        var error = await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => provider.GetTokenAsync(false, default));
        Assert.AreEqual(AzureCliAuthenticationFailure.Disconnected, error.Failure);
        await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => provider.RefreshTokenAsync(default));
        Assert.HasCount(1, runner.Commands);
        Assert.AreEqual("reconnected", (await provider.GetTokenAsync(true, default)).Token);
        Assert.HasCount(2, runner.Commands);
    }

    [TestMethod]
    public async Task CacheIsReacquiredAtTwoMinuteBoundaryAndAfterExpiry()
    {
        var clock = new FakeClock();
        var runner = new FakeRunner(Success(expires: clock.Now.AddMinutes(3)), Success("second", clock.Now.AddMinutes(8)), Success("third", clock.Now.AddHours(1)));
        var provider = new AzureCliAccessTokenProvider(runner, clock);
        await provider.GetTokenAsync(false, default);
        clock.Now = clock.Now.AddMinutes(1);
        Assert.AreEqual("second", (await provider.GetTokenAsync(false, default)).Token);
        clock.Now = clock.Now.AddMinutes(10);
        Assert.AreEqual("third", (await provider.GetTokenAsync(false, default)).Token);
        Assert.HasCount(3, runner.Commands);
    }

    [TestMethod]
    public async Task ParallelRequestsLaunchOneLogin()
    {
        var runner = new FakeRunner(MissingLogin(), new(0), Success()) { Delay = true };
        var provider = new AzureCliAccessTokenProvider(runner);
        var tokens = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => provider.GetTokenAsync(true, default)));
        Assert.IsTrue(tokens.All(token => token.Token == "secret-token"));
        Assert.AreEqual(1, runner.Commands.Count(command => command == AzureCliCommand.Login));
        Assert.HasCount(3, runner.Commands);
    }

    [TestMethod]
    public async Task LoginFailureDoesNotRetryLoginOrAcquireToken()
    {
        var runner = new FakeRunner(MissingLogin(), new(1, "secret-token", "AADSTS53003 blocked secret-token"));
        var error = await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => new AzureCliAccessTokenProvider(runner).GetTokenAsync(true, default));
        Assert.AreEqual(AzureCliAuthenticationFailure.AccessDenied, error.Failure);
        Assert.HasCount(2, runner.Commands);
        Assert.IsFalse(error.ToString().Contains("secret-token", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task StillMissingAfterSuccessfulLoginDoesNotLoop()
    {
        var runner = new FakeRunner(MissingLogin(), new(0), MissingLogin());
        var error = await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => new AzureCliAccessTokenProvider(runner).GetTokenAsync(true, default));
        Assert.AreEqual(AzureCliAuthenticationFailure.LoginRequired, error.Failure);
        Assert.HasCount(3, runner.Commands);
        Assert.AreEqual(1, runner.Commands.Count(command => command == AzureCliCommand.Login));
    }

    [TestMethod]
    public async Task FailedExplicitReconnectRemainsDisconnected()
    {
        var runner = new FakeRunner(new AzureCliResult(0, "bad-json secret-token"));
        var provider = new AzureCliAccessTokenProvider(runner);
        await provider.SignOutAsync(default);
        await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => provider.GetTokenAsync(true, default));
        var error = await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => provider.GetTokenAsync(false, default));
        Assert.AreEqual(AzureCliAuthenticationFailure.Disconnected, error.Failure);
        Assert.HasCount(1, runner.Commands);
    }

    [TestMethod]
    public async Task FailedRefreshCannotReusePreviousMemoryToken()
    {
        var runner = new FakeRunner(Success(), new(0, "bad-json"), Success("reacquired"));
        var provider = new AzureCliAccessTokenProvider(runner);
        await provider.GetTokenAsync(false, default);
        await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => provider.RefreshTokenAsync(default));
        Assert.AreEqual("reacquired", (await provider.GetTokenAsync(false, default)).Token);
        Assert.HasCount(3, runner.Commands);
    }

    [TestMethod]
    public async Task NearExpiryTokenIsReturnedButNotReusedFromMemory()
    {
        var runner = new FakeRunner(Success(expires: DateTimeOffset.UtcNow.AddSeconds(30)), Success("fresh"));
        var provider = new AzureCliAccessTokenProvider(runner);
        await provider.GetTokenAsync(false, default);
        Assert.AreEqual("fresh", (await provider.GetTokenAsync(false, default)).Token);
        Assert.HasCount(2, runner.Commands);
    }

    [TestMethod]
    public async Task CancellationWhileWaitingForGateDoesNotStartAnotherCliRequest()
    {
        var runner = new FakeRunner(Success()) { CancelFirst = true };
        var provider = new AzureCliAccessTokenProvider(runner);
        using var firstCancellation = new CancellationTokenSource();
        using var queuedCancellation = new CancellationTokenSource();
        var active = provider.GetTokenAsync(true, firstCancellation.Token);
        await runner.Started.Task;
        var queued = provider.GetTokenAsync(true, queuedCancellation.Token);
        queuedCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
        Assert.HasCount(1, runner.Commands);
        firstCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => active);
    }

    [TestMethod]
    [DataRow("AADSTS53003 Conditional Access blocked secret-token", AzureCliAuthenticationFailure.AccessDenied)]
    [DataRow("AuthorizationFailed secret-token", AzureCliAuthenticationFailure.AccessDenied)]
    [DataRow("network timeout secret-token", AzureCliAuthenticationFailure.CliFailure)]
    [DataRow("unknown error secret-token", AzureCliAuthenticationFailure.CliFailure)]
    [DataRow("network timeout Please run 'az login' to setup account.", AzureCliAuthenticationFailure.CliFailure)]
    [DataRow("AADSTS53000 Please run 'az login' to setup account.", AzureCliAuthenticationFailure.AccessDenied)]
    public async Task PolicyAndNetworkErrorsNeverFallBackToLogin(string stderr, AzureCliAuthenticationFailure expected)
    {
        var runner = new FakeRunner(new AzureCliResult(1, "secret-token", stderr));
        var error = await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => new AzureCliAccessTokenProvider(runner).GetTokenAsync(true, default));
        Assert.AreEqual(expected, error.Failure);
        Assert.IsFalse(error.ToString().Contains("secret-token", StringComparison.Ordinal));
        Assert.HasCount(1, runner.Commands);
    }

    [TestMethod]
    [DataRow("not-json secret-token")]
    [DataRow("{\"accessToken\":\"secret-token\",\"tokenType\":\"Basic\",\"expires_on\":9999999999}")]
    [DataRow("{\"accessToken\":\"\",\"tokenType\":\"Bearer\",\"expires_on\":9999999999}")]
    [DataRow("{\"accessToken\":\"secret-token\",\"tokenType\":\"Bearer\",\"expires_on\":0}")]
    [DataRow("{\"accessToken\":\"secret-token\",\"tokenType\":\"Bearer\",\"expires_on\":\"bad\",\"expiresOn\":\"2099-01-01 00:00:00.000000\"}")]
    [DataRow("{\"accessToken\":\"secret-token\",\"tokenType\":\"Bearer\",\"expiresOn\":\"01/02/2099\"}")]
    [DataRow("{\"accessToken\":\"secret-token\",\"tokenType\":\"Bearer\"}")]
    [DataRow("[]")]
    [DataRow("{\"accessToken\":\"secret-token\",\"tokenType\":\"Basic\",\"tokenType\":\"Bearer\",\"expires_on\":9999999999}")]
    public async Task InvalidMetadataIsRedactedAndNeverTriggersLogin(string json)
    {
        var runner = new FakeRunner(new AzureCliResult(0, json));
        var error = await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => new AzureCliAccessTokenProvider(runner).GetTokenAsync(true, default));
        Assert.AreEqual(AzureCliAuthenticationFailure.InvalidToken, error.Failure);
        Assert.IsFalse(error.ToString().Contains("secret-token", StringComparison.Ordinal));
        Assert.HasCount(1, runner.Commands);
    }

    [TestMethod]
    public async Task UnixExpiryIsUtcAndPreferredOverLegacyLocalExpiry()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var json = JsonSerializer.Serialize(new { accessToken = "secret-token", tokenType = "bearer", expires_on = expiry.ToString(), expiresOn = "bad" });
        var token = await new AzureCliAccessTokenProvider(new FakeRunner(new AzureCliResult(0, json))).GetTokenAsync(false, default);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(expiry), token.ExpiresAt);
        Assert.AreEqual(TimeSpan.Zero, token.ExpiresAt.Offset);
    }

    [TestMethod]
    public async Task KnownLegacyLocalExpiryIsConvertedSafely()
    {
        var local = DateTime.Now.AddHours(1);
        var json = JsonSerializer.Serialize(new { accessToken = "secret-token", tokenType = "Bearer", expiresOn = local.ToString("yyyy-MM-dd HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture) });
        var token = await new AzureCliAccessTokenProvider(new FakeRunner(new AzureCliResult(0, json))).GetTokenAsync(false, default);
        Assert.IsTrue(Math.Abs((token.ExpiresAt - local.ToUniversalTime()).TotalSeconds) < 1);
        Assert.AreEqual(TimeSpan.Zero, token.ExpiresAt.Offset);
    }

    [TestMethod]
    public async Task MissingCliAndRunnerExceptionsAreSanitized()
    {
        var missing = new FakeRunner { Error = new AzureCliAuthenticationException(AzureCliAuthenticationFailure.NotInstalled) };
        var error = await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => new AzureCliAccessTokenProvider(missing).GetTokenAsync(true, default));
        Assert.AreEqual(AzureCliAuthenticationFailure.NotInstalled, error.Failure);
        var unsafeRunner = new FakeRunner { Error = new IOException("secret-token") };
        error = await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => new AzureCliAccessTokenProvider(unsafeRunner).GetTokenAsync(true, default));
        Assert.IsFalse(error.ToString().Contains("secret-token", StringComparison.Ordinal));
        Assert.IsNull(error.InnerException);
    }

    [TestMethod]
    public async Task CancellationReleasesProviderGateWithoutLogin()
    {
        var runner = new FakeRunner(Success()) { CancelFirst = true };
        var provider = new AzureCliAccessTokenProvider(runner);
        using var cancellation = new CancellationTokenSource();
        var pending = provider.GetTokenAsync(true, cancellation.Token);
        await runner.Started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.AreEqual("secret-token", (await provider.GetTokenAsync(false, default)).Token);
        Assert.IsTrue(runner.Commands.All(command => command == AzureCliCommand.GetAccessToken));
    }

    [TestMethod]
    public void CliResultToStringRedactsBothStreams()
    {
        var result = new AzureCliResult(1, "secret-token", "secret-error");
        Assert.IsFalse(result.ToString().Contains("secret-token", StringComparison.Ordinal));
        Assert.IsFalse(result.ToString().Contains("secret-error", StringComparison.Ordinal));
    }

    private static AzureCliResult MissingLogin() => new(1, "", "ERROR: Please run 'az login' to setup account.");
    private static AzureCliResult InteractionRequired(string code) => new(1, "secret-token",
        $"ERROR: AADSTS{code}: Interactive authentication is needed. Please run: az login private-cli-error");
    private static AzureCliResult Success(string token = "secret-token", DateTimeOffset? expires = null) => new(0,
        JsonSerializer.Serialize(new { accessToken = token, tokenType = "Bearer", expires_on = (expires ?? DateTimeOffset.UtcNow.AddHours(1)).ToUnixTimeSeconds() }));

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestMethod]
    public async Task TokenProcessUsesFixedResourceAndChildEnvironmentAndCapturesInMemory()
    {
        var process = new FakeProcess();
        var factory = new FakeProcessFactory(process);
        var runner = Runner(factory, @"C:\Tools\Azure CLI\az.exe");
        var result = await runner.RunAsync(AzureCliCommand.GetAccessToken, default);
        var start = factory.StartInfo!;
        Assert.AreEqual(@"C:\Tools\Azure CLI\az.exe", start.FileName);
        CollectionAssert.AreEqual(new[] { "account", "get-access-token", "--resource", "499b84ac-1321-427f-aa17-267ca6975798", "--output", "json", "--only-show-errors" }, start.ArgumentList.ToArray());
        Assert.IsTrue(start.CreateNoWindow);
        Assert.IsFalse(start.UseShellExecute);
        Assert.IsTrue(start.RedirectStandardOutput && start.RedirectStandardError && start.RedirectStandardInput);
        Assert.AreEqual("off", start.Environment["AZURE_CORE_LOGIN_EXPERIENCE_V2"]);
        Assert.AreEqual("false", start.Environment["AZURE_CORE_ENABLE_BROKER_ON_WINDOWS"]);
        Assert.AreEqual(Environment.SystemDirectory, start.WorkingDirectory);
        Assert.IsTrue(process.InputClosed && process.OutputDrained && process.ErrorDrained && process.Disposed);
        Assert.AreEqual("captured-token", result.StandardOutput);
    }

    [TestMethod]
    public async Task LoginProcessIsVisibleWithNoRedirectedPromptsOrSubscriptionSelector()
    {
        var process = new FakeProcess();
        var factory = new FakeProcessFactory(process);
        await Runner(factory, @"C:\Tools\Azure CLI\az.exe").RunAsync(AzureCliCommand.Login, default);
        var start = factory.StartInfo!;
        CollectionAssert.AreEqual(new[] { "login", "--allow-no-subscriptions", "--output", "none", "--only-show-errors" }, start.ArgumentList.ToArray());
        Assert.IsFalse(start.CreateNoWindow);
        Assert.IsFalse(start.RedirectStandardOutput || start.RedirectStandardError || start.RedirectStandardInput);
        Assert.IsFalse(process.InputClosed || process.OutputDrained || process.ErrorDrained);
        Assert.AreEqual("off", start.Environment["AZURE_CORE_LOGIN_EXPERIENCE_V2"]);
    }

    [TestMethod]
    public async Task CmdWithSpacesAndParenthesesUsesCanonicalSystemCmdAndFixedQuotedArguments()
    {
        var factory = new FakeProcessFactory(new FakeProcess());
        var cli = @"C:\Program Files (x86)\Microsoft SDKs\Azure\CLI2\wbin\az.cmd";
        await Runner(factory, cli).RunAsync(AzureCliCommand.GetAccessToken, default);
        var start = factory.StartInfo!;
        Assert.AreEqual(Path.Combine(Environment.SystemDirectory, "cmd.exe"), start.FileName);
        Assert.AreEqual($"/d /v:off /s /c \"\"{cli}\" account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 --output json --only-show-errors\"", start.Arguments);
        Assert.HasCount(0, start.ArgumentList);
    }

    [TestMethod]
    public async Task BundledPythonUsesIsolatedAzureCliModuleInsteadOfCmd()
    {
        var cli = @"C:\Program Files (x86)\Microsoft SDKs\Azure\CLI2\wbin\az.cmd";
        var python = @"C:\Program Files (x86)\Microsoft SDKs\Azure\CLI2\python.exe";
        var factory = new FakeProcessFactory(new FakeProcess());
        var runner = new AzureCliProcessRunner(factory, () => [cli], path => path == cli || path == python);
        await runner.RunAsync(AzureCliCommand.GetAccessToken, default);
        Assert.AreEqual(python, factory.StartInfo!.FileName);
        CollectionAssert.AreEqual(new[] { "-I", "-m", "azure.cli", "account", "get-access-token", "--resource", "499b84ac-1321-427f-aa17-267ca6975798", "--output", "json", "--only-show-errors" }, factory.StartInfo.ArgumentList.ToArray());
    }

    [TestMethod]
    [DataRow("az.cmd")]
    [DataRow(".\\az.cmd")]
    [DataRow("C:\\Tools\\bad&path\\az.cmd")]
    [DataRow("C:\\Tools\\bad%PATH%\\az.cmd")]
    [DataRow("C:\\Tools\\bad!path\\az.cmd")]
    [DataRow("C:\\Tools\\bad\"path\\az.cmd")]
    [DataRow("C:\\Tools\\not-az.cmd")]
    [DataRow("\\\\server\\share\\az.cmd")]
    public async Task UnsafeOrRelativeCliPathsFailClosedBeforeProcessStart(string cli)
    {
        var factory = new FakeProcessFactory(new FakeProcess());
        var error = await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => Runner(factory, cli).RunAsync(AzureCliCommand.Login, default));
        Assert.AreEqual(AzureCliAuthenticationFailure.NotInstalled, error.Failure);
        Assert.IsNull(factory.StartInfo);
        Assert.IsFalse(error.ToString().Contains(cli, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CurrentDirectoryCliIsNeverExecutedEvenWhenCandidateExists()
    {
        var factory = new FakeProcessFactory(new FakeProcess());
        var cli = Path.Combine(Environment.CurrentDirectory, "az.exe");
        await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => Runner(factory, cli).RunAsync(AzureCliCommand.GetAccessToken, default));
        Assert.IsNull(factory.StartInfo);
    }

    [TestMethod]
    public async Task MissingCliDoesNotLaunchAnyProcess()
    {
        var factory = new FakeProcessFactory(new FakeProcess());
        var runner = new AzureCliProcessRunner(factory, () => [@"C:\Tools\az.exe"], _ => false);
        var error = await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => runner.RunAsync(AzureCliCommand.GetAccessToken, default));
        Assert.AreEqual(AzureCliAuthenticationFailure.NotInstalled, error.Failure);
        Assert.IsNull(factory.StartInfo);
    }

    [TestMethod]
    public async Task ProcessCancellationKillsTreeDrainsBothStreamsAndDisposes()
    {
        var process = new FakeProcess { Block = true };
        var factory = new FakeProcessFactory(process);
        using var cancellation = new CancellationTokenSource();
        var pending = Runner(factory, @"C:\Tools\az.exe").RunAsync(AzureCliCommand.GetAccessToken, cancellation.Token);
        await process.WaitStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.IsTrue(process.Killed && process.OutputDrained && process.ErrorDrained && process.Disposed);
        Assert.IsTrue(process.WaitedAfterKill);
    }

    [TestMethod]
    public async Task InteractiveCancellationKillsTreeWithoutCapturingPrompts()
    {
        var process = new FakeProcess { Block = true };
        using var cancellation = new CancellationTokenSource();
        var pending = Runner(new FakeProcessFactory(process), @"C:\Tools\az.exe").RunAsync(AzureCliCommand.Login, cancellation.Token);
        await process.WaitStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.IsTrue(process.Killed && process.Disposed);
        Assert.IsFalse(process.OutputDrained || process.ErrorDrained);
    }

    [TestMethod]
    public async Task ProcessLaunchFailureIsSanitizedWithoutInnerException()
    {
        var factory = new FakeProcessFactory(new FakeProcess()) { Error = new IOException("captured-token") };
        var error = await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => Runner(factory, @"C:\Tools\az.exe").RunAsync(AzureCliCommand.GetAccessToken, default));
        Assert.IsFalse(error.ToString().Contains("captured-token", StringComparison.Ordinal));
        Assert.IsNull(error.InnerException);
    }

    [TestMethod]
    public async Task ProcessWaitFailureKillsAndDrainsWithoutLeakingException()
    {
        var process = new FakeProcess { WaitError = new IOException("captured-token") };
        var error = await Assert.ThrowsAsync<AzureCliAuthenticationException>(() => Runner(new FakeProcessFactory(process), @"C:\Tools\az.exe").RunAsync(AzureCliCommand.GetAccessToken, default));
        Assert.IsFalse(error.ToString().Contains("captured-token", StringComparison.Ordinal));
        Assert.IsTrue(process.Killed && process.WaitedAfterKill && process.OutputDrained && process.ErrorDrained && process.Disposed);
    }

    [TestMethod]
    public async Task PrecancelledProcessRequestNeverStartsOrDiscoversCli()
    {
        var factory = new FakeProcessFactory(new FakeProcess());
        var runner = new AzureCliProcessRunner(factory, () => throw new IOException("must not discover"), _ => true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(AzureCliCommand.Login, cancellation.Token));
        Assert.IsNull(factory.StartInfo);
    }

    [TestMethod]
    public async Task ProcessChildEnvironmentDoesNotModifyGlobalCliConfiguration()
    {
        var loginExperience = Environment.GetEnvironmentVariable("AZURE_CORE_LOGIN_EXPERIENCE_V2");
        var broker = Environment.GetEnvironmentVariable("AZURE_CORE_ENABLE_BROKER_ON_WINDOWS");
        await Runner(new FakeProcessFactory(new FakeProcess()), @"C:\Tools\az.exe").RunAsync(AzureCliCommand.Login, default);
        Assert.AreEqual(loginExperience, Environment.GetEnvironmentVariable("AZURE_CORE_LOGIN_EXPERIENCE_V2"));
        Assert.AreEqual(broker, Environment.GetEnvironmentVariable("AZURE_CORE_ENABLE_BROKER_ON_WINDOWS"));
    }

    private static AzureCliProcessRunner Runner(FakeProcessFactory factory, string cli) => new(factory, () => [cli], path => path == cli);

    private sealed class FakeProcessFactory(FakeProcess process) : IAzureCliProcessFactory
    {
        public ProcessStartInfo? StartInfo { get; private set; }
        public Exception? Error { get; init; }
        public IAzureCliProcess Start(ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
            if (Error is not null) throw Error;
            return process;
        }
    }

    private sealed class FakeProcess : IAzureCliProcess
    {
        private readonly TaskCompletionSource exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ExitCode => 0;
        public bool Block { get; init; }
        public Exception? WaitError { get; init; }
        public bool Killed { get; private set; }
        public bool OutputDrained { get; private set; }
        public bool ErrorDrained { get; private set; }
        public bool InputClosed { get; private set; }
        public bool Disposed { get; private set; }
        public bool WaitedAfterKill { get; private set; }
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> ReadStandardOutputAsync()
        {
            if (Block) await exit.Task;
            OutputDrained = true;
            return "captured-token";
        }
        public async Task<string> ReadStandardErrorAsync()
        {
            if (Block) await exit.Task;
            ErrorDrained = true;
            return "captured-error";
        }
        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitStarted.TrySetResult();
            if (Killed) WaitedAfterKill = true;
            if (!Killed && WaitError is not null) throw WaitError;
            if (Block) await exit.Task.WaitAsync(cancellationToken);
        }
        public void CloseStandardInput() => InputClosed = true;
        public void KillTree() { Killed = true; exit.TrySetResult(); }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeRunner(params AzureCliResult[] results) : IAzureCliRunner
    {
        private readonly Queue<AzureCliResult> remaining = new(results);
        public List<AzureCliCommand> Commands { get; } = [];
        public Exception? Error { get; init; }
        public bool Delay { get; init; }
        public bool CancelFirst { get; init; }
        public int CancelOnCommandCount { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AzureCliResult> RunAsync(AzureCliCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            Started.TrySetResult();
            if (Commands.Count == CancelOnCommandCount)
            {
                var result = remaining.Dequeue();
                CancellationStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return result;
            }
            if (CancelFirst && Commands.Count == 1) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Delay) await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (Error is not null) throw Error;
            return remaining.Dequeue();
        }
    }
}