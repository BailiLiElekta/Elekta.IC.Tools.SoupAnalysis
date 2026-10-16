using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Authentication;

namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure.AzureDevOps;

public sealed class AzureReposClient : IRepositorySnapshotProvider
{
    private readonly HttpClient http;
    private readonly IAccessTokenProvider tokens;
    private readonly SemaphoreSlim requests;
    private readonly string? cacheDirectory;
    private readonly TimeSpan retryDelay;

    public AzureReposClient(HttpClient httpClient, IAccessTokenProvider tokenProvider, int maxConcurrency = 4,
        string? cacheDirectory = null, TimeSpan? retryDelay = null)
    {
        if (maxConcurrency is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        http = httpClient;
        tokens = tokenProvider;
        requests = new(maxConcurrency, maxConcurrency);
        this.cacheDirectory = cacheDirectory;
        this.retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(250);
    }

    public async Task<SourceSnapshot> GetSnapshotAsync(RepositorySource source, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var endpoint = AzureRepoUrl.Parse(source.Url);
        AzureRepoUrl.ValidateRef(source.RefKind, source.Ref);
        if (source.Category != RepositoryCategory.Platform && source.RefKind != RefKind.Branch)
            throw new InvalidDataException("IC and emulator sources must use branches.");
        var clock = Stopwatch.StartNew();
        progress?.Report(new(OperationStage.ResolvingRefs, source.Id, Elapsed: clock.Elapsed));
        using var metadata = await JsonAsync(new Uri(endpoint.ApiBaseUri, "repositories/" + Uri.EscapeDataString(endpoint.Repository) + "?api-version=7.1"), cancellationToken);
        if (!Guid.TryParse(metadata.RootElement.GetProperty("id").GetString(), out var repositoryId))
            throw new InvalidDataException("Azure repository metadata did not contain a repository GUID.");
        var root = new Uri(endpoint.ApiBaseUri, $"repositories/{repositoryId:D}/");
        var commit = await ResolveAsync(root, source, cancellationToken);
        var version = "&versionDescriptor.versionType=commit&versionDescriptor.version=" + Uri.EscapeDataString(commit);
        using var tree = await JsonAsync(new Uri(root, "items?scopePath=%2F&recursionLevel=Full&includeContentMetadata=true" + version + "&api-version=7.1"), cancellationToken);
        var paths = tree.RootElement.GetProperty("value").EnumerateArray()
            .Where(item => !item.TryGetProperty("isFolder", out var folder) || !folder.GetBoolean())
            .Select(item => SafePath(item.GetProperty("path").GetString()!))
            .Where(path => !IsExcluded(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new ConcurrentDictionary<string, SnapshotFile>(StringComparer.OrdinalIgnoreCase);
        var pending = paths.Where(path => path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".proj", StringComparison.OrdinalIgnoreCase)
            || IsSourceProject(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var completed = 0;
        while (pending.Count != 0)
        {
            var batch = pending.Where(path => !files.ContainsKey(path)).ToArray();
            if (batch.Length == 0) break;
            await Task.WhenAll(batch.Select(async path =>
            {
                var bytes = await ReadFileAsync(root, repositoryId, commit, path, version, cancellationToken);
                files[path] = new(path, [.. bytes], Convert.ToHexString(SHA256.HashData(bytes)));
                progress?.Report(new(OperationStage.ReadingSources, source.Id, Interlocked.Increment(ref completed), Elapsed: clock.Elapsed, Detail: path));
            }));
            pending.Clear();
            var importProperties = CollectImportProperties(files.Values);
            foreach (var file in files.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var import in FindImports(file, importProperties))
                {
                    var matches = paths.Where(path => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(import, path, true));
                    foreach (var match in matches.Where(path => !files.ContainsKey(path))) pending.Add(match);
                }
            }
        }
        return new(source, commit, [.. files.Values.OrderBy(file => file.Path, StringComparer.Ordinal)], DateTimeOffset.UtcNow);
    }

    private async Task<string> ResolveAsync(Uri root, RepositorySource source, CancellationToken token)
    {
        if (source.RefKind == RefKind.Commit) return source.Ref.ToLowerInvariant();
        var expected = (source.RefKind == RefKind.Tag ? "refs/tags/" : "refs/heads/") + source.Ref;
        using var refs = await JsonAsync(new Uri(root, "refs?filter=" + Uri.EscapeDataString(expected[5..]) + "&peelTags=true&api-version=7.1"), token);
        var found = refs.RootElement.GetProperty("value").EnumerateArray().Where(item => item.GetProperty("name").GetString() == expected).ToArray();
        if (found.Length != 1) throw new InvalidDataException("The exact configured branch/tag was not found uniquely.");
        var reference = found[0];
        var objectId = Commit(reference.GetProperty("objectId").GetString());
        if (source.RefKind == RefKind.Tag)
        {
            if (reference.TryGetProperty("peeledObjectId", out var peeled) && peeled.GetString() is { } peeledId && peeledId != new string('0', 40))
                return Commit(peeledId);
            for (var depth = 0; depth < 16; depth++)
            {
                using var response = await SendAsync(new Uri(root, "annotatedtags/" + objectId + "?api-version=7.1"), token);
                if (response.StatusCode == HttpStatusCode.NotFound) return objectId;
                EnsureSuccess(response);
                using var tag = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
                var target = tag.RootElement.GetProperty("taggedObject");
                objectId = Commit(target.GetProperty("objectId").GetString());
                var type = target.GetProperty("objectType").GetString();
                if (string.Equals(type, "commit", StringComparison.OrdinalIgnoreCase)) return objectId;
                if (!string.Equals(type, "tag", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The tag does not point to a commit.");
            }
            throw new InvalidDataException("Annotated tag peeling exceeded the safe depth.");
        }
        return objectId;
    }

    private async Task<byte[]> ReadFileAsync(Uri root, Guid repositoryId, string commit, string path, string version, CancellationToken token)
    {
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(repositoryId.ToString("D") + "/" + commit + "/" + path)));
        var cached = cacheDirectory is null ? null : Path.Combine(cacheDirectory, key + ".json");
        if (cached is not null && File.Exists(cached))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<CachedFile>(await File.ReadAllBytesAsync(cached, token));
                if (entry is not null && entry.Content is not null && entry.RepositoryId == repositoryId && entry.Commit == commit && entry.Path == path
                    && Convert.ToHexString(SHA256.HashData(entry.Content)) == entry.Sha256) return entry.Content;
            }
            catch (Exception error) when (error is IOException or JsonException or ArgumentException) { }
        }
        using var response = await SendAsync(new Uri(root, "items?path=" + Uri.EscapeDataString("/" + path) + "&download=true" + version + "&api-version=7.1"), token);
        EnsureSuccess(response);
        var bytes = await response.Content.ReadAsByteArrayAsync(token);
        if (cached is not null)
        {
            var temporary = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(cacheDirectory!);
                await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(new CachedFile(repositoryId, commit, path, bytes, Convert.ToHexString(SHA256.HashData(bytes)))), token);
                File.Move(temporary, cached, true);
            }
            catch (IOException) { }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return bytes;
    }

    private async Task<JsonDocument> JsonAsync(Uri url, CancellationToken token)
    {
        using var response = await SendAsync(url, token);
        EnsureSuccess(response);
        if (response.Headers.Contains("x-ms-continuationtoken")) throw new InvalidDataException("Azure returned a truncated listing; incomplete snapshots are not accepted.");
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, CancellationToken token)
    {
        var renewed = false;
        AccessToken? fresh = null;
        var attempt = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await requests.WaitAsync(token);
            HttpResponseMessage? response = null;
            try
            {
                var access = fresh ?? await tokens.GetTokenAsync(false, token);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
                response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);
            }
            catch (HttpRequestException) when (attempt < 3) { }
            catch (HttpRequestException) { throw new HttpRequestException("Azure repository read failed after bounded network retries. Check connectivity and sign-in."); }
            finally { requests.Release(); }
            if (response is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(retryDelay.TotalMilliseconds * Math.Pow(2, attempt++), 0, 30000)), token);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized && !renewed)
            {
                renewed = true;
                response.Dispose();
                fresh = tokens is IRefreshableAccessTokenProvider refreshable ? await refreshable.RefreshTokenAsync(token) : await tokens.GetTokenAsync(false, token);
                continue;
            }
            if (attempt < 3 && (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500))
            {
                var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                    ?? TimeSpan.FromMilliseconds(retryDelay.TotalMilliseconds * Math.Pow(2, attempt));
                response.Dispose();
                attempt++;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 0, 30000)), token);
                continue;
            }
            return response;
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Azure repository read failed (HTTP {(int)response.StatusCode}). Check permissions, sign-in and the configured ref.", null, response.StatusCode);
    }

    private static string Commit(string? value)
    {
        if (value is null || value.Length != 40 || !value.All(Uri.IsHexDigit) || value == new string('0', 40))
            throw new InvalidDataException("Azure did not return a full commit/object SHA.");
        return value.ToLowerInvariant();
    }

    internal static bool IsExcluded(string path) => path.Split('/').Any(segment =>
        segment.Equals("bin", StringComparison.OrdinalIgnoreCase) || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase) || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
        || segment.Contains("worktree", StringComparison.OrdinalIgnoreCase));
    internal static bool IsSourceProject(string path) => path.StartsWith("src/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && !IsExcluded(path);
    internal static string SafePath(string path)
    {
        var relative = path.TrimStart('/');
        if (relative.Length == 0 || relative.Contains('\\') || relative.Contains(':') || relative.Split('/').Any(segment => segment is "" or "." or ".." || segment.Any(char.IsControl)))
            throw new InvalidDataException("Unsafe repository-relative file path.");
        return relative;
    }

    private static Dictionary<string, string[]> CollectImportProperties(IEnumerable<SnapshotFile> files)
    {
        var properties = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var document = ReadXml(file);
            if (document is null) continue;
            var directory = file.Path.Contains('/') ? file.Path[..(file.Path.LastIndexOf('/') + 1)] : "";
            foreach (var element in document.Descendants().Where(element => element.Parent?.Name.LocalName == "PropertyGroup" && !element.HasElements))
            {
                if (!properties.TryGetValue(element.Name.LocalName, out var values)) properties[element.Name.LocalName] = values = new(StringComparer.Ordinal);
                values.Add(element.Value.Replace("$(MSBuildThisFileDirectory)", "__soup_repository_root__/" + directory, StringComparison.OrdinalIgnoreCase));
            }
        }
        return properties.ToDictionary(property => property.Key, property => property.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static XDocument? ReadXml(SnapshotFile file)
    {
        try
        {
            using var stream = new MemoryStream(file.Content.ToArray());
            using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
            return XDocument.Load(reader);
        }
        catch (XmlException) { return null; }
    }

    private static IEnumerable<string> FindImports(SnapshotFile file, IReadOnlyDictionary<string, string[]> inheritedProperties)
    {
        var document = ReadXml(file);
        if (document is null) return [];
        var directory = file.Path.Contains('/') ? file.Path[..(file.Path.LastIndexOf('/') + 1)] : "";
        var properties = new Dictionary<string, string[]>(inheritedProperties, StringComparer.OrdinalIgnoreCase);
        foreach (var group in document.Descendants().Where(element => element.Parent?.Name.LocalName == "PropertyGroup" && !element.HasElements).GroupBy(element => element.Name.LocalName))
            properties[group.Key] = group.Select(element => element.Value).Distinct().ToArray();
        properties["MSBuildThisFileDirectory"] = ["__soup_repository_root__/" + directory];
        var imports = new List<string>();
        foreach (var element in document.Descendants().Where(element => element.Name.LocalName == "Import"))
        {
            var alternatives = new List<string> { (string?)element.Attribute("Project") ?? "" };
            for (var pass = 0; pass < 16; pass++)
            {
                var previous = alternatives.ToArray();
                foreach (var property in properties)
                {
                    var expression = "$(" + property.Key + ")";
                    alternatives = alternatives.SelectMany(value => value.Contains(expression, StringComparison.OrdinalIgnoreCase)
                        ? property.Value.Select(replacement => value.Replace(expression, replacement, StringComparison.OrdinalIgnoreCase)) : [value]).Distinct().Take(256).ToList();
                }
                if (alternatives.SequenceEqual(previous)) break;
            }
            foreach (var value in alternatives)
            {
                if (value.Contains("$(", StringComparison.Ordinal) || value.Contains("@(", StringComparison.Ordinal) || value.Contains(':')) continue;
                foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var normalized = part.Replace('\\', '/');
                    if (normalized.StartsWith('/')) continue;
                    const string repositoryRoot = "__soup_repository_root__/";
                    var segments = new List<string>();
                    var combined = normalized.StartsWith(repositoryRoot, StringComparison.Ordinal) ? normalized[repositoryRoot.Length..] : directory + normalized;
                    foreach (var segment in combined.Split('/'))
                    {
                        if (segment is "" or ".") continue;
                        if (segment == "..") { if (segments.Count == 0) { segments.Clear(); break; } segments.RemoveAt(segments.Count - 1); }
                        else segments.Add(segment);
                    }
                    if (segments.Count != 0) imports.Add(string.Join('/', segments));
                }
            }
        }
        return imports;
    }

    private sealed record CachedFile(Guid RepositoryId, string Commit, string Path, byte[] Content, string Sha256);
}