using Elekta.IC.Tools.SoupAnalysis.Domain;

namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure.AzureDevOps;

public sealed record AzureRepoUrl
{
    public string Organization { get; }
    public string Project { get; }
    public string Repository { get; }
    public string NormalizedRepoUrl { get; }
    public Uri ApiBaseUri { get; }

    private AzureRepoUrl(string organization, string project, string repository)
    {
        Organization = organization;
        Project = project;
        Repository = repository;
        var root = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}/";
        ApiBaseUri = new Uri(root + "_apis/git/");
        NormalizedRepoUrl = root + "_git/" + Uri.EscapeDataString(repository);
    }

    public static AzureRepoUrl Parse(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            throw new InvalidDataException("Use an HTTPS Azure Repos URL without embedded credentials or a custom port.");
        var rawPath = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('?', '#')[0];
        rawPath = rawPath.Contains('/') ? rawPath[rawPath.IndexOf('/')..] : "";
        var parts = rawPath.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        if (parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.Contains('/') || part.Contains('\\')
            || part.Contains('%') || part.Any(char.IsControl)))
            throw new InvalidDataException("Azure repository path contains an unsafe or ambiguous encoded segment.");
        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) && parts.Length == 4 && parts[2] == "_git")
            return new(parts[0], parts[1], parts[3]);
        const string suffix = ".visualstudio.com";
        if (uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && parts.Length == 3 && parts[1] == "_git")
        {
            var org = uri.Host[..^suffix.Length];
            if (org.Length > 0 && !org.Contains('.')) return new(org, parts[0], parts[2]);
        }
        throw new InvalidDataException("Expected dev.azure.com/org/project/_git/repo or org.visualstudio.com/project/_git/repo.");
    }

    public static void ValidateRef(RefKind kind, string reference)
    {
        if (!Enum.IsDefined(kind) || string.IsNullOrWhiteSpace(reference)) throw new InvalidDataException("A supported ref is required.");
        if (kind == RefKind.Commit)
        {
            if (reference.Length != 40 || !reference.All(Uri.IsHexDigit)) throw new InvalidDataException("Commit refs require exactly 40 hexadecimal characters.");
            return;
        }
        if (reference.StartsWith("refs/", StringComparison.Ordinal) || reference.StartsWith('/') || reference.EndsWith('/')
            || reference.Contains("..", StringComparison.Ordinal) || reference.Contains("@{", StringComparison.Ordinal)
            || reference.Split('/').Any(segment => segment.Length == 0 || segment.StartsWith('.') || segment.EndsWith('.') || segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            || reference.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || "~^:?*[\\".Contains(character)))
            throw new InvalidDataException("Use a branch/tag name, not a Git revision expression or fully qualified ref.");
    }
}