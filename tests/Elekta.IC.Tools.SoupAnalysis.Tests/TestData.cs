using System.Collections.Immutable;
using Elekta.IC.Tools.SoupAnalysis.Domain;

namespace Elekta.IC.Tools.SoupAnalysis.Tests;

internal static class TestData
{
    public static RepositorySource Repo(string id, RepositoryCategory category = RepositoryCategory.IC) =>
        new(id, id, $"https://dev.azure.com/example/platform/_git/{id}", category, RefKind.Branch, "main");

    public static PackageUsage Usage(RepositorySource repo, string packageId, string version,
        TestClassification test = TestClassification.Production, string project = "src/Main/Main.csproj") =>
        new(repo, project, packageId, version, test,
            new(new(project, "/Project/ItemGroup/PackageReference[1]"),
                new(project, "/Project/ItemGroup/PackageReference[1]/@Version"), VersionDeclarationKind.Inline));

    public static RepositoryInventory Inventory(RepositorySource repo, params PackageUsage[] usages) =>
        new(repo, new(repo, new string('a', 40), [], DateTimeOffset.UtcNow), [.. usages], [], InventoryStatus.Complete);

    public static AnalysisResult Analyze(params RepositoryInventory[] inventories)
    {
        var sources = inventories.Select(inventory => inventory.Source).ToImmutableArray();
        return AlignmentAnalyzer.Analyze(Guid.NewGuid(),
            new(1, "1", [.. sources.Where(source => source.Category == RepositoryCategory.Platform)]),
            "manifest-sha256", sources, [.. inventories], DateTimeOffset.UtcNow);
    }
}