using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Packages;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Updates;
using NuGet.Versioning;

namespace Elekta.IC.Tools.SoupAnalysis.Tests;

[TestClass]
public sealed class PackageFileEditorTests
{
    [TestMethod]
    public void RealScannerEditsAuthoritativeUpdateWithoutChangingInclude()
    {
        const string xml = "<Project><ItemGroup><PackageReference Include='X' Version='1.0'/><PackageReference Update='X' Version='1.5'/></ItemGroup></Project>";
        var snapshot = Snapshot(xml);
        var scanner = new PackageReferenceScanner();
        var usage = scanner.Scan(snapshot, AnalysisRules.Default, default).Usages.Single();
        var result = new PackageFileEditor(scanner).Edit(snapshot, [Change(usage)], AnalysisRules.Default, default);
        Assert.AreEqual(xml.Replace("Update='X' Version='1.5'", "Update='X' Version='2.0.0'", StringComparison.Ordinal),
            Encoding.UTF8.GetString(result.Files[0].Content.ToArray()));
        Assert.AreEqual("2.0.0", scanner.Scan(result, AnalysisRules.Default, default).Usages.Single().RawVersion);
    }

    [TestMethod]
    [DataRow("samples/B.vbproj")]
    [DataRow("src/B.fsproj")]
    [DataRow("samples/B.proj")]
    public void SharedCentralVersionBlocksUninventoriedBuildConsumers(string consumer)
    {
        var snapshot = Snapshot("<Project><ItemGroup><PackageReference Include='X'/></ItemGroup></Project>") with
        {
            Files = [File("src/A.csproj", "<Project><ItemGroup><PackageReference Include='X'/></ItemGroup></Project>"),
                File("Directory.Packages.props", "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include='X' Version='1.0'/></ItemGroup></Project>"),
                File(consumer, "<Project Sdk='Microsoft.NET.Sdk'><ItemGroup><PackageReference Include='X'/></ItemGroup></Project>")]
        };
        var scanner = new PackageReferenceScanner();
        var scan = scanner.Scan(snapshot, AnalysisRules.Default, default);
        Assert.HasCount(1, scan.Usages);
        Assert.IsFalse(new PackageFileEditor(scanner).CanEdit(snapshot, [Change(scan.Usages.Single())], AnalysisRules.Default, out _));
    }

    [TestMethod]
    public void EditsOnlyIndexedValuePreservingBomCrLfCommentsAndQuotes()
    {
        const string xml = "<?xml version='1.0' encoding='utf-8'?>\r\n<Project>\r\n  <!-- 1.0 must stay -->\r\n  <ItemGroup><PackageReference Include='X' Version='1.0' /><PackageReference Include='Y' Version='1.0' /></ItemGroup>\r\n</Project>\r\n";
        var usage = Usage("X", "1.0", "/Project[1]/ItemGroup[1]/PackageReference[1]/@Version");
        var snapshot = Snapshot(xml, bom: true);
        var scanner = new FixtureScanner(usage);
        var edited = new PackageFileEditor(scanner).Edit(snapshot, [Change(usage)], AnalysisRules.Default, default);
        var bytes = edited.Files.Single().Content.ToArray();
        CollectionAssert.AreEqual(Encoding.UTF8.GetPreamble(), bytes[..3]);
        Assert.AreEqual(xml.Replace("Include='X' Version='1.0'", "Include='X' Version='2.0.0'", StringComparison.Ordinal),
            Encoding.UTF8.GetString(bytes[3..]));
        Assert.AreEqual(2, scanner.Calls);
    }

    [TestMethod]
    public void RejectsStaleValueAndLocatorMismatch()
    {
        var usage = Usage("X", "1.0", "/Project[1]/ItemGroup[1]/PackageReference[1]/@Version");
        var editor = new PackageFileEditor(new FixtureScanner(usage));
        Assert.IsFalse(editor.CanEdit(Snapshot("<Project><ItemGroup><PackageReference Include='X' Version='1.1'/></ItemGroup></Project>"),
            [Change(usage)], AnalysisRules.Default, out var error));
        StringAssert.Contains(error, "expected");
        Assert.IsFalse(editor.CanEdit(Snapshot("<Project><ItemGroup><PackageReference Include='Y' Version='1.0'/></ItemGroup></Project>"),
            [Change(usage)], AnalysisRules.Default, out _));
    }

    [TestMethod]
    public void RejectsUnselectedPropertyConsumers()
    {
        var versionOwner = new XmlOwner("src/versions.props", "/Project[1]/PropertyGroup[1]/XVersion[1]");
        var first = Usage("X", "1.0", versionOwner.ElementPath) with
            { Declaration = new(new("src/A.csproj", "/Project[1]/ItemGroup[1]/PackageReference[1]"), versionOwner,
                VersionDeclarationKind.Property, "XVersion") };
        var second = first with { ProjectPath = "src/B.csproj", Declaration = first.Declaration with
            { ReferenceOwner = new("src/B.csproj", "/Project[1]/ItemGroup[1]/PackageReference[1]") } };
        var snapshot = new SourceSnapshot(first.Repository, new string('a', 40),
            [File("src/versions.props", "<Project><PropertyGroup><XVersion>1.0</XVersion></PropertyGroup></Project>"),
             File("src/A.csproj", "<Project><ItemGroup><PackageReference Include='X' Version='$(XVersion)' /></ItemGroup></Project>"),
             File("src/B.csproj", "<Project><ItemGroup><PackageReference Include='X' Version='$(XVersion)' /></ItemGroup></Project>")], DateTimeOffset.UtcNow);
        var editor = new PackageFileEditor(new FixtureScanner(first, second));
        Assert.IsFalse(editor.CanEdit(snapshot, [Change(first)], AnalysisRules.Default, out var error));
        StringAssert.Contains(error, "consumer");
    }

    [TestMethod]
    public void RejectsOutsideSrcCentralUsesEvenIfScannerOmitsThem()
    {
        var usage = Usage("X", "1.0", "/Project[1]/ItemGroup[1]/PackageVersion[1]/@Version") with
            { Declaration = new(new("src/A.csproj", "/Project[1]/ItemGroup[1]/PackageReference[1]"),
                new("Directory.Packages.props", "/Project[1]/ItemGroup[1]/PackageVersion[1]/@Version"),
                VersionDeclarationKind.Central, CentralFile: "Directory.Packages.props") };
        var snapshot = new SourceSnapshot(usage.Repository, new string('a', 40),
            [File("Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include='X' Version='1.0'/></ItemGroup></Project>"),
             File("src/A.csproj", "<Project><ItemGroup><PackageReference Include='X'/></ItemGroup></Project>"),
             File("samples/B.csproj", "<Project><ItemGroup><PackageReference Include='X'/></ItemGroup></Project>")], DateTimeOffset.UtcNow);
        Assert.IsFalse(new PackageFileEditor(new FixtureScanner(usage)).CanEdit(snapshot, [Change(usage)], AnalysisRules.Default, out var error));
        StringAssert.Contains(error, "outside");
    }

    [TestMethod]
    public void RejectsDtdAndUnknownLocatorsAndTraversal()
    {
        var usage = Usage("X", "1.0", "/Project[1]/ItemGroup[1]/PackageReference[1]/@Version");
        var editor = new PackageFileEditor(new FixtureScanner(usage));
        Assert.IsFalse(editor.CanEdit(Snapshot("<!DOCTYPE Project [<!ENTITY x '1.0'>]><Project/>"), [Change(usage)], AnalysisRules.Default, out _));
        Assert.IsFalse(editor.CanEdit(Snapshot("<Project/>"), [Change(usage with { Declaration = usage.Declaration with
            { VersionOwner = new("../escape.props", "/Project[1]") } })], AnalysisRules.Default, out _));
    }

    [TestMethod]
    public void ReparseRejectsUnexpectedPackageChanges()
    {
        var usage = Usage("X", "1.0", "/Project[1]/ItemGroup[1]/PackageReference[1]/@Version");
        var scanner = new FixtureScanner(usage) { CorruptAfterEdit = true };
        Assert.Throws<InvalidOperationException>(() => new PackageFileEditor(scanner).Edit(
            Snapshot("<Project><ItemGroup><PackageReference Include='X' Version='1.0'/></ItemGroup></Project>"),
            [Change(usage)], AnalysisRules.Default, default));
    }

    [TestMethod]
    public void RejectsVersionOwnerPointingToAnotherReference()
    {
        var usage = Usage("X", "1.0", "/Project[1]/ItemGroup[1]/PackageReference[1]/@Version");
        usage = usage with { Declaration = usage.Declaration with
            { VersionOwner = new("src/A.csproj", "/Project[1]/ItemGroup[1]/PackageReference[2]/@Version") } };
        Assert.IsFalse(new PackageFileEditor(new FixtureScanner(usage)).CanEdit(Snapshot(
            "<Project><ItemGroup><PackageReference Include='X' Version='1.0'/><PackageReference Include='Y' Version='1.0'/></ItemGroup></Project>"),
            [Change(usage)], AnalysisRules.Default, out _));
    }

    [TestMethod]
    public void SharedPropertyBlocksOutsideSrcProjectWithUnknownImports()
    {
        var usage = Usage("X", "1.0", "/Project[1]/ItemGroup[1]/PackageReference[1]/@Version") with
        {
            Declaration = new(new("src/A.csproj", "/Project[1]/ItemGroup[1]/PackageReference[1]"),
                new("src/versions.props", "/Project[1]/PropertyGroup[1]/XVersion[1]"), VersionDeclarationKind.Property, "XVersion")
        };
        var snapshot = new SourceSnapshot(usage.Repository, new string('a', 40),
            [File("src/versions.props", "<Project><PropertyGroup><XVersion>1.0</XVersion></PropertyGroup></Project>"),
             File("src/A.csproj", "<Project><ItemGroup><PackageReference Include='X' Version='$(XVersion)' /></ItemGroup></Project>"),
             File("samples/B.csproj", "<Project><Import Project='$(UnknownImports)'/></Project>")], DateTimeOffset.UtcNow);
        Assert.IsFalse(new PackageFileEditor(new FixtureScanner(usage)).CanEdit(snapshot, [Change(usage)], AnalysisRules.Default, out _));
    }

    [TestMethod]
    public void SupportsVersionChildAndUtf16WithoutFormattingChurn()
    {
        const string xml = "<?xml version='1.0' encoding='utf-16'?><Project xmlns='urn:msbuild'><ItemGroup><PackageReference Include='X'><Version> 1.0 </Version></PackageReference></ItemGroup></Project>";
        var usage = Usage("X", "1.0", "/Project[1]/ItemGroup[1]/PackageReference[1]/@Version") with
            { Declaration = new(new("src/A.csproj", "/Project[1]/ItemGroup[1]/PackageReference[1]"),
                new("src/A.csproj", "/Project[1]/ItemGroup[1]/PackageReference[1]/Version[1]"), VersionDeclarationKind.Inline) };
        var encoding = Encoding.Unicode;
        var snapshot = Snapshot("unused") with { Files = [Bytes("src/A.csproj", [.. encoding.GetPreamble(), .. encoding.GetBytes(xml)])] };
        var result = new PackageFileEditor(new FixtureScanner(usage)).Edit(snapshot, [Change(usage)], AnalysisRules.Default, default);
        Assert.AreEqual(xml.Replace("> 1.0 <", "> 2.0.0 <", StringComparison.Ordinal), encoding.GetString(result.Files[0].Content.ToArray()[2..]));
    }

    [TestMethod]
    public void FullySelectedCentralAndPropertyOwnersCanBeEdited()
    {
        foreach (var property in new[] { false, true })
        {
            var owner = property ? new XmlOwner("src/versions.props", "/Project[1]/PropertyGroup[1]/XVersion[1]")
                : new XmlOwner("Directory.Packages.props", "/Project[1]/ItemGroup[1]/PackageVersion[1]/@Version");
            var usage = Usage("X", "1.0", "/Project[1]/ItemGroup[1]/PackageReference[1]/@Version") with
                { Declaration = new(new("src/A.csproj", "/Project[1]/ItemGroup[1]/PackageReference[1]"), owner,
                    property ? VersionDeclarationKind.Property : VersionDeclarationKind.Central,
                    property ? "XVersion" : null, property ? null : owner.SourceFile) };
            var xml = property ? "<Project><PropertyGroup><XVersion>1.0</XVersion></PropertyGroup></Project>"
                : "<Project><ItemGroup><PackageVersion Include='X' Version='1.0'/></ItemGroup></Project>";
            var snapshot = Snapshot("unused") with { Files = [File(owner.SourceFile, xml), File("src/A.csproj",
                property ? "<Project><ItemGroup><PackageReference Include='X' Version='$(XVersion)'/></ItemGroup></Project>"
                    : "<Project><ItemGroup><PackageReference Include='X'/></ItemGroup></Project>")] };
            var result = new PackageFileEditor(new FixtureScanner(usage)).Edit(snapshot, [Change(usage)], AnalysisRules.Default, default);
            StringAssert.Contains(Encoding.UTF8.GetString(result.Files.Single(file => file.Path == owner.SourceFile).Content.ToArray()), "2.0.0");
        }
    }

    [TestMethod]
    public void RealScannerRoundTripsInlineChildOverridePropertyAndCentral()
    {
        foreach (var xml in new[]
        {
            "<Project><ItemGroup><PackageReference Include='X' Version='1.0'/></ItemGroup></Project>",
            "<Project><ItemGroup><PackageReference Include='X'><Version>1.0</Version></PackageReference></ItemGroup></Project>",
            "<Project><ItemGroup><PackageReference Include='X' VersionOverride='1.0'/></ItemGroup></Project>",
            "<Project><PropertyGroup><XVersion>1.0</XVersion></PropertyGroup><ItemGroup><PackageReference Include='X' Version='$(XVersion)'/></ItemGroup></Project>",
            "<Project><ItemGroup><PackageReference Include='X'/></ItemGroup></Project>"
        })
        {
            var snapshot = Snapshot(xml);
            if (xml.Contains("Include='X'/>", StringComparison.Ordinal)) snapshot = snapshot with
            {
                Files = [.. snapshot.Files, File("Directory.Packages.props",
                    "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include='X' Version='1.0'/></ItemGroup></Project>")]
            };
            var scanner = new PackageReferenceScanner();
            var before = scanner.Scan(snapshot, AnalysisRules.Default, default);
            Assert.HasCount(0, before.Issues);
            var result = new PackageFileEditor(scanner).Edit(snapshot, [Change(before.Usages.Single())], AnalysisRules.Default, default);
            Assert.AreEqual("2.0.0", scanner.Scan(result, AnalysisRules.Default, default).Usages.Single().RawVersion);
        }
    }

    [TestMethod]
    public void ReservedWindowsPathsAndCaseAliasedFilesAreRejected()
    {
        var usage = Usage("X", "1.0", "/Project[1]/ItemGroup[1]/PackageReference[1]/@Version");
        var editor = new PackageFileEditor(new FixtureScanner(usage));
        var snapshot = Snapshot("<Project><ItemGroup><PackageReference Include='X' Version='1.0'/></ItemGroup></Project>");
        Assert.IsFalse(editor.CanEdit(snapshot with { Files = [.. snapshot.Files, File("src/CON.props", "<Project/>")] },
            [Change(usage)], AnalysisRules.Default, out _));
        Assert.IsFalse(editor.CanEdit(snapshot with { Files = [.. snapshot.Files, File("src/a.csproj", "<Project/>")] },
            [Change(usage)], AnalysisRules.Default, out _));
    }

    internal static PackageUsage Usage(string package, string version, string locator) => new(TestData.Repo("ic"), "src/A.csproj",
        package, version, TestClassification.Production, new(new("src/A.csproj", locator.Contains("/@", StringComparison.Ordinal)
            ? locator[..locator.LastIndexOf("/@", StringComparison.Ordinal)] : "/Project[1]/ItemGroup[1]/PackageReference[1]"),
            new("src/A.csproj", locator), VersionDeclarationKind.Inline));
    internal static PlannedPackageChange Change(PackageUsage usage) => new(usage.Repository.Id, usage.PackageId,
        TargetSource.NuGetCandidate, NuGetVersion.Parse("2.0.0"), false, [new(usage, NuGetVersion.Parse("2.0.0"), AlignmentAction.Upgrade)]);
    internal static SnapshotFile File(string path, string xml) => Bytes(path, Encoding.UTF8.GetBytes(xml));
    private static SnapshotFile Bytes(string path, byte[] bytes) => new(path, [.. bytes], Convert.ToHexString(SHA256.HashData(bytes)));
    internal static SourceSnapshot Snapshot(string xml, bool bom = false) => new(TestData.Repo("ic"), new string('a', 40),
        [Bytes("src/A.csproj", bom ? [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(xml)] : Encoding.UTF8.GetBytes(xml))], DateTimeOffset.UtcNow);

    internal sealed class FixtureScanner(params PackageUsage[] usages) : IPackageScanner
    {
        public int Calls { get; private set; }
        public bool CorruptAfterEdit { get; init; }
        public PackageScanResult Scan(SourceSnapshot snapshot, AnalysisRules rules, CancellationToken cancellationToken)
        {
            Calls++;
            return new([.. usages.Select(usage => usage with { RawVersion = Calls == 1 ? usage.RawVersion : CorruptAfterEdit ? "8.0.0" : "2.0.0" })], []);
        }
    }
}