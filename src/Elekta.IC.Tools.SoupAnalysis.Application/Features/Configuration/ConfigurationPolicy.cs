using Elekta.IC.Tools.SoupAnalysis.Domain;

namespace Elekta.IC.Tools.SoupAnalysis.Application;

public static class ConfigurationPolicy
{
    public static ScanRequest CreateScanRequest(LoadedConfiguration configuration, UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SchemaVersion != 1 || settings.Repositories.IsDefault || settings.PlatformRefOverrides.IsDefault
            || configuration.Authentication is null || configuration.Authentication.SchemaVersion != 1
            || configuration.Authentication.Scopes.IsDefaultOrEmpty
            || configuration.Manifest is null || configuration.Manifest.Repositories.IsDefaultOrEmpty
            || configuration.Manifest.Repositories.Any(member => member is null)
            || settings.Repositories.Any(source => source is null || source.Category == RepositoryCategory.Platform))
            throw new ArgumentException("Unsupported or malformed configuration/settings; Platform membership is manifest-only.");
        if (settings.PlatformConfiguration is { } identity && string.IsNullOrWhiteSpace(identity.Version))
            throw new ArgumentException("Platform configuration identity requires a version.");
        if (settings.PlatformRefOverrides.Any(item => item is null || string.IsNullOrWhiteSpace(item.RepositoryId)
                || !Enum.IsDefined(item.RefKind) || string.IsNullOrWhiteSpace(item.Ref))
            || settings.PlatformRefOverrides.GroupBy(item => item.RepositoryId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new ArgumentException("Platform ref overrides must have valid refs and unique repository identities.");
        settings = BindPlatformOverrides(settings, configuration.Manifest);
        var platform = configuration.Manifest.Repositories.Select(member =>
        {
            var reference = settings.PlatformRefOverrides.SingleOrDefault(item =>
                item.RepositoryId.Equals(member.Id, StringComparison.OrdinalIgnoreCase));
            return reference is null ? member : member with { RefKind = reference.RefKind, Ref = reference.Ref };
        });
        var request = new ScanRequest(configuration.Manifest, configuration.ManifestHash,
            [.. platform, .. settings.Repositories], configuration.Rules);
        ValidateScanRequest(request);
        return request;
    }

    public static UserSettings BindPlatformOverrides(UserSettings settings, PlatformManifest manifest)
    {
        var identity = new PlatformConfigurationIdentity(manifest.Version, manifest.AnalysisFor);
        return settings.PlatformConfiguration == identity ? settings : settings with
        {
            PlatformConfiguration = identity,
            PlatformRefOverrides = []
        };
    }

    public static void ValidateScanRequest(ScanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Manifest is null || request.Manifest.SchemaVersion != 1 || request.Manifest.Repositories.IsDefaultOrEmpty
            || string.IsNullOrWhiteSpace(request.Manifest.Version) || string.IsNullOrWhiteSpace(request.ManifestHash)
            || request.Sources.IsDefaultOrEmpty || request.Rules is null || request.Rules.SchemaVersion != 1
            || request.Rules.MaxConcurrency is < 1 or > 32 || request.Rules.TestProjectPatterns.IsDefaultOrEmpty
            || request.Rules.TestProjectPatterns.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A valid manifest, rules, hash and source selection are required.", nameof(request));
        foreach (var source in request.Sources.Concat(request.Manifest.Repositories))
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Id) || string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.Ref)
                || !Enum.IsDefined(source.Category) || !Enum.IsDefined(source.RefKind)
                || !Uri.TryCreate(source.Url, UriKind.Absolute, out var url) || url.Scheme != "https" || !string.IsNullOrEmpty(url.UserInfo)
                || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment)
                || (source.Category != RepositoryCategory.Platform && source.RefKind != RefKind.Branch)
                || (source.RefKind is RefKind.Branch or RefKind.Tag && !IsValidNamedRef(source.Ref))
                || (source.RefKind == RefKind.Commit && (source.Ref.Length != 40 || !source.Ref.All(Uri.IsHexDigit))))
                throw new ArgumentException("Invalid repository source or ref.", nameof(request));
            var identity = ValidateRepositoryIdentity(source);
            if (source.Category != RepositoryCategory.Platform && RepositoryClassifier.Classify(identity.Name) is { } known
                && known != source.Category)
                throw new ArgumentException("Known repository names cannot be reclassified to bypass category restrictions.", nameof(request));
        }
        if (request.Sources.Select(source => source.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Sources.Length
            || request.Sources.Select(source => source.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Sources.Length
            || request.Sources.Select(source => ValidateRepositoryIdentity(source).NormalizedUrl).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Sources.Length
            || request.Manifest.Repositories.Select(source => source.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Manifest.Repositories.Length)
            throw new ArgumentException("Repository identities, names and URLs must be unique.", nameof(request));
        var platform = request.Sources.Where(source => source.Category == RepositoryCategory.Platform).ToArray();
        if (platform.Length != request.Manifest.Repositories.Length || request.Manifest.Repositories.Any(member =>
            member.Category != RepositoryCategory.Platform || !platform.Any(source =>
                source.Id.Equals(member.Id, StringComparison.OrdinalIgnoreCase) && source.Name == member.Name && source.Url == member.Url)))
            throw new ArgumentException("All and only manifest Platform members must be scanned; only refs may be overridden.", nameof(request));
    }

    public static (string Name, string NormalizedUrl) ValidateRepositoryIdentity(RepositorySource source)
    {
        if (source is null || !Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("A credential-free Azure repository URL is required.");
        var rawPath = source.Url[(source.Url.IndexOf("://", StringComparison.Ordinal) + 3)..];
        rawPath = rawPath.Contains('/') ? rawPath[rawPath.IndexOf('/')..] : "";
        var parts = rawPath.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        if (parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."
            || part.Contains('/') || part.Contains('\\') || part.Contains('%') || part.Any(char.IsControl)))
            throw new ArgumentException("Unsafe repository URL segments.");
        string organization, project, name;
        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) && parts.Length == 4 && parts[2] == "_git")
            (organization, project, name) = (parts[0], parts[1], parts[3]);
        else if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase) && parts.Length == 3 && parts[1] == "_git"
            && uri.Host[..^".visualstudio.com".Length] is { Length: > 0 } legacy && !legacy.Contains('.'))
            (organization, project, name) = (legacy, parts[0], parts[2]);
        else throw new ArgumentException("Unsupported Azure repository URL.");
        if (!string.Equals(source.Name, name, StringComparison.OrdinalIgnoreCase)
            || RepositoryClassifier.Classify(name) is { } known && known != source.Category)
            throw new ArgumentException("Repository name/category must match its actual URL identity.");
        return (name, $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(name)}");
    }

    private static bool IsValidNamedRef(string value) => !string.IsNullOrWhiteSpace(value)
        && value != "@" && !value.StartsWith('-') && !value.Contains("..", StringComparison.Ordinal)
        && !value.Contains("@{", StringComparison.Ordinal) && !value.Contains("//", StringComparison.Ordinal)
        && !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || "~^:?*[\\".Contains(character))
        && !value.Split('/').Any(part => part.Length == 0 || part.StartsWith('.') || part.EndsWith('.')
            || part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));
}