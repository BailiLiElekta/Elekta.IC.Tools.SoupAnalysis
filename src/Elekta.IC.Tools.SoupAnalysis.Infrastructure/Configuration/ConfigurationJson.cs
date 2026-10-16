using System.Text.Json;
using System.Text.Json.Serialization;
using Elekta.IC.Tools.SoupAnalysis.Domain;

namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure.Configuration;

internal static class ConfigurationJson
{
    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    internal static T Read<T>(byte[] bytes) where T : class
    {
        using var document = JsonDocument.Parse(bytes);
        ValidateKeys(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidDataException("JSON configuration must be an object.");
    }

    private static void ValidateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new JsonException("Duplicate configuration property names are not supported.");
                ValidateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) ValidateKeys(child);
    }

    internal static UserSettings Validate(UserSettings settings)
    {
        settings = settings with
        {
            Repositories = settings.Repositories.IsDefault ? [] : settings.Repositories,
            PlatformRefOverrides = settings.PlatformRefOverrides.IsDefault ? [] : settings.PlatformRefOverrides,
            CandidatePreferences = settings.CandidatePreferences.IsDefault ? [] : settings.CandidatePreferences,
            Window = settings.Window ?? new()
        };
        if (settings.SchemaVersion != 1 || settings.Repositories.Any(source => source is null || source.Category == RepositoryCategory.Platform)
            || settings.PlatformConfiguration is { } identity && string.IsNullOrWhiteSpace(identity.Version)
            || settings.PlatformRefOverrides.Any(item => item is null || string.IsNullOrWhiteSpace(item.RepositoryId)
                || !Enum.IsDefined(item.RefKind) || string.IsNullOrWhiteSpace(item.Ref))
            || settings.PlatformRefOverrides.GroupBy(item => item.RepositoryId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
            || settings.CandidatePreferences.Any(item => item is null || string.IsNullOrWhiteSpace(item.PackageId) || !Enum.IsDefined(item.Source)
                || (item.Source == TargetSource.Custom
                    ? string.IsNullOrWhiteSpace(item.CustomVersion) || PackageVersions.ParseExact(item.CustomVersion) is null
                    : item.CustomVersion is not null))
            || settings.CandidatePreferences.GroupBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
            || !double.IsFinite(settings.Window.Width) || !double.IsFinite(settings.Window.Height)
            || settings.Window.Width <= 0 || settings.Window.Height <= 0)
            throw new InvalidDataException("Unsupported or malformed user settings. Platform membership cannot be stored in user settings.");
        ValidateSources(settings.Repositories);
        return settings with { Repositories = [.. settings.Repositories.Select(source =>
        {
            var parsed = AzureDevOps.AzureRepoUrl.Parse(source.Url);
            return source with { Id = parsed.NormalizedRepoUrl.ToLowerInvariant(), Name = parsed.Repository, Url = parsed.NormalizedRepoUrl };
        })] };
    }

    internal static void ValidateSources(IEnumerable<RepositorySource> sources)
    {
        var list = sources.ToArray();
        foreach (var source in list)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Id) || string.IsNullOrWhiteSpace(source.Name)
                || !Enum.IsDefined(source.Category) || !Enum.IsDefined(source.RefKind) || string.IsNullOrWhiteSpace(source.Ref)
                || source.Category != RepositoryCategory.Platform && source.RefKind != RefKind.Branch)
                throw new InvalidDataException("Invalid repository identity, category or ref.");
            var parsed = AzureDevOps.AzureRepoUrl.Parse(source.Url);
            try { Application.ConfigurationPolicy.ValidateRepositoryIdentity(source with { Url = parsed.NormalizedRepoUrl }); }
            catch (ArgumentException exception) { throw new InvalidDataException("Repository name/category differs from URL identity.", exception); }
            AzureDevOps.AzureRepoUrl.ValidateRef(source.RefKind, source.Ref);
            if (source.Category != RepositoryCategory.Platform && RepositoryClassifier.Classify(source.Name) is { } known && known != source.Category)
                throw new InvalidDataException("Known repositories cannot be reclassified.");
        }
        if (list.GroupBy(source => source.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
            || list.GroupBy(source => source.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
            || list.GroupBy(source => AzureDevOps.AzureRepoUrl.Parse(source.Url).NormalizedRepoUrl, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidDataException("Repository identities, names and normalized URLs must be unique.");
    }
}