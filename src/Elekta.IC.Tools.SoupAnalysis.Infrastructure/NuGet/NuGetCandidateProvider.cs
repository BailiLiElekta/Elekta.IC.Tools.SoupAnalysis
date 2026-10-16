using System.Net;
using System.Text.Json;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using NuGet.Versioning;

namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure.NuGet;

public sealed class NuGetCandidateProvider : INuGetCandidateProvider
{
    private const string Source = "https://api.nuget.org/v3/index.json";
    private readonly HttpClient client;

    public NuGetCandidateProvider(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
        EnsureUnauthenticatedClient();
    }

    private void EnsureUnauthenticatedClient()
    {
        if (client.DefaultRequestHeaders.Any(header =>
            header.Key.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || header.Key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || header.Key.Contains("cookie", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Use a dedicated, unauthenticated public NuGet client.", nameof(client));
    }

    public Task<NuGetCandidate> GetCandidateAsync(string packageId, CancellationToken cancellationToken)
        => GetCandidateAsync(packageId, minimumVersion: null, cancellationToken);

    public async Task<NuGetCandidate> GetCandidateAsync(string packageId, NuGetVersion? minimumVersion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var queriedAt = DateTimeOffset.UtcNow;
        try
        {
            if (string.IsNullOrWhiteSpace(packageId) || packageId.Length > 100
                || packageId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
                throw new ArgumentException("Invalid NuGet package ID.", nameof(packageId));
            using var index = await GetJsonAsync(new(Source), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("NuGet service index was not found.");
            var registration = index.RootElement.GetProperty("resources").EnumerateArray()
                .First(resource => resource.GetProperty("@type").GetString() == "RegistrationsBaseUrl/3.6.0")
                .GetProperty("@id").GetString()!;
            var uri = PublicUri(registration.TrimEnd('/') + "/" + packageId.ToLowerInvariant() + "/index.json");
            using var metadata = await GetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
            if (metadata is null)
                return new(packageId, null, CandidateStatus.NotFound, Source, queriedAt, "Registration not found.");

            var visited = new HashSet<string>(StringComparer.Ordinal);
            var maximum = await FindHighestFormalVersionAsync(metadata.RootElement, minimumVersion, visited, cancellationToken).ConfigureAwait(false);
            return new(packageId, maximum, maximum is null ? CandidateStatus.NotFound : CandidateStatus.Available,
                Source, queriedAt, maximum is null ? "No formal version available." : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException
            or InvalidOperationException or KeyNotFoundException or ArgumentException or OperationCanceledException)
        {
            return new(packageId, null, CandidateStatus.Failed, Source, queriedAt,
                exception is NuGetRedirectException redirect ? redirect.Message
                    : exception is HttpRequestException http ? http.StatusCode is { } status
                        ? $"NuGet HTTP failure: {(int)status}." : "NuGet HTTP failure: network."
                    : $"NuGet metadata query failed ({exception.GetType().Name}).");
        }
    }

    private async Task<NuGetVersion?> FindHighestFormalVersionAsync(
        JsonElement root, NuGetVersion? minimumVersion, HashSet<string> visited, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("items", out var rootItemsElement))
            return null;

        var items = rootItemsElement.EnumerateArray().ToArray();
        if (items.Length == 0) return null;

        // If items are package leaves directly (unpaged registration or mock test)
        if (items.Any(i => i.TryGetProperty("catalogEntry", out _)))
        {
            var leaves = await ReadLeavesAsync(rootItemsElement, cancellationToken).ConfigureAwait(false);
            return leaves.Where(v => !v.IsPrerelease && (minimumVersion == null || v > minimumVersion))
                .OrderByDescending(v => v, VersionComparer.VersionRelease).FirstOrDefault();
        }

        // Paged registration: order pages descending by upper bound (or reverse array order if upper is absent)
        var pageComparer = Comparer<NuGetVersion?>.Create((a, b) =>
            a is null && b is null ? 0 : a is null ? -1 : b is null ? 1 : VersionComparer.VersionRelease.Compare(a, b));
        var sortedPages = items
            .Select((element, index) =>
            {
                NuGetVersion? upper = null;
                if (element.TryGetProperty("upper", out var upperProp) && NuGetVersion.TryParse(upperProp.GetString(), out var v))
                    upper = v;
                return (Element: element, Upper: upper, OriginalIndex: index);
            })
            .OrderByDescending(p => p.Upper, pageComparer)
            .ThenByDescending(p => p.OriginalIndex)
            .ToArray();

        foreach (var page in sortedPages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // If a minimum version is specified and this page's upper bound is <= minimumVersion,
            // neither this page nor any subsequent page (with lower upper bound) can exceed minimumVersion.
            if (minimumVersion is not null && page.Upper is not null && page.Upper <= minimumVersion)
                break;

            List<NuGetVersion> pageVersions;
            if (page.Element.TryGetProperty("items", out var inlineItems))
            {
                pageVersions = await ReadLeavesAsync(inlineItems, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var pageUri = PublicUri(page.Element.GetProperty("@id").GetString()!);
                if (visited.Count >= 10000 || !visited.Add(pageUri.AbsoluteUri))
                    throw new InvalidDataException("Repeated or excessive registration pages.");
                using var pageDoc = await GetJsonAsync(pageUri, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Registration page not found.");
                if (pageDoc.RootElement.TryGetProperty("items", out var pageItems))
                    pageVersions = await ReadLeavesAsync(pageItems, cancellationToken).ConfigureAwait(false);
                else
                    pageVersions = [];
            }

            var formalVersions = pageVersions
                .Where(v => !v.IsPrerelease && (minimumVersion == null || v > minimumVersion))
                .OrderByDescending(v => v, VersionComparer.VersionRelease)
                .ToArray();

            if (formalVersions.Length > 0)
            {
                // Newest page containing formal versions provides the overall maximum.
                return formalVersions[0];
            }
        }

        return null;
    }

    private async Task<List<NuGetVersion>> ReadLeavesAsync(JsonElement itemsContainer, CancellationToken cancellationToken)
    {
        var versions = new List<NuGetVersion>();
        foreach (var item in itemsContainer.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.TryGetProperty("catalogEntry", out var entry))
            {
                JsonDocument? catalog = null;
                try
                {
                    if (entry.ValueKind == JsonValueKind.String)
                    {
                        catalog = await GetJsonAsync(PublicUri(entry.GetString()!), cancellationToken).ConfigureAwait(false)
                            ?? throw new InvalidDataException("Missing catalog entry.");
                        entry = catalog.RootElement;
                    }
                    var hasListed = entry.TryGetProperty("listed", out var listed);
                    if (hasListed && listed.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                        throw new InvalidDataException("Invalid listed status.");
                    if (!hasListed || listed.GetBoolean())
                    {
                        if (!NuGetVersion.TryParse(entry.GetProperty("version").GetString(), out var version))
                            throw new InvalidDataException("Invalid listed version.");
                        versions.Add(version);
                    }
                }
                finally { catalog?.Dispose(); }
            }
            else if (item.TryGetProperty("items", out var nestedItems))
            {
                versions.AddRange(await ReadLeavesAsync(nestedItems, cancellationToken).ConfigureAwait(false));
            }
        }
        return versions;
    }

    private async Task<JsonDocument?> GetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        PublicUri(uri.AbsoluteUri);
        var visited = new HashSet<string>(StringComparer.Ordinal) { uri.AbsoluteUri };
        var redirects = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureUnauthenticatedClient();
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                    if (response.RequestMessage?.RequestUri is { } finalUri) PublicUri(finalUri.AbsoluteUri);
                    if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                    {
                        var locations = response.Headers.TryGetValues("Location", out var values) ? values.ToArray() : [];
                        if (locations.Length != 1 || !Uri.TryCreate(uri, locations[0], out var nextUri)
                            || !IsPublicUri(nextUri) || redirects >= 5 || !visited.Add(nextUri.AbsoluteUri))
                            throw new NuGetRedirectException(uri.Host, response.StatusCode);
                        uri = nextUri;
                        redirects++;
                        continue;
                    }
                    if (response.StatusCode == HttpStatusCode.NotFound) return null;
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
            catch (HttpRequestException exception) when (attempt < 2 && exception is not NuGetRedirectException
                && (exception.StatusCode is null or HttpStatusCode.TooManyRequests
                    || (int)exception.StatusCode >= 500 && (int)exception.StatusCode <= 599))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException("NuGet retry limit exceeded.");
    }

    private static Uri PublicUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsPublicUri(uri))
            throw new InvalidDataException("Registration link is not a public nuget.org endpoint.");
        return uri;
    }

    private static bool IsPublicUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https"
        && (uri.Host == "api.nuget.org" || uri.Host == "nuget.azure.cn" || uri.Host.EndsWith(".nuget.org", StringComparison.OrdinalIgnoreCase))
        && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment);

    private sealed class NuGetRedirectException(string host, HttpStatusCode status)
        : HttpRequestException($"NuGet redirect was blocked (host {host}; HTTP {(int)status}).", null, status);
}