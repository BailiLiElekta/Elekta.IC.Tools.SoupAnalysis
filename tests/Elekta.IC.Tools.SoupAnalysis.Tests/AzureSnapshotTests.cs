using System.Net;
using System.Text;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.AzureDevOps;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Elekta.IC.Tools.SoupAnalysis.Tests;

[TestClass]
public sealed class AzureSnapshotTests
{
    [TestMethod]
    public async Task UnauthorizedThroughReloadingAdapterForcesRefresh()
    {
        var inner = new AzureRenewableTokens();
        var wrapped = new Elekta.IC.Tools.SoupAnalysis.Desktop.Presentation.ReloadingAccessTokenProvider(new WrapperConfiguration(), _ => inner);
        var attempts = 0;
        using var http = new HttpClient(new AzureAdapterHandler(request =>
        {
            attempts++;
            Assert.AreEqual(attempts == 1 ? "local-test-token" : "fresh-test-token", request.Headers.Authorization!.Parameter);
            return new(attempts == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden);
        }));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new AzureReposClient(http, wrapped).GetSnapshotAsync(TestData.Repo("client"), null, default));
        Assert.AreEqual(1, inner.Renewals);
        Assert.AreEqual(2, attempts);
    }

    private sealed class WrapperConfiguration : IConfigurationService
    {
        public Task<LoadedConfiguration> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new LoadedConfiguration(
            new(1, "1", [TestData.Repo("platform", RepositoryCategory.Platform)]), "hash", AnalysisRules.Default,
            new(1, "tenant", "client", "http://localhost", ["scope"])));
    }

    [TestMethod]
    public void UrlFormsNormalizeAndUnsafePathsFail()
    {
        var old = AzureRepoUrl.Parse("https://org.visualstudio.com/project/_git/repo?version=GBmain");
        var modern = AzureRepoUrl.Parse("https://dev.azure.com/org/project/_git/repo");
        Assert.AreEqual(modern, old);
        Assert.AreEqual("https://dev.azure.com/org/project/_git/repo", old.NormalizedRepoUrl);
        foreach (var url in new[] { "http://dev.azure.com/org/p/_git/r", "https://user:secret@dev.azure.com/org/p/_git/r",
            "https://dev.azure.com/org/p/_git/%2e%2e", "https://dev.azure.com/org/p/_git/a%2fb", "https://evil.test/org/p/_git/r" })
            Assert.ThrowsExactly<InvalidDataException>(() => AzureRepoUrl.Parse(url));
        Assert.ThrowsExactly<InvalidDataException>(() => AzureRepoUrl.ValidateRef(RefKind.Branch, "main~1"));
    }

    [TestMethod]
    public async Task AnnotatedTagIsPeeledAndEveryReadPinsCommitAndIncludesRootImports()
    {
        var commit = new string('a', 40);
        var tag = new string('b', 40);
        var urls = new List<string>();
        using var http = new HttpClient(new AzureAdapterHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            urls.Add(url);
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            if (url.Contains("/refs?")) return AzureAdapterHandler.Json(System.Text.Json.JsonSerializer.Serialize(new { value = new[] { new { name = "refs/tags/release/a+b", objectId = tag } } }));
            if (url.Contains("/annotatedtags/")) return AzureAdapterHandler.Json(System.Text.Json.JsonSerializer.Serialize(new { taggedObject = new { objectId = commit, objectType = "commit" } }));
            if (!url.Contains("/items?")) return AzureAdapterHandler.Json("{\"id\":\"11111111-1111-1111-1111-111111111111\"}");
            StringAssert.Contains(url, "versionDescriptor.versionType=commit");
            StringAssert.Contains(url, "versionDescriptor.version=" + commit);
            if (url.Contains("recursionLevel=Full")) return AzureAdapterHandler.Json("{\"value\":[{\"path\":\"/Directory.Build.props\",\"isFolder\":false},{\"path\":\"/src/A/A.csproj\",\"isFolder\":false},{\"path\":\"/src/obj/skip.csproj\",\"isFolder\":false},{\"path\":\"/custom.versions\",\"isFolder\":false}]}");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(url.Contains("Directory.Build.props")
                ? "<Project><Import Project=\"custom.versions\" /></Project>" : "<Project />", Encoding.UTF8, "application/xml") };
        }));
        var provider = new AzureReposClient(http, new AzureAdapterTokens());
        var source = new RepositorySource("id", "repo", "https://org.visualstudio.com/project/_git/repo", RepositoryCategory.Platform, RefKind.Tag, "release/a+b");
        var snapshot = await provider.GetSnapshotAsync(source, null, default);
        Assert.AreEqual(commit, snapshot.Commit);
        CollectionAssert.AreEquivalent(new[] { "Directory.Build.props", "src/A/A.csproj", "custom.versions" }, snapshot.Files.Select(file => file.Path).ToArray());
        Assert.IsTrue(urls.Any(url => url.Contains("tags%2Frelease%2Fa%2Bb", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task RetryIsBoundedAndCancellationIsObserved()
    {
        var requests = 0;
        using var http = new HttpClient(new AzureAdapterHandler(_ => { requests++; return new(HttpStatusCode.TooManyRequests); }));
        var provider = new AzureReposClient(http, new AzureAdapterTokens(), retryDelay: TimeSpan.Zero);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => provider.GetSnapshotAsync(TestData.Repo("client"), null, default));
        Assert.AreEqual(4, requests);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.GetSnapshotAsync(TestData.Repo("client"), null, cancelled.Token));
    }

    [TestMethod]
    public async Task BranchAlwaysResolvesAgainAndCorruptCacheCannotSupplyFiles()
    {
        using var fixture = new AdapterSettingsFixture();
        var tip = new string('a', 40);
        var resolutions = 0;
        var downloads = 0;
        using var http = new HttpClient(new AzureAdapterHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/refs?"))
            {
                resolutions++;
                return AzureAdapterHandler.Json(System.Text.Json.JsonSerializer.Serialize(new { value = new[] { new { name = "refs/heads/main", objectId = tip } } }));
            }
            if (!url.Contains("/items?")) return AzureAdapterHandler.Json("{\"id\":\"11111111-1111-1111-1111-111111111111\"}");
            if (url.Contains("recursionLevel=Full")) return AzureAdapterHandler.Json("{\"value\":[{\"path\":\"/src/App.csproj\",\"isFolder\":false}]}");
            downloads++;
            return new(HttpStatusCode.OK) { Content = new StringContent("<Project />") };
        }));
        var client = new AzureReposClient(http, new AzureAdapterTokens(), cacheDirectory: fixture.Directory);
        var first = await client.GetSnapshotAsync(TestData.Repo("client"), null, default);
        _ = await client.GetSnapshotAsync(TestData.Repo("client"), null, default);
        Assert.AreEqual(1, downloads);
        foreach (var file in Directory.GetFiles(fixture.Directory)) await File.WriteAllTextAsync(file, "{\"content\":null}");
        _ = await client.GetSnapshotAsync(TestData.Repo("client"), null, default);
        Assert.AreEqual(2, downloads);
        tip = new string('c', 40);
        var latest = await client.GetSnapshotAsync(TestData.Repo("client"), null, default);
        Assert.AreNotEqual(first.Commit, latest.Commit);
        Assert.AreEqual(4, resolutions);
        Assert.AreEqual(3, downloads);
    }

    [TestMethod]
    public async Task UnauthorizedResponseRenewsWithoutInteractivePrompt()
    {
        var attempts = 0;
        var tokens = new AzureRenewableTokens();
        using var http = new HttpClient(new AzureAdapterHandler(_ =>
        {
            attempts++;
            return new(attempts == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden);
        }));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new AzureReposClient(http, tokens).GetSnapshotAsync(TestData.Repo("client"), null, default));
        Assert.AreEqual(1, tokens.Renewals);
        Assert.AreEqual(2, attempts);
    }

    [TestMethod]
    public async Task CrossFileImportPropertiesAndRecursiveCustomExtensionsAreFetched()
    {
        var paths = new[] { "Directory.Build.props", "src/App.csproj", "custom/first.versions", "custom/second.versions" };
        using var http = new HttpClient(new AzureAdapterHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/refs?")) return AzureAdapterHandler.Json(System.Text.Json.JsonSerializer.Serialize(new { value = new[] { new { name = "refs/heads/main", objectId = new string('a', 40) } } }));
            if (!url.Contains("/items?")) return AzureAdapterHandler.Json("{\"id\":\"11111111-1111-1111-1111-111111111111\"}");
            if (url.Contains("recursionLevel=Full")) return AzureAdapterHandler.Json(System.Text.Json.JsonSerializer.Serialize(new { value = paths.Select(path => new { path = "/" + path, isFolder = false }) }));
            var content = url.Contains("Directory.Build.props") ? "<Project><PropertyGroup><Root>$(MSBuildThisFileDirectory)custom/</Root></PropertyGroup></Project>"
                : url.Contains("App.csproj") ? "<Project><Import Project=\"$(Root)first.versions\"/></Project>"
                : url.Contains("first.versions") ? "<Project><Import Project=\"second.versions\"/></Project>" : "<Project/>";
            return new(HttpStatusCode.OK) { Content = new StringContent(content) };
        }));
        var snapshot = await new AzureReposClient(http, new AzureAdapterTokens()).GetSnapshotAsync(TestData.Repo("client"), null, default);
        CollectionAssert.AreEquivalent(paths, snapshot.Files.Select(file => file.Path).ToArray());
        var inventory = new Elekta.IC.Tools.SoupAnalysis.Infrastructure.Packages.PackageReferenceScanner().Scan(snapshot, AnalysisRules.Default, default);
        Assert.HasCount(0, inventory.Issues);
    }

    [TestMethod]
    public async Task TransientNetworkFailuresAreRetriedWithoutLeakingExceptionDetails()
    {
        var attempts = 0;
        using var http = new HttpClient(new AzureAdapterHandler(_ =>
        {
            attempts++;
            throw new HttpRequestException("private diagnostic deliberately not surfaced");
        }));
        var failure = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new AzureReposClient(http, new AzureAdapterTokens(), retryDelay: TimeSpan.Zero).GetSnapshotAsync(TestData.Repo("client"), null, default));
        Assert.AreEqual(4, attempts);
        Assert.IsFalse(failure.Message.Contains("private diagnostic", StringComparison.Ordinal));
    }
}

internal sealed class AzureAdapterHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(respond(request));
    }
    internal static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed class AzureAdapterTokens : IAccessTokenProvider
{
    public Task<AccessToken> GetTokenAsync(bool allowInteractive, CancellationToken cancellationToken)
    {
        Assert.IsFalse(allowInteractive);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AccessToken("fake-local-only", DateTimeOffset.UtcNow.AddHours(1)));
    }
    public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class AzureRenewableTokens : Elekta.IC.Tools.SoupAnalysis.Infrastructure.Authentication.IRefreshableAccessTokenProvider
{
    public int Renewals { get; private set; }
    public Task<AccessToken> GetTokenAsync(bool allowInteractive, CancellationToken cancellationToken)
    {
        Assert.IsFalse(allowInteractive);
        return Task.FromResult(new AccessToken("local-test-token", DateTimeOffset.UtcNow.AddHours(1)));
    }
    public Task<AccessToken> RefreshTokenAsync(CancellationToken cancellationToken)
    {
        Renewals++;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AccessToken("fresh-test-token", DateTimeOffset.UtcNow.AddHours(1)));
    }
    public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}