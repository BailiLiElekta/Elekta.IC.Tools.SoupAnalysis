using System.Text.Json;
using System.Text.Json.Serialization;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Elekta.IC.Tools.SoupAnalysis.Tests;

[TestClass]
public sealed class ConfigurationTests
{
    [TestMethod]
    public void LegacyUnscopedOverridesCannotReplaceReleaseManifestRefs()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform) with { Ref = "release/berlin" };
        var client = TestData.Repo("client");
        var configuration = new LoadedConfiguration(new(1, "2", [platform]), "hash", AnalysisRules.Default,
            new(1, "", "", "", ["scope"]));
        var settings = UserSettings.Default with
        {
            Repositories = [client],
            PlatformRefOverrides = [new(platform.Id, RefKind.Branch, "release/old")]
        };

        var request = ConfigurationPolicy.CreateScanRequest(configuration, settings);

        Assert.AreEqual("release/berlin", request.Sources.Single(source => source.Id == platform.Id).Ref);
        Assert.AreEqual(client, request.Sources.Single(source => source.Id == client.Id));
    }

    [TestMethod]
    [DataRow("2", "IC Berlin Release", "custom")]
    [DataRow("3", "IC Berlin Release", "release/new")]
    [DataRow("2", "IC Next Release", "release/new")]
    public void OverridesApplyOnlyToTheirConfigurationVersionAndRelease(string version, string analysisFor, string expectedRef)
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform) with { Ref = "release/new" };
        var configuration = new LoadedConfiguration(new(1, version, [platform], analysisFor), "hash", AnalysisRules.Default,
            new(1, "", "", "", ["scope"]));
        var settings = UserSettings.Default with
        {
            PlatformConfiguration = new("2", "IC Berlin Release"),
            PlatformRefOverrides = [new(platform.Id, RefKind.Branch, "custom")]
        };

        var request = ConfigurationPolicy.CreateScanRequest(configuration, settings);

        Assert.AreEqual(expectedRef, request.Sources.Single().Ref);
    }

    [TestMethod]
    public void NullCallerSourceIsRejectedByPolicy()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var configuration = new LoadedConfiguration(new(1, "1", [platform]), "hash", AnalysisRules.Default,
            new(1, "", "", "", ["scope"]));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationPolicy.CreateScanRequest(configuration,
            UserSettings.Default with { Repositories = [null!] }));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("main~1")]
    [DataRow("topic~1")]
    [DataRow("topic^1")]
    [DataRow("topic@{1}")]
    [DataRow("topic..branch")]
    [DataRow("topic.lock")]
    [DataRow("topic/")]
    [DataRow("@")]
    public void InvalidCallerBranchIsRejectedByPolicy(string? branch)
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var configuration = new LoadedConfiguration(new(1, "1", [platform]), "hash", AnalysisRules.Default,
            new(1, "", "", "", ["scope"]));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationPolicy.CreateScanRequest(configuration,
            UserSettings.Default with { Repositories = [TestData.Repo("client") with { Ref = branch! }] }));
    }

    [TestMethod]
    public void UninitializedOrNullConfigurationEntriesAreRejectedByPolicy()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var configuration = new LoadedConfiguration(new(1, "1", [platform]), "hash", AnalysisRules.Default,
            new(1, "", "", "", ["scope"]));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationPolicy.CreateScanRequest(configuration,
            UserSettings.Default with { Repositories = default }));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationPolicy.CreateScanRequest(configuration,
            UserSettings.Default with { PlatformRefOverrides = default }));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationPolicy.CreateScanRequest(configuration,
            UserSettings.Default with { PlatformRefOverrides = [null!] }));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationPolicy.CreateScanRequest(configuration with
            { Manifest = configuration.Manifest with { Repositories = default } }, UserSettings.Default));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationPolicy.CreateScanRequest(configuration with
            { Manifest = configuration.Manifest with { Repositories = [null!] } }, UserSettings.Default));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationPolicy.CreateScanRequest(configuration with
            { Authentication = configuration.Authentication with { Scopes = default } }, UserSettings.Default));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationPolicy.CreateScanRequest(configuration,
            UserSettings.Default with { PlatformRefOverrides = [new("platform", RefKind.Branch, null!)] }));
    }

    [TestMethod]
    public void LegacyAndModernCallerUrlsAreOneIdentity()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var client = TestData.Repo("client");
        var configuration = new LoadedConfiguration(new(1, "1", [platform]), "hash", AnalysisRules.Default,
            new(1, "", "", "", ["scope"]));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationPolicy.CreateScanRequest(configuration,
            UserSettings.Default with { Repositories = [client, client with { Id = "alias", Url = "https://example.visualstudio.com/platform/_git/client" }] }));
    }

    [TestMethod]
    [DataRow("elekta.platform.iam")]
    [DataRow("elekta.ic.emulator.ois")]
    public void CallerSourcesCannotHideRestrictedUrlBehindCustomName(string actualName)
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var configuration = new LoadedConfiguration(new(1, "1", [platform]), "hash", AnalysisRules.Default,
            new(1, "", "", "", ["https://app.vssps.visualstudio.com/.default"]));
        var malicious = TestData.Repo("customName") with { Url = "https://dev.azure.com/org/project/_git/" + actualName };
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationPolicy.CreateScanRequest(configuration,
            UserSettings.Default with { Repositories = [malicious] }));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    [TestMethod]
    public void DistributedConfigurationContainsExactApprovedSourcesAndNoSecrets()
    {
        var manifest = Read<PlatformManifest>("platform-repositories.json");
        Assert.AreEqual("3", manifest.Version);
        Assert.AreEqual("IC Berlin Release", manifest.AnalysisFor);
        var rules = Read<AnalysisRules>("analysis-rules.json");
        var auth = Read<AuthenticationConfiguration>("authentication.json");
        var request = ConfigurationPolicy.CreateScanRequest(new(manifest, "hash", rules, auth), UserSettings.Default);
        Assert.HasCount(8, request.Sources);
        Assert.IsFalse(auth.IsConfigured);
        Assert.AreEqual("", auth.TenantId);
        Assert.AreEqual("", auth.ClientId);
        Assert.AreEqual("", auth.RedirectUri);
        CollectionAssert.AreEqual(new[] { "https://app.vssps.visualstudio.com/.default" }, auth.Scopes.ToArray());
        var expected = new[]
        {
            ("elekta.platform.iam", RefKind.Tag, "33.7.0"),
            ("elekta.platform.minio", RefKind.Tag, "1.15.0"),
            ("elekta.platform.dicom.common", RefKind.Tag, "v3.0.0"),
            ("elekta.platform.dicom.repository", RefKind.Tag, "20.4.0"),
            ("elekta.platform.system-information", RefKind.Tag, "3.4.0"),
            ("elekta.platform.utility.polly", RefKind.Branch, "main"),
            ("elekta.platform.identity", RefKind.Tag, "29.5.0"),
            ("elekta.platform.utility.rest", RefKind.Branch, "main")
        };
        foreach (var (name, refKind, reference) in expected)
        {
            var source = request.Sources.Single(item => item.Name == name);
            Assert.AreEqual(refKind, source.RefKind);
            Assert.AreEqual(reference, source.Ref);
            Assert.AreEqual(RepositoryCategory.Platform, source.Category);
            Assert.AreEqual($"https://elektasoftwarefactory.visualstudio.com/platform/_git/{name}", source.Url);
        }
        CollectionAssert.AreEqual(new[] { "Test.*", "*.Test.Unit", "*.UnitTests" }, rules.TestProjectPatterns.ToArray());
        Assert.HasCount(expected.Length, manifest.PackageMappings);
        Assert.AreEqual("elekta.platform.identity", manifest.FindPackageRepositoryId("Elekta.Platform.Identity.Authentication"));
        Assert.AreEqual("elekta.platform.identity", manifest.FindPackageRepositoryId("Elekta.Platform.Identity.HealthChecks"));
        foreach (var mapping in manifest.PackageMappings)
            foreach (var prefix in mapping.PackagePrefixes)
                Assert.AreEqual(mapping.RepositoryId, manifest.FindPackageRepositoryId(prefix + ".Client"));
    }

    [TestMethod]
    public void BridgeIsUserSelectedICAndStalePlatformOverrideCannotRestoreIt()
    {
        const string name = "workflow-management-task-management-bridge";
        var manifest = Read<PlatformManifest>("platform-repositories.json");
        var configuration = new LoadedConfiguration(manifest, "hash", Read<AnalysisRules>("analysis-rules.json"),
            Read<AuthenticationConfiguration>("authentication.json"));
        var settings = UserSettings.Default with { PlatformRefOverrides = [new(name, RefKind.Tag, "old-tag")] };
        var request = ConfigurationPolicy.CreateScanRequest(configuration, settings);
        Assert.IsFalse(request.Sources.Any(source => source.Name == name));
        var bridge = Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels.MainViewModel.ParseRepository(
            $"https://elektasoftwarefactory.visualstudio.com/platform/_git/{name}", "main", null);
        Assert.AreEqual(RepositoryCategory.IC, bridge.Category);
        request = ConfigurationPolicy.CreateScanRequest(configuration, settings with { Repositories = [bridge] });
        Assert.AreEqual(RepositoryCategory.IC, request.Sources.Single(source => source.Name == name).Category);
        Assert.IsFalse(request.Manifest.Repositories.Any(source => source.Name == name));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationPolicy.CreateScanRequest(configuration,
            settings with { Repositories = [bridge with { RefKind = RefKind.Tag, Ref = "v1" }] }));
    }

    [TestMethod]
    public void ManifestIsAuthoritativeAndCurrentMemberOverridesSurvive()
    {
        var platform = TestData.Repo("current", RepositoryCategory.Platform);
        var client = TestData.Repo("client");
        var configuration = new LoadedConfiguration(new(1, "2", [platform]), "hash", AnalysisRules.Default,
            new(1, "", "", "", ["https://app.vssps.visualstudio.com/.default"]));
        var settings = UserSettings.Default with
        {
            Repositories = [client],
            PlatformConfiguration = new(configuration.Manifest.Version, configuration.Manifest.AnalysisFor),
            PlatformRefOverrides = [new("current", RefKind.Tag, "v2"), new("removed", RefKind.Branch, "old")]
        };
        var request = ConfigurationPolicy.CreateScanRequest(configuration, settings);
        Assert.HasCount(2, request.Sources);
        Assert.AreEqual("v2", request.Sources.Single(source => source.Id == "current").Ref);
        Assert.IsFalse(request.Sources.Any(source => source.Id == "removed"));
        Assert.AreEqual("main", configuration.Manifest.Repositories[0].Ref);
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationPolicy.CreateScanRequest(configuration,
            settings with { Repositories = [platform] }));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationPolicy.CreateScanRequest(configuration,
            settings with { Repositories = [platform with { Category = RepositoryCategory.IC }] }));
    }

    [TestMethod]
    public void MalformedOrDuplicateConfigurationBlocksRatherThanShrinksScope()
    {
        var manifest = Read<PlatformManifest>("platform-repositories.json");
        var configuration = new LoadedConfiguration(manifest, "hash", AnalysisRules.Default, Read<AuthenticationConfiguration>("authentication.json"));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationPolicy.CreateScanRequest(configuration with
            { Manifest = manifest with { Repositories = [manifest.Repositories[0], manifest.Repositories[0]] } }, UserSettings.Default));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationPolicy.CreateScanRequest(configuration with
            { Manifest = manifest with { SchemaVersion = 2 } }, UserSettings.Default));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationPolicy.CreateScanRequest(configuration with
            { ManifestHash = "" }, UserSettings.Default));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationPolicy.CreateScanRequest(configuration with
            { Rules = AnalysisRules.Default with { MaxConcurrency = 0 } }, UserSettings.Default));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationPolicy.CreateScanRequest(configuration,
            UserSettings.Default with { Repositories = [TestData.Repo("client") with { RefKind = RefKind.Tag }] }));
    }

    private static T Read<T>(string name) => JsonSerializer.Deserialize<T>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", name)), JsonOptions)!;
}