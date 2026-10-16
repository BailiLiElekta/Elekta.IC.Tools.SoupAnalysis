using System.Collections.Immutable;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using NuGet.Versioning;

namespace Elekta.IC.Tools.SoupAnalysis.Desktop.Verification;

internal static class SmokeFixture
{
    public static AnalysisResult Create(LoadedConfiguration configuration)
    {
        var source = new RepositorySource("smoke-ic", "desktop-smoke", "https://dev.azure.com/design/fixtures/_git/desktop-smoke", RepositoryCategory.IC, RefKind.Branch, "design-only");
        var second = new RepositorySource("smoke-ic-second", "desktop-smoke-second", "https://dev.azure.com/design/fixtures/_git/desktop-smoke-second", RepositoryCategory.IC, RefKind.Branch, "design-only");
        var emulator = new RepositorySource("smoke-emulator", "elekta.ic.emulator.desktop-smoke", "https://dev.azure.com/design/fixtures/_git/elekta.ic.emulator.desktop-smoke", RepositoryCategory.ICEmulator, RefKind.Branch, "design-only");
        var sources = configuration.Manifest.Repositories.Add(source).Add(second).Add(emulator);
        var inventories = sources.Select(repo =>
        {
            ImmutableArray<PackageUsage> usages = repo == configuration.Manifest.Repositories[0]
                ? [Usage(repo, "Newtonsoft.Json", "13.0.3")]
                : repo == source ? [Usage(repo, "Newtonsoft.Json", "13.0.1"), Usage(repo, "Polly", "7.2.4"), Usage(repo, "Dapper", "2.1.66", true), Usage(repo, "elekta.design.fixture", "1.0.0")]
                : repo == second ? [Usage(repo, "Newtonsoft.Json", "13.0.5"), Usage(repo, "Polly", "8.0.0"), Usage(repo, "Dapper", "2.1.66"), Usage(repo, "Serilog", "4.0.0"), Usage(repo, "elekta.design.fixture", "3.0.0", true)]
                : repo == emulator ? [Usage(repo, "Polly", "99.0.0", true), Usage(repo, "emulator.only.fixture", "99.0.0"), Usage(repo, "elekta.design.fixture", "99.0.0")] : [];
            return new RepositoryInventory(repo, new(repo, new string('a', 40), [], DateTimeOffset.UtcNow), usages, [], InventoryStatus.Complete);
        }).ToImmutableArray();
        var analysis = AlignmentAnalyzer.Analyze(Guid.NewGuid(), configuration.Manifest, configuration.ManifestHash, sources, inventories, DateTimeOffset.UtcNow);
        return analysis with { Packages = [.. analysis.Packages.Select(package => package.PackageId == "Polly"
            ? package with { NuGetCandidate = new(package.PackageId, NuGetVersion.Parse("8.6.4"), CandidateStatus.Available, "DESIGN FIXTURE (not queried)", DateTimeOffset.UtcNow) }
            : package.PackageId == "Dapper" ? package with { NuGetCandidate = new(package.PackageId, null, CandidateStatus.Failed, "DESIGN FIXTURE", DateTimeOffset.UtcNow, "Simulated metadata outage") } : package)] };
    }
    private static PackageUsage Usage(RepositorySource source, string package, string version, bool test = false)
    {
        var path = test ? "src/Design.UnitTests/Design.UnitTests.csproj" : "src/DesignFixture/DesignFixture.csproj";
        var owner = new XmlOwner(path, "/Project[1]/ItemGroup[1]/PackageReference[1]/@Version");
        return new(source, path, package, version, test ? TestClassification.Test : TestClassification.Production, new(owner, owner, VersionDeclarationKind.Inline));
    }
}