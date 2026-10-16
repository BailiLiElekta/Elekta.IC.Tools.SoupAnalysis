using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Elekta.IC.Tools.SoupAnalysis.Tests;

[TestClass]
public sealed class PackageScannerTests
{
    [TestMethod]
    public void UpdateVersionDoesNotEraseExistingVersionOverride()
    {
        var scan = Scan(("src/App.csproj", """
            <Project><ItemGroup><PackageReference Include="X" Version="1.0" VersionOverride="2.0"/>
            <PackageReference Update="X" Version="3.0"/></ItemGroup></Project>
            """));
        Assert.HasCount(0, scan.Issues);
        Assert.HasCount(1, scan.Usages);
        Assert.AreEqual("2.0", scan.Usages[0].RawVersion);
        Assert.AreEqual("/Project[1]/ItemGroup[1]/PackageReference[1]/@VersionOverride", scan.Usages[0].Declaration.VersionOwner.ElementPath);
    }

    [TestMethod]
    public void UnknownUpdateMetadataRemainsBlockingInsteadOfClaimingComplete()
    {
        var scan = Scan(("src/App.csproj", """
            <Project><ItemGroup><PackageReference Include="X" Version="1.0"/>
            <PackageReference Update="X"><Version Condition="'$(Unknown)' == 'true'">2.0</Version></PackageReference>
            </ItemGroup></Project>
            """));
        Assert.IsTrue(scan.Issues.Any(issue => issue.BlocksUpdates));
        Assert.IsTrue(scan.Usages.All(usage => !usage.Declaration.IsUniquelyOwned));
    }

    [TestMethod]
    public void ActiveImportIsNotRemovedByLaterPropertyAssignment()
    {
        var scan = Scan(("src/App.csproj", """
            <Project><PropertyGroup><Enabled>true</Enabled></PropertyGroup>
            <Import Project="packages.props" Condition="'$(Enabled)' == 'true'"/>
            <PropertyGroup><Enabled>false</Enabled></PropertyGroup></Project>
            """), ("src/packages.props", "<Project><ItemGroup><PackageReference Include=\"X\" Version=\"1.0.0\"/></ItemGroup></Project>"));
        Assert.HasCount(1, scan.Usages);
        Assert.AreEqual("1.0.0", scan.Usages[0].RawVersion);
        Assert.HasCount(0, scan.Issues);
        StringAssert.Contains(scan.Usages[0].Condition!, "Enabled");
    }

    [TestMethod]
    [DataRow("Version")]
    [DataRow("VersionOverride")]
    public void InactiveUpdateMetadataRetainsIncludeVersionAndOwner(string metadata)
    {
        var scan = Scan(("src/App.csproj", $"""
            <Project><ItemGroup><PackageReference Include="X" Version="1.0.0"/>
            <PackageReference Update="X"><{metadata} Condition="'false' == 'true'">1.5.0</{metadata}></PackageReference>
            </ItemGroup></Project>
            """));
        Assert.HasCount(1, scan.Usages);
        Assert.HasCount(0, scan.Issues);
        Assert.AreEqual("1.0.0", scan.Usages[0].RawVersion);
        Assert.AreEqual("/Project[1]/ItemGroup[1]/PackageReference[1]/@Version", scan.Usages[0].Declaration.VersionOwner.ElementPath);
    }

    [TestMethod]
    public void InlinePropertiesOverridesAndNamingAreInventoriedOnlyUnderSrc()
    {
        var scan = Scan(("src/Client.Test.Unit/Client.Test.Unit.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsTestProject>false</IsTestProject><V>2.1.0</V></PropertyGroup>
            <ItemGroup><PackageReference Include="A" Version="$(V)" PrivateAssets="all"/><PackageReference Include="B"><Version>3.0.0</Version></PackageReference>
            <PackageReference Include="C" VersionOverride="4.0.0" /></ItemGroup></Project>
            """), ("tests/Skipped.csproj", "<Project><ItemGroup><PackageReference Include=\"X\" Version=\"9.0.0\"/></ItemGroup></Project>"),
            ("src/obj/Skipped.csproj", "<Project />"));
        Assert.HasCount(3, scan.Usages);
        Assert.HasCount(0, scan.Issues);
        Assert.IsTrue(scan.Usages.All(usage => usage.TestClassification == TestClassification.Test));
        Assert.AreEqual("/Project[1]/PropertyGroup[1]/V[1]", scan.Usages.Single(usage => usage.PackageId == "A").Declaration.VersionOwner.ElementPath);
        Assert.AreEqual("/Project[1]/ItemGroup[1]/PackageReference[2]/Version[1]", scan.Usages.Single(usage => usage.PackageId == "B").Declaration.VersionOwner.ElementPath);
        Assert.AreEqual("/Project[1]/ItemGroup[1]/PackageReference[3]/@VersionOverride", scan.Usages.Single(usage => usage.PackageId == "C").Declaration.VersionOwner.ElementPath);
    }

    [TestMethod]
    public void NearestCentralAndInheritedReferencesResolveWithoutUnusedVersions()
    {
        var scan = Scan(("Directory.Build.props", "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally><V>2.0.0</V></PropertyGroup><Import Project=\"versions.custom\" /></Project>"),
            ("versions.custom", "<Project><ItemGroup><PackageReference Include=\"Inherited\" VersionOverride=\"1.0.0\" /></ItemGroup></Project>"),
            ("Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"A\" Version=\"9.0.0\" /></ItemGroup></Project>"),
            ("src/Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"A\" Version=\"$(V)\"/><PackageVersion Include=\"Unused\" Version=\"5.0.0\"/></ItemGroup></Project>"),
            ("src/App.csproj", "<Project><ItemGroup><PackageReference Include=\"A\"/><PackageReference Update=\"Inherited\" VersionOverride=\"1.2.0\" /></ItemGroup></Project>"));
        Assert.HasCount(2, scan.Usages);
        Assert.HasCount(0, scan.Issues);
        var central = scan.Usages.Single(usage => usage.PackageId == "A");
        Assert.AreEqual("2.0.0", central.RawVersion);
        Assert.AreEqual("Directory.Build.props", central.Declaration.VersionOwner.SourceFile);
        Assert.AreEqual("src/Directory.Packages.props", central.Declaration.CentralFile);
        Assert.AreEqual("1.2.0", scan.Usages.Single(usage => usage.PackageId == "Inherited").RawVersion);
    }

    [TestMethod]
    public void MissingImportsDtdUnknownConditionsAndRangesAreExplicitBlockingIssues()
    {
        var scan = Scan(("src/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><Import Project="missing.custom"/><ItemGroup>
            <PackageReference Include="A" Version="1.*"/><PackageReference Include="B" Version="2.0.0" Condition="'$(Unknown)' == 'true'" />
            <PackageReference Include="C" Version="$(Missing)" /></ItemGroup></Project>
            """), ("src/Bad.csproj", "<!DOCTYPE Project [<!ENTITY leak SYSTEM 'file:///private'>]><Project>&leak;</Project>"));
        Assert.HasCount(3, scan.Usages);
        Assert.IsTrue(scan.Issues.Count(issue => issue.BlocksUpdates) >= 4);
        Assert.IsTrue(scan.Issues.Any(issue => issue.Code == "ImportMissing"));
        Assert.IsTrue(scan.Issues.Any(issue => issue.Code == "InvalidXml"));
        Assert.IsFalse(scan.Usages.Single(usage => usage.PackageId == "B").Declaration.IsUniquelyOwned);
    }

    [TestMethod]
    public void ConditionalPropertyAlternativesAndSharedPropertyOwnershipAreNotGuessed()
    {
        var scan = Scan(("src/App.csproj", """
            <Project><PropertyGroup><V>1.0.0</V><V Condition="'$(Flavor)' == 'special'">2.0.0</V></PropertyGroup><ItemGroup>
            <PackageReference Include="A" Version="$(V)"/><PackageReference Include="B" Version="$(V)"/></ItemGroup></Project>
            """));
        Assert.HasCount(4, scan.Usages);
        Assert.IsTrue(scan.Issues.Any(issue => issue.BlocksUpdates));
        Assert.IsTrue(scan.Usages.All(usage => !usage.Declaration.IsUniquelyOwned));
        CollectionAssert.AreEquivalent(new[] { "1.0.0", "2.0.0" }, scan.Usages.Where(usage => usage.PackageId == "A").Select(usage => usage.RawVersion).ToArray());
    }

    [TestMethod]
    public void AssemblyNameAndFilenameOnlyDriveTestClassificationAndLocatorRoundTrips()
    {
        var scan = Scan(("src/App.csproj", "<Project><PropertyGroup><AssemblyName>Test.Helpers</AssemblyName></PropertyGroup><ItemGroup><PackageReference Include=\"A\" Version=\"1.0.0\"/></ItemGroup></Project>"),
            ("src/Production.csproj", "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"A\" Version=\"1.0.0\"/></ItemGroup></Project>"));
        Assert.AreEqual(TestClassification.Test, scan.Usages.Single(usage => usage.ProjectPath == "src/App.csproj").TestClassification);
        Assert.AreEqual(TestClassification.Production, scan.Usages.Single(usage => usage.ProjectPath == "src/Production.csproj").TestClassification);
        var document = XDocument.Parse("<Project xmlns=\"urn:test\"><ItemGroup/><ItemGroup><PackageReference Version=\"1\"/></ItemGroup></Project>");
        var attribute = document.Descendants().Last().Attribute("Version")!;
        Assert.AreEqual("/Project[1]/ItemGroup[2]/PackageReference[1]/@Version", XmlLocator.GetPath(attribute));
        Assert.AreSame(attribute, XmlLocator.Find(document, XmlLocator.GetPath(attribute)));
    }

    [TestMethod]
    public void ConditionalCentralAndVersionMetadataCannotClaimUniqueOwnership()
    {
        var scan = Scan(("src/Directory.Packages.props", "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include=\"A\" Version=\"1.0.0\" Condition=\"'$(Flavor)' == 'a'\"/></ItemGroup></Project>"),
            ("src/App.csproj", "<Project><ItemGroup><PackageReference Include=\"A\"/><PackageReference Include=\"B\"><VersionOverride Condition=\"'$(Flavor)' == 'a'\">2.0.0</VersionOverride><VersionOverride Condition=\"'$(Flavor)' == 'b'\">3.0.0</VersionOverride></PackageReference></ItemGroup></Project>"));
        Assert.HasCount(3, scan.Usages);
        Assert.IsTrue(scan.Usages.All(usage => !usage.Declaration.IsUniquelyOwned));
        Assert.IsTrue(scan.Issues.Any(issue => issue.BlocksUpdates));
    }

    [TestMethod]
    public void UnreadableExistsGuardAndAbsoluteImportAreBlocking()
    {
        var scan = Scan(("src/App.csproj", "<Project><Import Project=\"missing.custom\" Condition=\"Exists('missing.custom')\"/><Import Project=\"/outside.props\"/><ItemGroup><PackageReference Include=\"A\" Version=\"1.0.0\"/></ItemGroup></Project>"),
            ("outside.props", "<Project/>"));
        Assert.IsTrue(scan.Issues.Any(issue => issue.Code == "ImportMissing"));
        Assert.IsTrue(scan.Issues.Any(issue => issue.Code == "ImportUnsafe"));
    }

    [TestMethod]
    public void SharedPropertyAcrossDifferentPackagesIsNeverUnique()
    {
        var scan = Scan(("src/App.csproj", "<Project><PropertyGroup><V>1.0.0</V></PropertyGroup><ItemGroup><PackageReference Include=\"A\" Version=\"$(V)\"/><PackageReference Include=\"B\" Version=\"$(V)\"/></ItemGroup></Project>"));
        Assert.HasCount(2, scan.Usages);
        Assert.HasCount(0, scan.Issues);
        Assert.IsTrue(scan.Usages.All(usage => !usage.Declaration.IsUniquelyOwned));
    }

    [TestMethod]
    public async Task CancellationIsObservedBeforeParsing()
    {
        using var token = new CancellationTokenSource();
        token.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Task.Run(() => new PackageReferenceScanner().Scan(Snapshot(("src/App.csproj", "<Project/>")), AnalysisRules.Default, token.Token)));
    }

    private static Elekta.IC.Tools.SoupAnalysis.Application.PackageScanResult Scan(params (string Path, string Xml)[] files) =>
        new PackageReferenceScanner().Scan(Snapshot(files), AnalysisRules.Default, default);

    private static SourceSnapshot Snapshot(params (string Path, string Xml)[] files) => new(TestData.Repo("client"), new string('a', 40),
        [.. files.Select(file => { var bytes = Encoding.UTF8.GetBytes(file.Xml); return new SnapshotFile(file.Path, [.. bytes], Convert.ToHexString(SHA256.HashData(bytes))); })], DateTimeOffset.UtcNow);
}