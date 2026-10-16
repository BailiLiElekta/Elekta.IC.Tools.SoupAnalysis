using System.Security.Cryptography;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Domain;

namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure.Configuration;

public sealed class ConfigurationService(string configDirectory) : IConfigurationService
{
    public async Task<LoadedConfiguration> LoadAsync(CancellationToken cancellationToken)
    {
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(configDirectory, "platform-repositories.json"), cancellationToken);
        var manifest = ConfigurationJson.Read<PlatformManifest>(manifestBytes);
        var rules = ConfigurationJson.Read<AnalysisRules>(await File.ReadAllBytesAsync(Path.Combine(configDirectory, "analysis-rules.json"), cancellationToken));
        var auth = ConfigurationJson.Read<AuthenticationConfiguration>(await File.ReadAllBytesAsync(Path.Combine(configDirectory, "authentication.json"), cancellationToken));
        manifest = manifest with { Repositories = manifest.Repositories.IsDefault ? [] : manifest.Repositories };
        rules = rules with { TestProjectPatterns = rules.TestProjectPatterns.IsDefault ? [] : rules.TestProjectPatterns };
        auth = auth with { TenantId = auth.TenantId ?? "", ClientId = auth.ClientId ?? "", RedirectUri = auth.RedirectUri ?? "", Scopes = auth.Scopes.IsDefault ? [] : auth.Scopes };
        if (auth.SchemaVersion != 1 || auth.Scopes.IsDefaultOrEmpty || auth.Scopes.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Authentication schemaVersion 1 and nonempty delegated scopes are required.");
        ConfigurationJson.ValidateSources(manifest.Repositories);
        manifest = manifest with { Repositories = [.. manifest.Repositories.Select(source => source with { Url = AzureDevOps.AzureRepoUrl.Parse(source.Url).NormalizedRepoUrl })] };
        var loaded = new LoadedConfiguration(manifest, Convert.ToHexString(SHA256.HashData(manifestBytes)), rules, auth);
        try { _ = ConfigurationPolicy.CreateScanRequest(loaded, UserSettings.Default); }
        catch (ArgumentException) { throw new InvalidDataException("Invalid Platform manifest or analysis rules; the scope has not been reduced."); }
        return loaded;
    }
}