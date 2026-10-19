using System.Collections.Immutable;

namespace Elekta.IC.Tools.SoupAnalysis.Domain;

public sealed record PlatformManifest(int SchemaVersion, string Version, ImmutableArray<RepositorySource> Repositories,
    string? AnalysisFor = null)
{
    public ImmutableArray<PlatformPackageMapping> PackageMappings { get; init; } = [];

    public string? FindPackageRepositoryId(string packageId) => PackageMappings
        .SelectMany(mapping => mapping.PackagePrefixes.Select(prefix => (mapping.RepositoryId, Prefix: prefix)))
        .Where(mapping => packageId.Equals(mapping.Prefix, StringComparison.OrdinalIgnoreCase)
            || packageId.StartsWith(mapping.Prefix + ".", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(mapping => mapping.Prefix.Length)
        .Select(mapping => mapping.RepositoryId).FirstOrDefault();
}

public sealed record PlatformPackageMapping(string RepositoryId, ImmutableArray<string> PackagePrefixes);

public sealed record AnalysisRules(int SchemaVersion, ImmutableArray<string> TestProjectPatterns, int MaxConcurrency = 4)
{
    public static AnalysisRules Default { get; } = new(1, ["Test.*", "*.Test.Unit", "*.UnitTests"]);
}

public sealed record AuthenticationConfiguration(
    int SchemaVersion, string TenantId, string ClientId, string RedirectUri, ImmutableArray<string> Scopes)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(ClientId) && Uri.TryCreate(RedirectUri, UriKind.Absolute, out _);
}

public enum TargetSource { PlatformMax, ICMax, NuGetCandidate, Custom, PlatformTag }

public sealed record PlatformRefOverride(string RepositoryId, RefKind RefKind, string Ref);
public sealed record PlatformConfigurationIdentity(string Version, string? AnalysisFor);
public sealed record CandidatePreference(string PackageId, TargetSource Source, string? CustomVersion = null);
public sealed record WindowLayout(double Width = 1200, double Height = 800, bool Maximized = false);

public sealed record UserSettings(
    int SchemaVersion,
    ImmutableArray<RepositorySource> Repositories,
    ImmutableArray<PlatformRefOverride> PlatformRefOverrides,
    string? OutputDirectory,
    WindowLayout Window,
    ImmutableArray<CandidatePreference> CandidatePreferences,
    PlatformConfigurationIdentity? PlatformConfiguration = null)
{
    public static UserSettings Default { get; } = new(1, [], [], null, new(), []);
}

public sealed record LoadedConfiguration(
    PlatformManifest Manifest, string ManifestHash, AnalysisRules Rules, AuthenticationConfiguration Authentication);