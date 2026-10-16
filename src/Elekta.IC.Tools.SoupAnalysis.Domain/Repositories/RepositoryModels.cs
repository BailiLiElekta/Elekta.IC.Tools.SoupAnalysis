using System.Collections.Immutable;

namespace Elekta.IC.Tools.SoupAnalysis.Domain;

public enum RepositoryCategory { Platform, IC, ICEmulator }
public enum RefKind { Branch, Tag, Commit }

public sealed record RepositorySource(
    string Id, string Name, string Url, RepositoryCategory Category, RefKind RefKind, string Ref);

public sealed record SnapshotFile(string Path, ImmutableArray<byte> Content, string Sha256);

public sealed record SourceSnapshot(
    RepositorySource Source, string Commit, ImmutableArray<SnapshotFile> Files, DateTimeOffset CapturedAt);

public static class RepositoryClassifier
{
    public static RepositoryCategory? Classify(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Equals("workflow-management-task-management-bridge", StringComparison.OrdinalIgnoreCase))
            return RepositoryCategory.IC;
        if (new[] { "elekta.platform.workflow-manager", "elekta.platform.task-management", "elekta.platform.messaging" }
            .Contains(name, StringComparer.OrdinalIgnoreCase))
            return RepositoryCategory.IC;
        if (name.StartsWith("elekta.ic.emulator", StringComparison.OrdinalIgnoreCase))
            return RepositoryCategory.ICEmulator;
        if (name.StartsWith("elekta.platform", StringComparison.OrdinalIgnoreCase))
            return RepositoryCategory.Platform;
        if (name.StartsWith("elekta.ic", StringComparison.OrdinalIgnoreCase))
            return RepositoryCategory.IC;
        return null;
    }
}