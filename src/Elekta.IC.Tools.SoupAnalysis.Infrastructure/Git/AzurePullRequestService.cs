using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Domain;

namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure.Git;

public sealed class AzurePullRequestService(HttpClient client)
{
    public async Task<string> CreateOrFindAsync(RepositorySource source, string branch, string description,
        AccessToken token, CancellationToken cancellationToken)
        => (await CreateOrFindDetailedAsync(source, branch, description, token, cancellationToken).ConfigureAwait(false)).Url;

    internal async Task<PullRequestEvidence> CreateOrFindDetailedAsync(RepositorySource source, string branch, string description,
        AccessToken token, CancellationToken cancellationToken, bool allowCreate = true, Func<Task>? creationStarted = null)
    {
        if (client.DefaultRequestHeaders.Any(header => header.Key.Contains("auth", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Use an Azure client without default authorization headers.");
        var address = AzureGitAddress.Parse(source.Url);
        var endpoint = address.Api + "/pullrequests";
        var repositoryUrl = source.Url.TrimEnd('/');
        var sourceRef = "refs/heads/" + branch;
        var targetRef = "refs/heads/" + source.Ref;
        var existing = await FindAsync(endpoint, repositoryUrl, sourceRef, targetRef, token, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return existing;
        if (!allowCreate) throw new InvalidOperationException("Previously confirmed or possibly created PR was not found; outcome remains unknown. Refusing to create another PR.");
        cancellationToken.ThrowIfCancellationRequested();
        using var request = Request(HttpMethod.Post, endpoint + "?api-version=7.1", token);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            sourceRefName = sourceRef, targetRefName = targetRef, title = "SOUP alignment: approved package versions",
            description, isDraft = false
        }), Encoding.UTF8, "application/json");
        if (creationStarted is not null) await creationStarted().ConfigureAwait(false);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return Evidence(body.RootElement, repositoryUrl, sourceRef, targetRef);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException
            or OperationCanceledException or InvalidOperationException)
        {
            var rejected = exception is HttpRequestException
            {
                StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity or HttpStatusCode.TooManyRequests
            };
            if (cancellationToken.IsCancellationRequested)
            {
                using var reconciliation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    var confirmed = await FindAsync(endpoint, repositoryUrl, sourceRef, targetRef, token, reconciliation.Token).ConfigureAwait(false);
                    if (confirmed is not null) return confirmed;
                }
                catch (Exception reconciliationFailure) when (reconciliationFailure is HttpRequestException or JsonException
                    or InvalidDataException or OperationCanceledException or InvalidOperationException)
                {
                    throw new OperationCanceledException("PR creation failed (" + exception.Message
                        + "); reconciliation also failed (" + reconciliationFailure.Message + "). Pushed branch retained.",
                        new AggregateException(exception, reconciliationFailure), cancellationToken);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            PullRequestEvidence? recovered;
            try
            {
                recovered = await FindAsync(endpoint, repositoryUrl, sourceRef, targetRef, token, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception reconciliationFailure) when (reconciliationFailure is HttpRequestException or JsonException
                or InvalidDataException or OperationCanceledException or InvalidOperationException)
            {
                var detail = "PR creation failed (" + exception.Message + "); reconciliation also failed ("
                    + reconciliationFailure.Message + "). Pushed branch retained. No duplicate POST was attempted.";
                var failures = new AggregateException(exception, reconciliationFailure);
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(detail, failures, cancellationToken);
                throw new PullRequestCreationException(detail, failures, rejected);
            }
            return recovered ?? throw new PullRequestCreationException("PR creation was not confirmed (" + exception.Message
                + "); pushed branch retained. No duplicate POST was attempted.", exception, rejected);
        }
    }

    private async Task<PullRequestEvidence?> FindAsync(string endpoint, string repositoryUrl, string source, string target,
        AccessToken token, CancellationToken cancellationToken)
    {
        var query = "?searchCriteria.status=all&searchCriteria.sourceRefName=" + Uri.EscapeDataString(source)
            + "&searchCriteria.targetRefName=" + Uri.EscapeDataString(target) + "&$top=100&api-version=7.1";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                PullRequestEvidence? match = null;
                for (var skip = 0; ; skip += 100)
                {
                    using var request = Request(HttpMethod.Get, endpoint + query + "&$skip=" + skip, token);
                    using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var body = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                    var page = body.RootElement.GetProperty("value");
                    foreach (var item in page.EnumerateArray().Where(item =>
                        item.GetProperty("sourceRefName").GetString() == source && item.GetProperty("targetRefName").GetString() == target))
                    {
                        if (match is not null) throw new InvalidDataException("Multiple PRs match this branch; refusing to create another PR.");
                        match = Evidence(item, repositoryUrl, source, target);
                    }
                    if (page.GetArrayLength() < 100) return match;
                }
            }
            catch (HttpRequestException) when (attempt < 2)
            { await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken).ConfigureAwait(false); }
        }
        throw new InvalidOperationException("PR lookup retry limit exceeded.");
    }

    internal sealed record PullRequestEvidence(string Url, string Status);
    internal sealed class PullRequestCreationException(string message, Exception innerException, bool wasRejected)
        : InvalidOperationException(message, innerException)
    {
        public bool WasRejected { get; } = wasRejected;
    }

    private static PullRequestEvidence Evidence(JsonElement value, string repositoryUrl, string source, string target)
    {
        var url = PrUrl(value, repositoryUrl, source, target);
        var status = value.TryGetProperty("status", out var property) ? property.GetString() : null;
        if (status is not ("active" or "completed" or "abandoned"))
            throw new InvalidDataException("PR response contains an unknown status.");
        return new(url, status);
    }

    private static HttpRequestMessage Request(HttpMethod method, string uri, AccessToken token)
    {
        if (token.ExpiresAt <= DateTimeOffset.UtcNow || string.IsNullOrWhiteSpace(token.Token))
            throw new InvalidOperationException("Azure access token is unavailable or expired.");
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return request;
    }

    private static string PrUrl(JsonElement value, string repositoryUrl, string source, string target)
    {
        if (value.GetProperty("pullRequestId").GetInt32() <= 0 || value.GetProperty("sourceRefName").GetString() != source
            || value.GetProperty("targetRefName").GetString() != target)
            throw new InvalidDataException("PR response does not match the approved source and target.");
        var url = value.TryGetProperty("_links", out var links) && links.TryGetProperty("web", out var web)
            && web.TryGetProperty("href", out var href)
            ? href.GetString()
            : repositoryUrl + "/pullrequest/" + value.GetProperty("pullRequestId").GetInt32();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Host != "dev.azure.com" && !uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PR response contains an invalid web URL.");
        return url!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        string? errorType = null;
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            try
            {
                using var body = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (body.RootElement.ValueKind == JsonValueKind.Object
                    && body.RootElement.TryGetProperty("typeKey", out var type) && type.ValueKind == JsonValueKind.String)
                {
                    var candidate = type.GetString();
                    if (candidate is { Length: > 0 and <= 128 }
                        && candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_'))
                        errorType = candidate;
                }
            }
            catch (JsonException)
            {
                throw new HttpRequestException($"Azure PR request failed: HTTP {(int)response.StatusCode}; error response was not valid JSON.",
                    null, response.StatusCode);
            }
        }
        throw new HttpRequestException($"Azure PR request failed: HTTP {(int)response.StatusCode}"
            + (errorType is null ? "." : " (" + errorType + ")."), null, response.StatusCode);
    }
}

internal sealed record AzureGitAddress(string Api)
{
    public static AzureGitAddress Parse(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("Only credential-free Azure HTTPS repository URLs are supported.");
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        string organization, project, repository;
        if (uri.Host == "dev.azure.com" && parts.Length == 4 && parts[2] == "_git")
            (organization, project, repository) = (parts[0], parts[1], parts[3]);
        else if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase) && parts.Length == 3 && parts[1] == "_git")
            (organization, project, repository) = (uri.Host[..^".visualstudio.com".Length], parts[0], parts[2]);
        else throw new InvalidOperationException("Unsupported Azure Git URL.");
        if (new[] { organization, project, repository }.Any(part => string.IsNullOrWhiteSpace(part)
            || part is "." or ".." || part.Contains('/') || part.Contains('\\') || part.Any(char.IsControl)))
            throw new InvalidOperationException("Invalid Azure repository identity.");
        return new("https://dev.azure.com/" + Uri.EscapeDataString(organization) + "/" + Uri.EscapeDataString(project)
            + "/_apis/git/repositories/" + Uri.EscapeDataString(repository));
    }
}