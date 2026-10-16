using System.Net;
using System.Text;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.NuGet;

namespace Elekta.IC.Tools.SoupAnalysis.Tests;

[TestClass]
public sealed class NuGetCandidateTests
{
    [TestMethod]
    public async Task PagesExcludeUnlistedAndPrereleaseAndChooseHighestFormalVersion()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v3/index.json" => Json("""{"resources":[{"@id":"https://api.nuget.org/registration/","@type":"RegistrationsBaseUrl/3.6.0"}]}"""),
            "/registration/example/index.json" => Json("""{"items":[{"@id":"https://api.nuget.org/page.json"}]}"""),
            "/page.json" => Json("""{"items":[{"catalogEntry":{"version":"9.0.0","listed":false}},{"catalogEntry":{"version":"2.0.0-rc.10","listed":true}},{"catalogEntry":{"version":"2.0.0-rc.2","listed":true}},{"catalogEntry":{"version":"1.8.0+build.42","listed":true}},{"catalogEntry":{"version":"1.7.0","listed":true}}]}"""),
            _ => throw new InvalidOperationException("Unexpected endpoint")
        });
        using var client = new HttpClient(handler);
        var before = DateTimeOffset.UtcNow;
        var candidate = await new NuGetCandidateProvider(client).GetCandidateAsync("Example", default);
        Assert.AreEqual(CandidateStatus.Available, candidate.Status);
        Assert.AreEqual("1.8.0", candidate.Version!.ToNormalizedString());
        Assert.IsFalse(candidate.Version.IsPrerelease);
        Assert.AreEqual("build.42", candidate.Version.Metadata);
        Assert.IsTrue(candidate.QueriedAt >= before);
        Assert.AreEqual("https://api.nuget.org/v3/index.json", candidate.Source);
        Assert.AreEqual(3, handler.Calls);
    }

    [TestMethod]
    public async Task Registration404MeansNotFound()
    {
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath == "/v3/index.json"
            ? Index() : new(HttpStatusCode.NotFound)));
        Assert.AreEqual(CandidateStatus.NotFound,
            (await new NuGetCandidateProvider(client).GetCandidateAsync("missing", default)).Status);
    }

    [TestMethod]
    public async Task ServiceErrorIsFailedAndRetriesAreBounded()
    {
        var handler = new Handler(_ => new(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        var candidate = await new NuGetCandidateProvider(client).GetCandidateAsync("example", default);
        Assert.AreEqual(CandidateStatus.Failed, candidate.Status);
        Assert.IsNotNull(candidate.Detail);
        Assert.AreEqual(3, handler.Calls);
    }

    [TestMethod]
    public async Task MalformedListingMetadataFailsInsteadOfGuessing()
    {
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath == "/v3/index.json"
            ? Index() : Json("""{"items":[{"items":[{"catalogEntry":{"version":"1.0.0","listed":"invalid"}}]}]}""")));
        Assert.AreEqual(CandidateStatus.Failed,
            (await new NuGetCandidateProvider(client).GetCandidateAsync("example", default)).Status);
    }

    [TestMethod]
    public async Task AbsentListedDefaultsToTrueAndExplicitUnlistedRemainsExcluded()
    {
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath == "/v3/index.json"
            ? Index() : Json("""{"items":[{"items":[{"catalogEntry":{"version":"1.0.0"}},{"catalogEntry":{"version":"9.0.0","listed":false}}]}]}""")));
        var result = await new NuGetCandidateProvider(client).GetCandidateAsync("example", default);
        Assert.AreEqual(CandidateStatus.Available, result.Status);
        Assert.AreEqual("1.0.0", result.Version!.ToNormalizedString());
    }

    [TestMethod]
    public async Task CancellationIsNotConvertedToFailedCandidate()
    {
        using var client = new HttpClient(new Handler(_ => Index()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new NuGetCandidateProvider(client).GetCandidateAsync("example", cancellation.Token));
    }

    [TestMethod]
    public void RejectsAuthenticatedClient()
    {
        using var client = new HttpClient(new Handler(_ => Index()));
        client.DefaultRequestHeaders.Authorization = new("Bearer", "azure-secret");
        Assert.Throws<ArgumentException>(() => new NuGetCandidateProvider(client));
    }

    [TestMethod]
    public async Task RejectsMetadataLinksOutsidePublicNuGet()
    {
        var handler = new Handler(_ => Json("""{"resources":[{"@id":"https://evil.example/","@type":"RegistrationsBaseUrl/3.6.0"}]}"""));
        using var client = new HttpClient(handler);
        Assert.AreEqual(CandidateStatus.Failed,
            (await new NuGetCandidateProvider(client).GetCandidateAsync("example", default)).Status);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task AuthenticationAddedAfterConstructionStillCannotLeak()
    {
        var handler = new Handler(_ => Index());
        using var client = new HttpClient(handler);
        var provider = new NuGetCandidateProvider(client);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "azure-secret");
        Assert.AreEqual(CandidateStatus.Failed, (await provider.GetCandidateAsync("example", default)).Status);
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public async Task PrereleaseOnlyRegistrationHasNoFormalVersion()
    {
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath == "/v3/index.json"
            ? Index() : Json("""{"items":[{"catalogEntry":{"version":"3.0.0-rc.1","listed":true}}]}""")));
        var candidate = await new NuGetCandidateProvider(client).GetCandidateAsync("example", default);
        Assert.AreEqual(CandidateStatus.NotFound, candidate.Status);
        Assert.IsNull(candidate.Version);
        Assert.AreEqual("No formal version available.", candidate.Detail);
    }

    [TestMethod]
    [DataRow(301, "/redirected.json")]
    [DataRow(302, "redirected.json")]
    [DataRow(303, "https://api.nuget.org/redirected.json")]
    [DataRow(307, "/redirected.json")]
    [DataRow(308, "/redirected.json")]
    public async Task PublicPageRedirectPreservesListedStableSemver2Selection(int status, string location)
    {
        var content = new TrackedContent();
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v3/index.json" => Index(),
            "/registration/example/index.json" => Json("""{"items":[{"@id":"https://api.nuget.org/page.json"}]}"""),
            "/page.json" => Redirect(status, location, content),
            "/redirected.json" => Json("""{"items":[{"catalogEntry":{"version":"9.0.0","listed":false}},{"catalogEntry":{"version":"8.0.0-rc.10","listed":true}},{"catalogEntry":{"version":"2.0.0+build.42","listed":true}},{"catalogEntry":{"version":"1.0.0","listed":true}}]}"""),
            _ => throw new InvalidOperationException("Unexpected endpoint")
        });
        using var client = new HttpClient(handler);
        var result = await new NuGetCandidateProvider(client).GetCandidateAsync("example", default);
        Assert.AreEqual(CandidateStatus.Available, result.Status);
        Assert.AreEqual("2.0.0", result.Version!.ToNormalizedString());
        Assert.AreEqual("build.42", result.Version.Metadata);
        Assert.AreEqual(4, handler.Calls);
        Assert.IsTrue(content.Disposed);
    }

    [TestMethod]
    [DataRow("service")]
    [DataRow("registration")]
    [DataRow("catalog")]
    public async Task PublicRedirectWorksAtEachMetadataBoundary(string boundary)
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v3/index.json" => boundary == "service" ? Redirect(302, "/service.json") : Index(),
            "/service.json" => Index(),
            "/registration/example/index.json" => boundary == "registration" ? Redirect(302, "/metadata.json")
                : Json("""{"items":[{"catalogEntry":"https://api.nuget.org/catalog.json"}]}"""),
            "/metadata.json" => Json("""{"items":[{"catalogEntry":{"version":"2.0.0","listed":true}}]}"""),
            "/catalog.json" => boundary == "catalog" ? Redirect(302, "/entry.json") : Json("""{"version":"2.0.0","listed":true}"""),
            "/entry.json" => Json("""{"version":"2.0.0","listed":true}"""),
            _ => throw new InvalidOperationException("Unexpected endpoint")
        });
        using var client = new HttpClient(handler);
        var result = await new NuGetCandidateProvider(client).GetCandidateAsync("example", default);
        Assert.AreEqual(CandidateStatus.Available, result.Status);
        Assert.AreEqual("2.0.0", result.Version!.ToNormalizedString());
    }

    [TestMethod]
    [DataRow("https://login.corporate.example/sso?token=secret-marker")]
    [DataRow("https://api.nuget.org.evil.example/sso?token=secret-marker")]
    [DataRow("//login.corporate.example/sso?token=secret-marker")]
    [DataRow("http://api.nuget.org/sso?token=secret-marker")]
    [DataRow("https://user:secret-marker@api.nuget.org/page.json")]
    [DataRow("https://api.nuget.org:444/page.json?token=secret-marker")]
    [DataRow("https://api.nuget.org/page.json#secret-marker")]
    [DataRow("https://[invalid/secret-marker")]
    [DataRow(null)]
    public async Task UnsafeOrUnusableRedirectFailsOnceWithoutLeakingLocation(string? location)
    {
        var content = new TrackedContent();
        var handler = new Handler(_ => Redirect(302, location, content));
        using var client = new HttpClient(handler);
        var result = await new NuGetCandidateProvider(client).GetCandidateAsync("example", default);
        Assert.AreEqual(CandidateStatus.Failed, result.Status);
        Assert.IsNull(result.Version);
        StringAssert.StartsWith(result.Detail!, "NuGet redirect was blocked (");
        StringAssert.Contains(result.Detail!, "302");
        Assert.IsFalse(result.Detail!.Contains("secret-marker", StringComparison.Ordinal));
        Assert.IsFalse(result.Detail.Contains("/sso", StringComparison.Ordinal));
        Assert.IsFalse(result.Detail.Contains("Found", StringComparison.Ordinal));
        Assert.AreEqual(1, handler.Calls);
        Assert.IsTrue(content.Disposed);
    }

    [TestMethod]
    [DataRow(5, true)]
    [DataRow(6, false)]
    public async Task RedirectLimitAllowsExactlyFiveHops(int redirects, bool available)
    {
        var responses = new List<TrackedContent>();
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/registration/example/index.json")
                return Json("""{"items":[{"catalogEntry":{"version":"2.0.0","listed":true}}]}""");
            if (responses.Count == redirects) return Index();
            var content = new TrackedContent();
            responses.Add(content);
            return Redirect(302, $"/hop{responses.Count}.json", content);
        });
        using var client = new HttpClient(handler);
        var result = await new NuGetCandidateProvider(client).GetCandidateAsync("example", default);
        Assert.AreEqual(available ? CandidateStatus.Available : CandidateStatus.Failed, result.Status);
        Assert.AreEqual(available ? 7 : 6, handler.Calls);
        Assert.IsTrue(responses.All(content => content.Disposed));
    }

    [TestMethod]
    public async Task RedirectLoopIsBlockedWithoutRetry()
    {
        var handler = new Handler(request => Redirect(302,
            request.RequestUri!.AbsolutePath == "/v3/index.json" ? "/loop.json" : "/v3/index.json"));
        using var client = new HttpClient(handler);
        var result = await new NuGetCandidateProvider(client).GetCandidateAsync("example", default);
        Assert.AreEqual(CandidateStatus.Failed, result.Status);
        StringAssert.StartsWith(result.Detail!, "NuGet redirect was blocked (");
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task CancellationBetweenRedirectHopsPropagatesAndDisposesResponse()
    {
        using var cancellation = new CancellationTokenSource();
        var content = new TrackedContent();
        var handler = new Handler(_ =>
        {
            cancellation.Cancel();
            return Redirect(302, "/next.json", content);
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new NuGetCandidateProvider(client).GetCandidateAsync("example", cancellation.Token));
        Assert.AreEqual(1, handler.Calls);
        Assert.IsTrue(content.Disposed);
    }

    [TestMethod]
    [DataRow("Authorization")]
    [DataRow("Cookie")]
    public async Task CredentialsAddedBetweenHopsPreventNextRequest(string header)
    {
        HttpClient? client = null;
        var handler = new Handler(_ =>
        {
            client!.DefaultRequestHeaders.TryAddWithoutValidation(header, "secret-marker");
            return Redirect(302, "/next.json");
        });
        using (client = new HttpClient(handler))
        {
            var result = await new NuGetCandidateProvider(client).GetCandidateAsync("example", default);
            Assert.AreEqual(CandidateStatus.Failed, result.Status);
            Assert.AreEqual(1, handler.Calls);
        }
    }

    [TestMethod]
    [DataRow(400, 1)]
    [DataRow(401, 1)]
    [DataRow(403, 1)]
    [DataRow(429, 3)]
    [DataRow(500, 3)]
    public async Task OnlyTransientHttpStatusesAreRetriedWithNumericDetail(int status, int calls)
    {
        var handler = new Handler(_ => new((HttpStatusCode)status));
        using var client = new HttpClient(handler);
        var result = await new NuGetCandidateProvider(client).GetCandidateAsync("example", default);
        Assert.AreEqual(CandidateStatus.Failed, result.Status);
        Assert.AreEqual($"NuGet HTTP failure: {status}.", result.Detail);
        Assert.AreEqual(calls, handler.Calls);
    }

    [TestMethod]
    public async Task NetworkFailuresRemainBoundedAndSanitized()
    {
        var handler = new Handler(_ => throw new HttpRequestException("https://secret-marker.example/?token=secret-marker"));
        using var client = new HttpClient(handler);
        var result = await new NuGetCandidateProvider(client).GetCandidateAsync("example", default);
        Assert.AreEqual(CandidateStatus.Failed, result.Status);
        Assert.IsFalse(result.Detail!.Contains("secret-marker", StringComparison.Ordinal));
        Assert.AreEqual(3, handler.Calls);
    }

    [TestMethod]
    public async Task RedirectTo404RemainsNotFound()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v3/index.json" => Index(),
            "/registration/example/index.json" => Redirect(302, "/missing.json"),
            _ => new(HttpStatusCode.NotFound)
        });
        using var client = new HttpClient(handler);
        Assert.AreEqual(CandidateStatus.NotFound,
            (await new NuGetCandidateProvider(client).GetCandidateAsync("example", default)).Status);
        Assert.AreEqual(3, handler.Calls);
    }

    [TestMethod]
    public async Task NewerPageWithFormalVersionAvoidsFetchingOlderPages()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v3/index.json" => Index(),
            "/registration/example/index.json" => Json("""{"items":[{"@id":"https://api.nuget.org/page1.json","lower":"1.0.0","upper":"1.5.0"},{"@id":"https://api.nuget.org/page2.json","lower":"2.0.0","upper":"2.5.0"}]}"""),
            "/page2.json" => Json("""{"items":[{"catalogEntry":{"version":"2.5.0","listed":true}}]}"""),
            "/page1.json" => throw new InvalidOperationException("Older page must not be fetched when newer page has formal version"),
            _ => throw new InvalidOperationException($"Unexpected endpoint: {request.RequestUri}")
        });
        using var client = new HttpClient(handler);
        var result = await new NuGetCandidateProvider(client).GetCandidateAsync("example", default);
        Assert.AreEqual(CandidateStatus.Available, result.Status);
        Assert.AreEqual("2.5.0", result.Version!.ToNormalizedString());
        Assert.AreEqual(3, handler.Calls);
    }

    [TestMethod]
    public async Task MinimumVersionPrunesPagesBelowThreshold()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v3/index.json" => Index(),
            "/registration/example/index.json" => Json("""{"items":[{"@id":"https://api.nuget.org/page1.json","lower":"1.0.0","upper":"1.5.0"}]}"""),
            "/page1.json" => throw new InvalidOperationException("Page below minimumVersion must not be fetched"),
            _ => throw new InvalidOperationException($"Unexpected endpoint: {request.RequestUri}")
        });
        using var client = new HttpClient(handler);
        var minVersion = NuGet.Versioning.NuGetVersion.Parse("2.0.0");
        var result = await new NuGetCandidateProvider(client).GetCandidateAsync("example", minVersion, default);
        Assert.AreEqual(CandidateStatus.NotFound, result.Status);
        Assert.IsNull(result.Version);
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task PrereleaseOnlyLatestPageFallsBackToPreviousPage()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v3/index.json" => Index(),
            "/registration/example/index.json" => Json("""{"items":[{"@id":"https://api.nuget.org/page1.json","lower":"1.0.0","upper":"1.5.0"},{"@id":"https://api.nuget.org/page2.json","lower":"2.0.0-rc.1","upper":"2.0.0-rc.2"}]}"""),
            "/page2.json" => Json("""{"items":[{"catalogEntry":{"version":"2.0.0-rc.2","listed":true}}]}"""),
            "/page1.json" => Json("""{"items":[{"catalogEntry":{"version":"1.5.0","listed":true}}]}"""),
            _ => throw new InvalidOperationException($"Unexpected endpoint: {request.RequestUri}")
        });
        using var client = new HttpClient(handler);
        var result = await new NuGetCandidateProvider(client).GetCandidateAsync("example", default);
        Assert.AreEqual(CandidateStatus.Available, result.Status);
        Assert.AreEqual("1.5.0", result.Version!.ToNormalizedString());
        Assert.AreEqual(4, handler.Calls);
    }

    [TestMethod]
    public async Task LiveNuGetSmbLibraryCandidate()
    {
        using var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All
        });
        var result = await new NuGetCandidateProvider(client).GetCandidateAsync("SMBLibrary", default);
        Console.WriteLine($"STATUS: {result.Status}, VERSION: {result.Version}, DETAIL: {result.Detail}");
        Assert.AreEqual(CandidateStatus.Available, result.Status, result.Detail);
        Assert.AreEqual("1.5.8.1", result.Version?.ToNormalizedString());
    }

    private static HttpResponseMessage Redirect(int status, string? location, HttpContent? content = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = content };
        if (location is not null) response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    private sealed class TrackedContent : StringContent
    {
        public TrackedContent() : base("") { }
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static HttpResponseMessage Index() => Json("""{"resources":[{"@id":"https://api.nuget.org/registration/","@type":"RegistrationsBaseUrl/3.6.0"}]}""");
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Assert.IsNull(request.Headers.Authorization);
            Assert.IsFalse(request.Headers.Contains("Cookie"));
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.IsFalse(request.Headers.Contains("X-TFS-FedAuthRedirect"));
            return Task.FromResult(response(request));
        }
    }
}