using Elekta.IC.Tools.SoupAnalysis.Domain;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Elekta.IC.Tools.SoupAnalysis.Tests;

[TestClass]
public sealed class SettingsPersistenceTests
{
    [TestMethod]
    [DataRow("elekta.platform.iam")]
    [DataRow("elekta.ic.emulator.ois")]
    public async Task MismatchedPersistedNameCannotHideRestrictedUrl(string actualName)
    {
        using var fixture = new AdapterSettingsFixture();
        var store = new SettingsStore(Path.Combine(fixture.Directory, "settings.json"));
        var malicious = TestData.Repo("customName") with { Url = "https://dev.azure.com/org/project/_git/" + actualName };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.SaveAsync(UserSettings.Default with { Repositories = [malicious] }, default));
    }

    [TestMethod]
    public async Task FirstSaveHasRecoveryAndBrowserQueryCannotPersistCredentials()
    {
        using var fixture = new AdapterSettingsFixture();
        var path = Path.Combine(fixture.Directory, "settings.json");
        var store = new SettingsStore(path);
        await store.SaveAsync(UserSettings.Default with { Repositories = [TestData.Repo("client") with
            { Url = "https://org.visualstudio.com/project/_git/client?access_token=not-a-real-secret" }] }, default);
        Assert.IsFalse((await File.ReadAllTextAsync(path)).Contains("not-a-real-secret", StringComparison.Ordinal));
        Assert.AreEqual("https://dev.azure.com/org/project/_git/client", (await store.LoadAsync(default)).Repositories[0].Url);
        await File.WriteAllTextAsync(path, "broken");
        Assert.HasCount(1, (await store.LoadAsync(default)).Repositories);
        Assert.IsNotNull(store.RecoveryNotice);
    }

    [TestMethod]
    public async Task MalformedAndSecretBearingSettingsNeverBecomeDefaultsSilently()
    {
        using var fixture = new AdapterSettingsFixture();
        var path = Path.Combine(fixture.Directory, "settings.json");
        var store = new SettingsStore(path);
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"accessToken\":\"fake\"}");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.LoadAsync(default));
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"SchemaVersion\":1}");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.LoadAsync(default));
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1}");
        var defaults = await store.LoadAsync(default);
        Assert.IsFalse(defaults.Repositories.IsDefault);
        Assert.IsFalse(defaults.PlatformRefOverrides.IsDefault);
        Assert.IsFalse(defaults.CandidatePreferences.IsDefault);
    }

    [TestMethod]
    public async Task PersistedOverridesMergeOnlyWithCurrentManifestMembers()
    {
        using var fixture = new AdapterSettingsFixture();
        var store = new SettingsStore(Path.Combine(fixture.Directory, "settings.json"));
        var configuration = await new ConfigurationService(Path.Combine(AppContext.BaseDirectory, "config")).LoadAsync(default);
        var current = configuration.Manifest.Repositories[0];
        await store.SaveAsync(UserSettings.Default with
        {
            PlatformConfiguration = new(configuration.Manifest.Version, configuration.Manifest.AnalysisFor),
            PlatformRefOverrides = [new(current.Id, RefKind.Branch, "custom"), new("removed", RefKind.Tag, "old")]
        }, default);
        var settings = await store.LoadAsync(default);
        Assert.AreEqual(new PlatformConfigurationIdentity(configuration.Manifest.Version, configuration.Manifest.AnalysisFor), settings.PlatformConfiguration);
        var request = Elekta.IC.Tools.SoupAnalysis.Application.ConfigurationPolicy.CreateScanRequest(configuration, settings);
        Assert.HasCount(configuration.Manifest.Repositories.Length, request.Sources);
        Assert.AreEqual("custom", request.Sources.Single(source => source.Id == current.Id).Ref);
        Assert.IsFalse(request.Sources.Any(source => source.Id == "removed"));
    }

    [TestMethod]
    public async Task RoundTripAndCorruptionRecoverLastGoodWithNotice()
    {
        using var fixture = new AdapterSettingsFixture();
        var path = Path.Combine(fixture.Directory, "settings.json");
        var store = new SettingsStore(path);
        Assert.IsFalse((await store.LoadAsync(default)).Repositories.IsDefault);
        var settings = UserSettings.Default with { OutputDirectory = "first", Repositories = [TestData.Repo("client")] };
        await store.SaveAsync(settings, default);
        await store.SaveAsync(settings with { OutputDirectory = "second" }, default);
        Assert.AreEqual("second", (await store.LoadAsync(default)).OutputDirectory);
        await File.WriteAllTextAsync(path, "broken");
        Assert.AreEqual("first", (await store.LoadAsync(default)).OutputDirectory);
        Assert.IsNotNull(store.RecoveryNotice);
        Assert.IsFalse((await File.ReadAllTextAsync(path + ".bak")).Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task PlatformMembershipAndDuplicateUserIdentitiesCannotBePersisted()
    {
        using var fixture = new AdapterSettingsFixture();
        var store = new SettingsStore(Path.Combine(fixture.Directory, "settings.json"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.SaveAsync(UserSettings.Default with
            { Repositories = [TestData.Repo("platform", RepositoryCategory.Platform)] }, default));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.SaveAsync(UserSettings.Default with
            { Repositories = [TestData.Repo("client"), TestData.Repo("client")] }, default));
    }
}