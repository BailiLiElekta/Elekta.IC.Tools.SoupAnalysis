using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Domain;

namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure.Updates;

public sealed class PackageFileEditor(IPackageScanner scanner)
{
    public bool CanEdit(SourceSnapshot snapshot, IReadOnlyList<PlannedPackageChange> changes,
        AnalysisRules rules, out string error)
    {
        try
        {
            Prepare(snapshot, changes, rules, CancellationToken.None);
            error = "";
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException
            or XmlException or ArgumentException or DecoderFallbackException)
        {
            error = exception.Message;
            return false;
        }
    }

    public SourceSnapshot Edit(SourceSnapshot snapshot, IReadOnlyList<PlannedPackageChange> changes,
        AnalysisRules rules, CancellationToken cancellationToken)
    {
        var preparation = Prepare(snapshot, changes, rules, cancellationToken);
        var files = snapshot.Files.Select(file =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!preparation.Edits.TryGetValue(file.Path, out var edits)) return file;
            var document = preparation.Documents[file.Path];
            var text = document.Text;
            foreach (var edit in edits.OrderByDescending(edit => edit.Start))
                text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Value);
            byte[] content = [.. document.Preamble, .. document.Encoding.GetBytes(text)];
            return new SnapshotFile(file.Path, [.. content], Convert.ToHexString(SHA256.HashData(content)));
        }).ToImmutableArray();
        var result = snapshot with { Files = files };
        var after = scanner.Scan(result, rules, cancellationToken);
        if (after.Issues.Any(issue => issue.BlocksUpdates) || after.Usages.Length != preparation.Before.Usages.Length)
            throw new InvalidOperationException("Reparse did not preserve the approved package inventory.");
        var expected = preparation.Before.Usages.Select(usage => preparation.Selected.TryGetValue(Key(usage), out var change)
            ? usage with { RawVersion = change.Target.ToNormalizedString() } : usage).ToArray();
        if (!expected.OrderBy(Key, StringComparer.Ordinal).SequenceEqual(after.Usages.OrderBy(Key, StringComparer.Ordinal)))
            throw new InvalidOperationException("Reparse changed package versions or provenance outside the approved changes.");
        return result;
    }

    private Preparation Prepare(SourceSnapshot snapshot, IReadOnlyList<PlannedPackageChange> changes,
        AnalysisRules rules, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.Source.Category != RepositoryCategory.IC || snapshot.Source.RefKind != RefKind.Branch || changes.Count == 0)
            throw new InvalidOperationException("Only selected IC branch changes may be edited.");
        var documents = new Dictionary<string, Document>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in snapshot.Files)
        {
            SafeRelativePath(file.Path);
            if (!paths.Add(file.Path)) throw new InvalidOperationException("Case-aliased snapshot files are not safe on Windows.");
            if (!Convert.ToHexString(SHA256.HashData(file.Content.AsSpan())).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Snapshot file hash mismatch.");
            if (IsBuildXml(file.Path)) documents.Add(file.Path, Document.Read(file));
        }
        var before = scanner.Scan(snapshot, rules, cancellationToken);
        if (before.Issues.Any(issue => issue.BlocksUpdates) || before.Usages.Any(usage => usage.Version is null
            || !usage.Declaration.IsUniquelyOwned || usage.Declaration.Kind == VersionDeclarationKind.Unresolved))
            throw new InvalidOperationException("Scanner cannot prove source ownership for all consumers.");
        var selected = new Dictionary<string, UsageChange>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (change.RepositoryId != snapshot.Source.Id || change.Usages.IsDefaultOrEmpty)
                throw new InvalidOperationException("Change repository or usages do not match snapshot.");
            foreach (var edit in change.Usages)
            {
                if (edit.Usage.PackageId != change.PackageId || !PackageVersions.Equal(edit.Target, change.Target)
                    || edit.Usage.Repository != snapshot.Source || !edit.Usage.Declaration.IsUniquelyOwned
                    || edit.Action is not AlignmentAction.Upgrade and not AlignmentAction.Downgrade)
                    throw new InvalidOperationException("Change is not an exact approved package edit.");
                var actual = before.Usages.Where(usage => Key(usage) == Key(edit.Usage)).ToArray();
                if (actual.Length != 1 || actual[0] != edit.Usage || !selected.TryAdd(Key(edit.Usage), edit))
                    throw new InvalidOperationException("Reviewed usage differs from current scanner result or is duplicated.");
            }
        }
        var edits = new Dictionary<string, List<TextEdit>>(StringComparer.Ordinal);
        foreach (var group in selected.Values.GroupBy(edit => OwnerKey(edit.Usage.Declaration.VersionOwner)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = group.First();
            var owner = first.Usage.Declaration.VersionOwner;
            if (group.Any(edit => edit.Usage.RawVersion != first.Usage.RawVersion || !PackageVersions.Equal(edit.Target, first.Target)))
                throw new InvalidOperationException("One XML owner has conflicting approved targets.");
            var consumers = before.Usages.Where(usage => OwnerKey(usage.Declaration.VersionOwner) == group.Key).ToArray();
            if (consumers.Any(usage => !selected.ContainsKey(Key(usage))))
                throw new InvalidOperationException("Version owner has an unselected consumer.");
            foreach (var edit in group)
            {
                var reference = Resolve(documents, edit.Usage.Declaration.ReferenceOwner) as XElement;
                if (reference?.Name.LocalName != "PackageReference"
                    || !string.Equals((string?)reference.Attribute("Include") ?? (string?)reference.Attribute("Update"),
                        edit.Usage.PackageId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Reference locator does not identify the expected package.");
            }
            var value = Resolve(documents, owner);
            ValidateOwner(first.Usage, value);
            var current = value is XAttribute attribute ? attribute.Value : ((XElement)value).Value.Trim();
            if (current != first.Usage.RawVersion)
                throw new InvalidOperationException("Version owner does not contain the expected old value.");
            CheckCollateral(documents, before.Usages, selected, first.Usage, owner);
            var document = documents[owner.SourceFile];
            var span = document.ValueSpan(value);
            if (!edits.TryGetValue(owner.SourceFile, out var fileEdits)) edits[owner.SourceFile] = fileEdits = [];
            if (fileEdits.Any(edit => span.Start < edit.Start + edit.Length && edit.Start < span.Start + span.Length))
                throw new InvalidOperationException("Overlapping XML edits are ambiguous.");
            fileEdits.Add(new(span.Start, span.Length, first.Target.ToNormalizedString()));
        }
        return new(documents, edits, before, selected);
    }

    private static void ValidateOwner(PackageUsage usage, XObject value)
    {
        var kind = usage.Declaration.Kind;
        var parent = value is XAttribute attribute ? attribute.Parent : ((XElement)value).Parent;
        if (kind is not VersionDeclarationKind.Property and not VersionDeclarationKind.Central
            && (parent is null || parent.Name.LocalName != "PackageReference"
                || !string.Equals((string?)parent.Attribute("Include") ?? (string?)parent.Attribute("Update"),
                    usage.PackageId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Version owner is not part of the expected package reference.");
        if (kind == VersionDeclarationKind.Central && (parent?.Name.LocalName != "PackageVersion"
            || usage.Declaration.CentralFile != usage.Declaration.VersionOwner.SourceFile))
            throw new InvalidOperationException("Central version owner does not match the approved CPM file.");
        if (value is XAttribute versionAttribute)
        {
            var expected = kind == VersionDeclarationKind.VersionOverride ? "VersionOverride" : "Version";
            if (versionAttribute.Name.Namespace != XNamespace.None || versionAttribute.Name.LocalName != expected
                || parent?.Name.LocalName is not "PackageReference" and not "PackageVersion")
                throw new InvalidOperationException("Unsupported version attribute owner.");
            if (parent.Name.LocalName == "PackageVersion"
                && !string.Equals((string?)parent.Attribute("Include") ?? (string?)parent.Attribute("Update"), usage.PackageId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Central locator identifies another package.");
        }
        else
        {
            var element = (XElement)value;
            if (element.HasElements || element.Nodes().Any(node => node is not XText || node is XCData))
                throw new InvalidOperationException("Complex version content cannot be edited.");
            if (kind == VersionDeclarationKind.Property)
            {
                if (element.Name.LocalName != usage.Declaration.PropertyName || parent?.Name.LocalName != "PropertyGroup")
                    throw new InvalidOperationException("Property locator does not identify the expected property.");
            }
            else if (element.Name.LocalName != (kind == VersionDeclarationKind.VersionOverride ? "VersionOverride" : "Version")
                || parent?.Name.LocalName is not "PackageReference" and not "PackageVersion")
                throw new InvalidOperationException("Unsupported version element owner.");
        }
    }

    private static void CheckCollateral(Dictionary<string, Document> documents, ImmutableArray<PackageUsage> usages,
        Dictionary<string, UsageChange> selected, PackageUsage affected, XmlOwner owner)
    {
        var property = affected.Declaration.PropertyName;
        var central = affected.Declaration.Kind == VersionDeclarationKind.Central || affected.Declaration.CentralFile is not null;
        if (!central && property is null && affected.Declaration.ReferenceOwner.SourceFile.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var (path, document) in documents)
        {
            if (IsBuildProject(path) && (!path.StartsWith("src/", StringComparison.OrdinalIgnoreCase)
                || !path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Shared version has an unverified project outside the src C# safety scope.");
            foreach (var element in document.Xml.Descendants())
            {
                var consumesProperty = property is not null && (element.Attributes().Any(attribute =>
                    attribute.Value.Contains("$(" + property + ")", StringComparison.OrdinalIgnoreCase))
                    || !element.HasElements && element.Value.Contains("$(" + property + ")", StringComparison.OrdinalIgnoreCase));
                var packageNode = element.Name.LocalName is "PackageReference" or "PackageVersion";
                var consumesPackage = central && packageNode && string.Equals((string?)element.Attribute("Include")
                    ?? (string?)element.Attribute("Update"), affected.PackageId, StringComparison.OrdinalIgnoreCase);
                var inherited = element.Name.LocalName == "PackageReference" && path == affected.Declaration.ReferenceOwner.SourceFile
                    && Locator(element) == affected.Declaration.ReferenceOwner.ElementPath;
                if (!consumesProperty && !consumesPackage && !inherited) continue;
                if (!path.StartsWith("src/", StringComparison.OrdinalIgnoreCase)
                    && !(path == owner.SourceFile && OwnerElement(owner.ElementPath) == Locator(element)))
                    throw new InvalidOperationException("Version has a consumer outside the src safety scope.");
                if (element.Name.LocalName == "PackageVersion" && path == owner.SourceFile
                    && OwnerElement(owner.ElementPath) == Locator(element)) continue;
                var known = usages.Where(usage => usage.Declaration.ReferenceOwner.SourceFile == path
                    && usage.Declaration.ReferenceOwner.ElementPath == Locator(element)).ToArray();
                if (known.Length == 0 || known.Any(usage => OwnerKey(usage.Declaration.VersionOwner) != OwnerKey(owner)
                    || !selected.ContainsKey(Key(usage))))
                    throw new InvalidOperationException("Version has an unknown or unselected consumer.");
            }
        }
    }

    private static XObject Resolve(Dictionary<string, Document> documents, XmlOwner owner)
    {
        SafeRelativePath(owner.SourceFile);
        if (!documents.TryGetValue(owner.SourceFile, out var document))
            throw new InvalidOperationException("XML owner source is missing.");
        var parts = owner.ElementPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!owner.ElementPath.StartsWith('/') || parts.Length == 0)
            throw new InvalidOperationException("Invalid indexed XML locator.");
        XElement? current = null;
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].StartsWith('@'))
            {
                if (index != parts.Length - 1 || current is null) throw new InvalidOperationException("Invalid attribute locator.");
                return current.Attributes().SingleOrDefault(attribute => attribute.Name.Namespace == XNamespace.None
                    && attribute.Name.LocalName == parts[index][1..]) ?? throw new InvalidOperationException("Expected attribute is missing.");
            }
            var match = Regex.Match(parts[index], "^([A-Za-z_][A-Za-z0-9_.-]*)\\[([1-9][0-9]*)\\]$", RegexOptions.CultureInvariant);
            if (!match.Success || !int.TryParse(match.Groups[2].Value, out var sibling))
                throw new InvalidOperationException("Only stable indexed XML locators are supported.");
            var candidates = (current is null ? new[] { document.Xml.Root! } : current.Elements())
                .Where(element => element.Name.LocalName == match.Groups[1].Value).ToArray();
            if (sibling > candidates.Length) throw new InvalidOperationException("Expected XML element is missing.");
            current = candidates[sibling - 1];
        }
        return current!;
    }

    internal static string Locator(XElement element) => (element.Parent is null ? "" : Locator(element.Parent)) + "/"
        + element.Name.LocalName + "[" + (element.ElementsBeforeSelf().Count(sibling => sibling.Name.LocalName == element.Name.LocalName) + 1) + "]";
    private static string OwnerElement(string locator) => locator.Contains("/@", StringComparison.Ordinal)
        ? locator[..locator.LastIndexOf("/@", StringComparison.Ordinal)] : locator;
    private static string OwnerKey(XmlOwner owner) => owner.SourceFile + "|" + owner.ElementPath;
    private static string Key(PackageUsage usage) => usage.Repository.Id + "|" + usage.ProjectPath + "|" + usage.PackageId.ToUpperInvariant()
        + "|" + OwnerKey(usage.Declaration.ReferenceOwner) + "|" + OwnerKey(usage.Declaration.VersionOwner) + "|" + usage.Condition;
    public static bool IsBuildProject(string path) => new[] { ".csproj", ".vbproj", ".fsproj", ".proj" }
        .Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    public static bool IsBuildXml(string path) => IsBuildProject(path) || new[] { ".props", ".targets" }
        .Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    public static void SafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || Path.IsPathRooted(path) || path.Contains(':')
            || path.Split('/').Any(part => part is "" or "." or ".." || part.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || IsDeviceName(part.Split('.')[0])))
            throw new InvalidOperationException("Unsafe repository-relative file path.");
    }

    private static bool IsDeviceName(string name) => new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }
        .Contains(name, StringComparer.OrdinalIgnoreCase)
        || name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && char.IsAsciiDigit(name[3]);

    private sealed record TextEdit(int Start, int Length, string Value);
    private sealed record Preparation(Dictionary<string, Document> Documents, Dictionary<string, List<TextEdit>> Edits,
        PackageScanResult Before, Dictionary<string, UsageChange> Selected);

    private sealed record Document(string Text, Encoding Encoding, byte[] Preamble, XDocument Xml)
    {
        public static Document Read(SnapshotFile file)
        {
            var bytes = file.Content.ToArray();
            Encoding encoding = new UTF8Encoding(false, true);
            var skip = 0;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) skip = 3;
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
            var text = encoding.GetString(bytes, skip, bytes.Length - skip);
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
            var xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            if (xml.Root?.Name.LocalName != "Project") throw new InvalidDataException("Build XML must have a Project root.");
            if (xml.Declaration?.Encoding is { } declared
                && !declared.Equals(encoding.WebName, StringComparison.OrdinalIgnoreCase)
                && !(declared.Equals("utf-16", StringComparison.OrdinalIgnoreCase) && encoding is UnicodeEncoding))
                throw new InvalidDataException("Unsupported or mismatched XML encoding.");
            return new(text, encoding, bytes[..skip], xml);
        }

        public (int Start, int Length) ValueSpan(XObject value)
        {
            var info = (IXmlLineInfo)value;
            var offset = 0;
            for (var line = 1; line < info.LineNumber; line++)
            {
                var next = Text.IndexOf('\n', offset);
                if (next < 0) throw new InvalidOperationException("XML line position is invalid.");
                offset = next + 1;
            }
            offset += info.LinePosition - 1;
            if (value is XAttribute)
            {
                var equals = Text.IndexOf('=', offset);
                var quote = equals + 1;
                while (quote < Text.Length && char.IsWhiteSpace(Text[quote])) quote++;
                if (equals < 0 || quote >= Text.Length || Text[quote] is not '\'' and not '"')
                    throw new InvalidOperationException("XML attribute span is invalid.");
                var end = Text.IndexOf(Text[quote], quote + 1);
                return (quote + 1, end - quote - 1);
            }
            var cursor = offset;
            char quoted = '\0';
            for (; cursor < Text.Length; cursor++)
            {
                var character = Text[cursor];
                if (quoted != '\0') { if (character == quoted) quoted = '\0'; }
                else if (character is '\'' or '"') quoted = character;
                else if (character == '>') break;
            }
            var start = cursor + 1;
            var finish = Text.IndexOf('<', start);
            if (finish < start) throw new InvalidOperationException("XML text span is invalid.");
            while (start < finish && char.IsWhiteSpace(Text[start])) start++;
            while (finish > start && char.IsWhiteSpace(Text[finish - 1])) finish--;
            return (start, finish - start);
        }
    }
}