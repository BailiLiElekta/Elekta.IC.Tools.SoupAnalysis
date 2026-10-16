using System.Security.Cryptography;
using System.Text.Json;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Configuration;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Authentication;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Elekta.IC.Tools.SoupAnalysis.Tests;

[TestClass]
public sealed class AuthConfigurationTests
{
    [TestMethod]
    public async Task SignOutWithoutSignInAndCancelledSignInNeverInitializeCache()
    {
        using var fixture = new AdapterSettingsFixture();
        var login = new MicrosoftAccountLogin(new(1, "", "", "", ["https://app.vssps.visualstudio.com/.default"]), fixture.Directory);
        await login.SignOutAsync(default);
        Assert.IsNull(login.AccountLabel);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => login.GetTokenAsync(true, cancelled.Token));
        Assert.HasCount(0, Directory.GetFiles(fixture.Directory));
    }

    [TestMethod]
    public async Task InsecureRedirectAndTenantPathAreRejectedBeforeAnyBrowserOrCache()
    {
        using var fixture = new AdapterSettingsFixture();
        foreach (var auth in new[]
        {
            new Elekta.IC.Tools.SoupAnalysis.Domain.AuthenticationConfiguration(1, "..", Guid.NewGuid().ToString(), "http://localhost", ["scope"]),
            new Elekta.IC.Tools.SoupAnalysis.Domain.AuthenticationConfiguration(1, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "http://external.test", ["scope"])
        })
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new MicrosoftAccountLogin(auth, fixture.Directory).GetTokenAsync(false, default));
        Assert.HasCount(0, Directory.GetFiles(fixture.Directory));
    }

    [TestMethod]
    public async Task ManifestHashIsExactBytesAndBlankAuthenticationAllowsStartup()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "config");
        var loaded = await new ConfigurationService(directory).LoadAsync(default);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "platform-repositories.json"));
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)), loaded.ManifestHash);
        Assert.IsFalse(loaded.Authentication.IsConfigured);
        var login = new MicrosoftAccountLogin(loaded.Authentication);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => login.GetTokenAsync(false, default));
    }

    [TestMethod]
    public async Task UnsupportedSchemaAndIntegerEnumsAreRejected()
    {
        using var fixture = new AdapterSettingsFixture();
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "config")))
            File.Copy(file, Path.Combine(fixture.Directory, Path.GetFileName(file)));
        var path = Path.Combine(fixture.Directory, "authentication.json");
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":2,\"scopes\":[\"scope\"]}");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new ConfigurationService(fixture.Directory).LoadAsync(default));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "config", "authentication.json"), path, true);
        var manifest = Path.Combine(fixture.Directory, "platform-repositories.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(manifest))!;
        node["repositories"]![0]!["category"] = 0;
        await File.WriteAllTextAsync(manifest, node.ToJsonString());
        await Assert.ThrowsExactlyAsync<JsonException>(() => new ConfigurationService(fixture.Directory).LoadAsync(default));
    }
}