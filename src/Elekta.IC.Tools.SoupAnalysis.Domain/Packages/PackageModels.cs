using NuGet.Versioning;
using System.IO.Enumeration;

namespace Elekta.IC.Tools.SoupAnalysis.Domain;

public enum TestClassification { Production, Test, Unknown }
public enum VersionDeclarationKind { Inline, Central, Property, VersionOverride, Inherited, Unresolved }

public sealed record XmlOwner(string SourceFile, string ElementPath, int? LineNumber = null);

public sealed record DeclarationProvenance(
    XmlOwner ReferenceOwner,
    XmlOwner VersionOwner,
    VersionDeclarationKind Kind,
    string? PropertyName = null,
    string? CentralFile = null,
    bool IsUniquelyOwned = true);

public sealed record PackageUsage(
    RepositorySource Repository,
    string ProjectPath,
    string PackageId,
    string RawVersion,
    TestClassification TestClassification,
    DeclarationProvenance Declaration,
    string? Condition = null,
    string? AssemblyName = null)
{
    public NuGetVersion? Version => PackageVersions.ParseExact(RawVersion);
}

public static class PackageVersions
{
    public static NuGetVersion? ParseExact(string rawVersion) =>
        NuGetVersion.TryParse(rawVersion, out var version) ? version : null;

    public static bool Equal(NuGetVersion left, NuGetVersion right) =>
        VersionComparer.VersionRelease.Equals(left, right);
}

public static class TestOnlyClassifier
{
    public static bool IsTestProject(string projectPath, string? assemblyName, AnalysisRules rules)
    {
        var stem = Path.GetFileNameWithoutExtension(projectPath.Replace('\\', '/'));
        return rules.TestProjectPatterns.Any(pattern =>
            FileSystemName.MatchesSimpleExpression(pattern, stem, ignoreCase: true)
            || (!string.IsNullOrWhiteSpace(assemblyName)
                && FileSystemName.MatchesSimpleExpression(pattern, assemblyName, ignoreCase: true)));
    }
}