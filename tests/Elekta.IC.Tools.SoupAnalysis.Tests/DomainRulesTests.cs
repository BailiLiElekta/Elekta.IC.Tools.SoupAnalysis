using Elekta.IC.Tools.SoupAnalysis.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Elekta.IC.Tools.SoupAnalysis.Tests;

[TestClass]
public sealed class DomainRulesTests
{
    [TestMethod]
    [DataRow("workflow-management-task-management-bridge", RepositoryCategory.IC)]
    [DataRow("WORKFLOW-MANAGEMENT-TASK-MANAGEMENT-BRIDGE", RepositoryCategory.IC)]
    [DataRow("elekta.platform.workflow-manager", RepositoryCategory.IC)]
    [DataRow("elekta.platform.task-management", RepositoryCategory.IC)]
    [DataRow("elekta.platform.messaging", RepositoryCategory.IC)]
    [DataRow("ELEKTA.IC.EMULATOR.ois", RepositoryCategory.ICEmulator)]
    [DataRow("elekta.platform.logging", RepositoryCategory.Platform)]
    [DataRow("elekta.ic.process", RepositoryCategory.IC)]
    public void NamesRespectExceptions(string name, RepositoryCategory expected) =>
        Assert.AreEqual(expected, RepositoryClassifier.Classify(name));

    [TestMethod]
    public void UnknownNamesNeedExplicitClassification() => Assert.IsNull(RepositoryClassifier.Classify("custom"));

    [TestMethod]
    [DataRow("src/Test.Helpers/Test.Helpers.csproj", null, true)]
    [DataRow("src/Client.Test.Unit/Client.Test.Unit.csproj", null, true)]
    [DataRow("src/Foo.UnitTests/Foo.UnitTests.csproj", null, true)]
    [DataRow("src/Main.csproj", "Test.Assembly", true)]
    [DataRow("src/Main.Tests/Main.Tests.csproj", null, false)]
    public void TestClassificationIsNamingOnly(string path, string? assembly, bool expected) =>
        Assert.AreEqual(expected, TestOnlyClassifier.IsTestProject(path, assembly, AnalysisRules.Default));

    [TestMethod]
    public void SharedPackagesUseOnlyPlatformMaxAndReduceAllUsages()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var client = TestData.Repo("client");
        var emulator = TestData.Repo("emulator", RepositoryCategory.ICEmulator);
        var result = TestData.Analyze(
            TestData.Inventory(platform, TestData.Usage(platform, "X", "1.0", TestClassification.Test),
                TestData.Usage(platform, "X", "2.0", TestClassification.Test)),
            TestData.Inventory(client, TestData.Usage(client, "x", "1.5", TestClassification.Test),
                TestData.Usage(client, "X", "3.0")),
            TestData.Inventory(emulator, TestData.Usage(emulator, "X", "99.0")));
        Assert.HasCount(1, result.Packages);
        var decision = result.Packages[0];
        Assert.AreEqual(PackageOwnership.SharedWithPlatform, decision.Ownership);
        Assert.AreEqual("2.0.0", decision.PlatformMax?.ToNormalizedString());
        Assert.AreEqual("3.0.0", decision.ICMax?.ToNormalizedString());
        Assert.IsFalse(decision.Repositories.Single(repo => repo.RepositoryId == client.Id).TestOnly);
        Assert.IsTrue(decision.Repositories.Single(repo => repo.RepositoryId == platform.Id).TestOnly);
        Assert.IsFalse(decision.Repositories.Any(repo => repo.RepositoryId == emulator.Id));
        Assert.IsTrue(result.CanUpdate);
    }

    [TestMethod]
    [DataRow("1.5", AlignmentAction.Upgrade)]
    [DataRow("3.0", AlignmentAction.Downgrade)]
    [DataRow("2.0.0+metadata", AlignmentAction.Aligned)]
    public void SharedActionsAreLockedToPlatform(string version, AlignmentAction action)
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var client = TestData.Repo("client");
        var result = TestData.Analyze(TestData.Inventory(platform, TestData.Usage(platform, "X", "2.0")),
            TestData.Inventory(client, TestData.Usage(client, "X", version)));
        Assert.HasCount(1, result.Packages);
        Assert.AreEqual(action, result.Packages[0].Repositories.Single(repo => repo.RepositoryId == client.Id).Action);
    }

    [TestMethod]
    public void SoupInternalAndPrereleaseRulesAreIndependent()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var client = TestData.Repo("client");
        var result = TestData.Analyze(TestData.Inventory(platform, TestData.Usage(platform, "elekta.shared", "1.0")),
            TestData.Inventory(client, TestData.Usage(client, "Y", "2.0-rc.2"),
                TestData.Usage(client, "y", "2.0-rc.10"), TestData.Usage(client, "Elekta.Z", "1.0"),
                TestData.Usage(client, "elekta.shared", "0.9")));
        Assert.HasCount(3, result.Packages);
        Assert.AreEqual("2.0.0-rc.10", result.Packages.Single(package => package.IsSoup).ICMax?.ToNormalizedString());
        Assert.AreEqual(PackageOwnership.Internal, result.Packages.Single(package => package.PackageId == "Elekta.Z").Ownership);
        Assert.AreEqual(PackageOwnership.SharedWithPlatform,
            result.Packages.Single(package => package.PackageId == "elekta.shared").Ownership);
    }

    [TestMethod]
    public void InternalPackagesRemainNotApplicableWithoutAnExplicitCustomTarget()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var ic = TestData.Repo("ic");
        var other = TestData.Repo("other");
        var analysis = TestData.Analyze(TestData.Inventory(platform),
            TestData.Inventory(ic, TestData.Usage(ic, "elekta.Internal", "1.0")),
            TestData.Inventory(other, TestData.Usage(other, "elekta.Internal", "9.0-rc.1")));
        var package = analysis.Packages.Single();
        Assert.AreEqual(PackageOwnership.Internal, package.Ownership);
        Assert.IsFalse(package.IsSoup);
        Assert.IsTrue(package.Repositories.All(repository => repository.Action == AlignmentAction.NotApplicable));
        Assert.IsNull(package.NuGetCandidate);
        Assert.AreEqual("9.0.0-rc.1", package.ICMax!.ToNormalizedString());
    }

    [TestMethod]
    public void StableWinsAndRawStringsArePreserved()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var client = TestData.Repo("client");
        var result = TestData.Analyze(TestData.Inventory(platform), TestData.Inventory(client,
            TestData.Usage(client, "X", "1.0-rc.10"), TestData.Usage(client, "X", "1.0")));
        Assert.HasCount(1, result.Packages);
        Assert.AreEqual("1.0.0", result.Packages[0].ICMax?.ToNormalizedString());
        Assert.AreEqual("1.0", result.Inventories[1].Usages[1].RawVersion);
        Assert.IsTrue(PackageVersions.Equal(PackageVersions.ParseExact("1.0")!, PackageVersions.ParseExact("1.0.0")!));
    }

    [TestMethod]
    [DataRow("[1.0,2.0)")]
    [DataRow("1.*")]
    [DataRow("$(Unknown)")]
    public void UnresolvedVersionsBlockUpdates(string version)
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var client = TestData.Repo("client");
        var result = TestData.Analyze(TestData.Inventory(platform),
            TestData.Inventory(client, TestData.Usage(client, "X", version)));
        Assert.IsNull(PackageVersions.ParseExact(version));
        Assert.IsFalse(result.CompleteIC);
        Assert.IsFalse(result.CanUpdate);
    }

    [TestMethod]
    public void MissingPlatformCannotProveSoupButEmulatorFailureIsIsolated()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var client = TestData.Repo("client");
        var emulator = TestData.Repo("emulator", RepositoryCategory.ICEmulator);
        var inventories = new[] { TestData.Inventory(client, TestData.Usage(client, "X", "1.0")) };
        var result = AlignmentAnalyzer.Analyze(Guid.NewGuid(), new(1, "1", [platform]), "hash",
            [platform, client], [.. inventories], DateTimeOffset.UtcNow);
        Assert.HasCount(1, result.Packages);
        Assert.IsFalse(result.CompletePlatform);
        Assert.AreEqual(PackageOwnership.Unconfirmed, result.Packages[0].Ownership);
        var complete = TestData.Analyze(TestData.Inventory(platform), inventories[0],
            new(emulator, null, [], [new("read", "failure")], InventoryStatus.Failed));
        Assert.IsTrue(complete.CanUpdate);
    }

    [TestMethod]
    public void ComparisonHonorsCancellation()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => AlignmentAnalyzer.Analyze(Guid.NewGuid(),
            new(1, "1", [platform]), "hash", [platform], [TestData.Inventory(platform)], DateTimeOffset.UtcNow, cancellation.Token));
    }

    [TestMethod]
    public void MissingNormalICAndUnresolvedPlatformCannotAuthorizeUpdates()
    {
        var platform = TestData.Repo("platform", RepositoryCategory.Platform);
        var client = TestData.Repo("client");
        var result = AlignmentAnalyzer.Analyze(Guid.NewGuid(), new(1, "1", [platform]), "hash",
            [platform, client], [TestData.Inventory(platform)], DateTimeOffset.UtcNow);
        Assert.IsTrue(result.CompletePlatform);
        Assert.IsFalse(result.CompleteIC);
        Assert.IsFalse(result.CanUpdate);
        var unresolved = TestData.Analyze(TestData.Inventory(platform, TestData.Usage(platform, "Shared", "[1.0,2.0)")),
            TestData.Inventory(client, TestData.Usage(client, "Shared", "3.0"), TestData.Usage(client, "Other", "1.0")));
        Assert.IsFalse(unresolved.CompletePlatform);
        Assert.IsFalse(unresolved.CanUpdate);
        Assert.IsNull(unresolved.Packages.Single(package => package.PackageId == "Shared").PlatformMax);
        Assert.AreEqual(PackageOwnership.Unconfirmed, unresolved.Packages.Single(package => package.PackageId == "Other").Ownership);
    }
}