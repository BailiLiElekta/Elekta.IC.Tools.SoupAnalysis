using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClosedXML.Excel;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Reporting;

namespace Elekta.IC.Tools.SoupAnalysis.Tests;

[TestClass]
public sealed class PlatformPackageAlignmentTests
{
    private const string Prefix = "Elekta.Platform.Identity";
    private static readonly RepositorySource Identity = TestData.Repo("elekta.platform.identity", RepositoryCategory.Platform)
        with { RefKind = RefKind.Tag, Ref = "29.5.0" };
    private static readonly RepositorySource IC = TestData.Repo("ic");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static PlatformManifest Manifest(RepositorySource[] repositories, params (string Repo, string Prefix)[] mappings) =>
        JsonSerializer.Deserialize<PlatformManifest>(JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Version = "test", Repositories = repositories,
            PackageMappings = mappings.Select(mapping => new { RepositoryId = mapping.Repo, PackagePrefixes = new[] { mapping.Prefix } })
        }, JsonOptions), JsonOptions)!;

    private static AnalysisResult Analyze(RepositorySource producer, params RepositoryInventory[] consumers)
    {
        var inventories = new[] { TestData.Inventory(producer) }.Concat(consumers).ToImmutableArray();
        return AlignmentAnalyzer.Analyze(Guid.NewGuid(), Manifest([Identity], (Identity.Id, Prefix)), "hash",
            [.. inventories.Select(inventory => inventory.Source)], inventories, DateTimeOffset.UtcNow);
    }

    [TestMethod]
    [DataRow("Elekta.Platform.Identity.Authentication", "28.0", "Upgrade")]
    [DataRow("elekta.platform.identity.healthchecks", "30.0", "Downgrade")]
    [DataRow("Elekta.Platform.Identity", "29.5.0+build", "Aligned")]
    public void ProducerTagLocksAllMappedPackages(string packageId, string current, string action)
    {
        var result = Analyze(Identity, TestData.Inventory(IC, TestData.Usage(IC, packageId, current)));
        var package = result.Packages.Single();
        var row = new PackageRow(package);
        Assert.AreEqual(PackageOwnership.SharedWithPlatform, package.Ownership);
        Assert.AreEqual("Platform package", row.SourceLabel);
        Assert.AreEqual("29.5.0", row.Target);
        Assert.AreEqual(action, row.Action);
        Assert.AreEqual(action, package.Repositories.Single().Action.ToString());
        Assert.IsFalse(row.IsCustomTargetEditable);
        Assert.IsFalse(row.IsSoupTargetEditable);
        StringAssert.Contains(row.TargetTooltip, Identity.Id);
        Assert.AreEqual("PlatformTag", row.SelectedTarget!.Source.ToString());
        var review = new ReviewRow(package.Repositories.Single(), row, IC);
        Assert.IsTrue(review.Eligible);
        var plan = new ChangePlanner().Create(result, [new(IC.Id, packageId)],
            [new(packageId, row.SelectedTarget.Source)]);
        Assert.IsTrue(plan.Changes.All(change => change.Target.ToNormalizedString() == "29.5.0"));
        Assert.Throws<ArgumentException>(() => new ChangePlanner().Create(result, [new(IC.Id, packageId)],
            [new(packageId, TargetSource.Custom, "99.0")]));
        Assert.Throws<ArgumentException>(() => new ChangePlanner().Create(result, [new(IC.Id, packageId)],
            [new(packageId, TargetSource.PlatformMax)]));
    }

    [TestMethod]
    [DataRow(RefKind.Branch, "main")]
    [DataRow(RefKind.Commit, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [DataRow(RefKind.Tag, "release-name")]
    public void NonVersionRefsRemainExplicitlyUnresolved(RefKind kind, string reference)
    {
        var producer = Identity with { RefKind = kind, Ref = reference };
        var result = Analyze(producer, TestData.Inventory(IC, TestData.Usage(IC, Prefix + ".Authentication", "28.0")));
        var package = result.Packages.Single();
        var row = new PackageRow(package);
        Assert.AreEqual(PackageOwnership.SharedWithPlatform, package.Ownership);
        Assert.IsNull(row.SelectedTarget);
        Assert.AreEqual("Unresolved", row.Action);
        Assert.AreEqual(AlignmentAction.Unresolved, package.Repositories.Single().Action);
        StringAssert.Contains(row.TargetUnavailableReason, Identity.Id);
        StringAssert.Contains(row.TargetUnavailableReason, reference);
        Assert.IsFalse(new ReviewRow(package.Repositories.Single(), row, IC).Eligible);
    }

    [TestMethod]
    public void EffectiveTagOverrideAndVPrefixTakePrecedenceOverConsumedMaxima()
    {
        var producer = Identity with { Ref = "v3.0.0" };
        var other = TestData.Repo("other", RepositoryCategory.Platform);
        var inventories = new[]
        {
            TestData.Inventory(producer),
            TestData.Inventory(other, TestData.Usage(other, Prefix + ".Authentication", "99.0")),
            TestData.Inventory(IC, TestData.Usage(IC, Prefix + ".Authentication", "4.0"))
        };
        var result = AlignmentAnalyzer.Analyze(Guid.NewGuid(), Manifest([Identity, other], (Identity.Id, Prefix)),
            "hash", [producer, other, IC], [.. inventories], DateTimeOffset.UtcNow);
        var row = new PackageRow(result.Packages.Single());
        Assert.AreEqual("3.0.0", row.Target);
        Assert.AreEqual("Downgrade", row.Action);
        Assert.AreEqual("99.0.0", row.PlatformMax);
    }

    [TestMethod]
    public void EmulatorOnlyProducedPackageIsAnalyzedButCannotBeUpdated()
    {
        var emulator = TestData.Repo("emulator", RepositoryCategory.ICEmulator);
        var usage = TestData.Usage(emulator, Prefix + ".Authentication", "100.0");
        var result = Analyze(Identity, TestData.Inventory(emulator, usage));
        Assert.HasCount(1, result.Packages);
        var package = result.Packages.Single();
        Assert.IsNull(package.ICMax);
        Assert.AreEqual(AlignmentAction.Downgrade, package.Repositories.Single().Action);
        var row = new PackageRow(package);
        row.SetRepositoryScope(emulator, [usage], true);
        Assert.AreEqual("Downgrade", row.Action);
        Assert.AreEqual("Read-only", row.TargetAvailability);
        Assert.IsFalse(new ReviewRow(package.Repositories.Single(), row, emulator).Eligible);
        Assert.Throws<ArgumentException>(() => new ChangePlanner().Create(result, [new(emulator.Id, package.PackageId)],
            [new(package.PackageId, row.SelectedTarget!.Source)]));
    }

    [TestMethod]
    public void PrefixMatchingUsesDotBoundariesAndLongestMatch()
    {
        var specialized = TestData.Repo("specialized", RepositoryCategory.Platform) with { RefKind = RefKind.Tag, Ref = "2.0" };
        var manifest = Manifest([Identity, specialized], (Identity.Id, Prefix), (specialized.Id, Prefix + ".Special"));
        var result = AlignmentAnalyzer.Analyze(Guid.NewGuid(), manifest, "hash", [Identity, specialized, IC],
            [TestData.Inventory(Identity), TestData.Inventory(specialized), TestData.Inventory(IC,
                TestData.Usage(IC, Prefix + ".Special.Client", "1.0"),
                TestData.Usage(IC, Prefix + "Other", "1.0"))], DateTimeOffset.UtcNow);
        Assert.AreEqual("2.0.0", new PackageRow(result.Packages.Single(package => package.PackageId.EndsWith(".Client"))).Target);
        Assert.AreEqual(PackageOwnership.Internal, result.Packages.Single(package => package.PackageId.EndsWith("Other")).Ownership);
    }

    [TestMethod]
    public void InvalidMappingsAreRejectedBeforeScanning()
    {
        foreach (var mappings in new[]
        {
            new[] { ("missing", Prefix) },
            new[] { (Identity.Id, Prefix), (Identity.Id, Prefix.ToLowerInvariant()) },
            new[] { (Identity.Id, "") },
            new[] { (Identity.Id, Prefix + ".") }
        })
        {
            var manifest = Manifest([Identity], mappings);
            Assert.Throws<ArgumentException>(() => ConfigurationPolicy.ValidateScanRequest(new(manifest, "hash", [Identity, IC], AnalysisRules.Default)));
        }
    }

    [TestMethod]
    public async Task ExcelShowsProducerTargetAndReadOnlyEmulatorComparison()
    {
        var emulator = TestData.Repo("emulator", RepositoryCategory.ICEmulator);
        var result = Analyze(Identity,
            TestData.Inventory(IC, TestData.Usage(IC, Prefix + ".Authentication", "28.0")),
            TestData.Inventory(emulator, TestData.Usage(emulator, Prefix + ".Authentication", "30.0")));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            await new ExcelReportWriter().WriteAsync(result, path, null, CancellationToken.None);
            using var workbook = new XLWorkbook(path);
            foreach (var (name, action) in new[] { ("IC Alignment", "Upgrade"), ("IC Emulator", "Downgrade") })
            {
                var sheet = workbook.Worksheet(name);
                int Column(string header) => sheet.Row(1).CellsUsed().Single(cell => cell.GetString() == header).Address.ColumnNumber;
                Assert.AreEqual("29.5.0", sheet.Cell(2, Column("Selected Target")).GetString());
                Assert.AreEqual("PlatformTag", sheet.Cell(2, Column("Target Source")).GetString());
                Assert.AreEqual(action, sheet.Cell(2, Column("Action")).GetString());
                Assert.AreEqual(Identity.Id, sheet.Cell(2, Column("Platform Producer")).GetString());
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
