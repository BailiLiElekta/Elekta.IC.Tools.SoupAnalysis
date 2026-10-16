using System.Collections.Immutable;
using System.IO.Enumeration;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Domain;
using Elekta.IC.Tools.SoupAnalysis.Infrastructure.AzureDevOps;

namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure.Packages;

public sealed class PackageReferenceScanner : IPackageScanner
{
    public PackageScanResult Scan(SourceSnapshot snapshot, AnalysisRules rules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var issues = new List<InventoryIssue>();
        var usages = new List<PackageUsage>();
        var files = new Dictionary<string, SnapshotFile>(StringComparer.OrdinalIgnoreCase);
        if (snapshot.Files.IsDefault || rules.TestProjectPatterns.IsDefaultOrEmpty)
            return new([], [new("InvalidSnapshot", "Initialized snapshot files and test naming rules are required.")]);
        foreach (var file in snapshot.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var path = AzureReposClient.SafePath(file.Path);
                if (AzureReposClient.IsExcluded(path)) continue;
                if (file.Content.IsDefault || !files.TryAdd(path, file with { Path = path }))
                    issues.Add(new("DuplicateFile", "Duplicate or unreadable snapshot file.", path));
            }
            catch (InvalidDataException) { issues.Add(new("UnsafePath", "Unsafe snapshot path.", file.Path)); }
        }
        foreach (var path in files.Keys.Where(AzureReposClient.IsSourceProject).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evaluation = new ProjectEvaluation(snapshot.Source, path, files, rules, issues, cancellationToken);
            usages.AddRange(evaluation.Evaluate());
        }
        var sharedOwners = usages.Where(usage => usage.Declaration.PropertyName is not null)
            .GroupBy(usage => (usage.Declaration.VersionOwner.SourceFile, usage.Declaration.VersionOwner.ElementPath))
            .Where(group => group.Select(usage => usage.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(group => group.Key).ToHashSet();
        for (var index = 0; index < usages.Count; index++)
        {
            var usage = usages[index];
            if (sharedOwners.Contains((usage.Declaration.VersionOwner.SourceFile, usage.Declaration.VersionOwner.ElementPath)))
                usages[index] = usage with { Declaration = usage.Declaration with { IsUniquelyOwned = false } };
        }
        return new([.. usages], [.. issues.Distinct()]);
    }

    private sealed class ProjectEvaluation(RepositorySource source, string project, Dictionary<string, SnapshotFile> files,
        AnalysisRules rules, List<InventoryIssue> issues, CancellationToken token)
    {
        private readonly Dictionary<string, List<Value>> properties = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Pending> references = [];
        private readonly List<Pending> central = [];
        private readonly HashSet<string> active = new(StringComparer.OrdinalIgnoreCase);
        private string? centralFile;
        private static readonly Regex PropertyPattern = new(@"\$\(([A-Za-z_][A-Za-z0-9_.-]*)\)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex Comparison = new(@"^\s*(['""].*?['""])\s*(==|!=)\s*(['""].*?['""])\s*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        internal IEnumerable<PackageUsage> Evaluate()
        {
            var build = Nearest("Directory.Build.props");
            if (build is not null) WalkFile(build, null, false);
            centralFile = Nearest("Directory.Packages.props");
            if (centralFile is not null) WalkFile(centralFile, null, false);
            WalkFile(project, null, false);
            var targets = Nearest("Directory.Build.targets");
            if (targets is not null) WalkFile(targets, null, false);
            var centralDeclarations = Materialize(central);
            var directDeclarations = Materialize(references);
            string? assemblyName = null;
            var assemblyUnknown = false;
            if (properties.TryGetValue("AssemblyName", out var assemblyValues))
            {
                if (assemblyValues.Count == 1 && !assemblyValues[0].Uncertain && !IsDynamic(assemblyValues[0].Text)) assemblyName = assemblyValues[0].Text;
                else { assemblyUnknown = true; Issue("AssemblyNameUnresolved", "AssemblyName is not statically known; test naming remains uncertain.", project); }
            }
            var classification = TestOnlyClassifier.IsTestProject(project, assemblyName, rules) ? TestClassification.Test
                : assemblyUnknown ? TestClassification.Unknown : TestClassification.Production;
            var managed = BooleanProperty("ManagePackageVersionsCentrally", false);
            var results = new List<PackageUsage>();
            foreach (var pair in directDeclarations)
            {
                token.ThrowIfCancellationRequested();
                foreach (var declaration in pair.Value)
                {
                    var chosen = declaration.Version;
                    var overrideValues = VersionCandidates(declaration.OverrideVersion ?? chosen, "VersionOverride");
                    var inlineValues = VersionCandidates(chosen, "Version");
                    var candidates = new List<(Pending Pending, XObject? Node, string? CentralFile, bool Override)>();
                    if (overrideValues.Count > 0 || inlineValues.Count > 0)
                    {
                        candidates.AddRange(overrideValues.Select(value => (value.Pending, (XObject?)value.Node, (string?)null, true)));
                        if (overrideValues.Count == 0 || overrideValues.Any(value => value.Pending.Uncertain))
                            candidates.AddRange(inlineValues.Select(value => (value.Pending, (XObject?)value.Node, (string?)null, false)));
                        if (overrideValues.Count > 0 && BooleanProperty("CentralPackageVersionOverrideEnabled", true) == false)
                            Issue("OverrideDisabled", "VersionOverride is disabled by repository policy.", chosen.File);
                        if (overrideValues.Count == 0 && inlineValues.Count > 0 && managed == true)
                            Issue("InlineCentralConflict", "Inline Version with active central package management is not a valid authoritative declaration.", chosen.File);
                    }
                    else if (managed == true && centralDeclarations.TryGetValue(pair.Key, out var definitions))
                    {
                        foreach (var definition in definitions)
                            foreach (var version in VersionCandidates(definition.Version with { Uncertain = definition.Uncertain }, "Version"))
                                candidates.Add((version.Pending, version.Node, centralFile, false));
                    }
                    if (candidates.Count == 0)
                    {
                        Issue("VersionUnresolved", "No deterministic inline/override or active central version for " + pair.Key + ".", declaration.Reference.File);
                        candidates.Add((chosen, null, null, false));
                    }
                    foreach (var candidate in candidates)
                    {
                        var owner = candidate.Node is null ? XmlLocator.CreateOwner(candidate.Pending.File, candidate.Pending.Element)
                            : XmlLocator.CreateOwner(candidate.Pending.File, candidate.Node);
                        var expression = candidate.Node is XAttribute attribute ? attribute.Value : candidate.Node is XElement child ? child.Value : "";
                        var values = Expand(expression, candidate.Pending.File, owner);
                        foreach (var value in values)
                        {
                            var uncertain = declaration.Uncertain || candidate.Pending.Uncertain || value.Uncertain || candidates.Count > 1 || values.Count > 1;
                            var condition = Combine(declaration.Reference.ImportCondition, Combine(declaration.Condition, Combine(candidate.Pending.Condition, value.Condition)));
                            var exact = PackageVersions.ParseExact(value.Text) is not null;
                            if (!exact) Issue("VersionUnresolved", "Package " + pair.Key + " has a dynamic, range, floating or missing version.", candidate.Pending.File);
                            if (uncertain) Issue("ConditionalDeclaration", "Package " + pair.Key + " has indeterminate presence or version alternatives.", declaration.Reference.File);
                            var kind = !exact ? VersionDeclarationKind.Unresolved : candidate.Override ? VersionDeclarationKind.VersionOverride
                                : value.PropertyName is not null ? VersionDeclarationKind.Property : candidate.CentralFile is not null ? VersionDeclarationKind.Central
                                : candidate.Pending.File != project ? VersionDeclarationKind.Inherited : VersionDeclarationKind.Inline;
                            var provenance = new DeclarationProvenance(XmlLocator.CreateOwner(declaration.Reference.File, declaration.Reference.Element), value.Owner, kind,
                                value.PropertyName, candidate.CentralFile, exact && !uncertain && !value.Composite && !declaration.MultipleIds);
                            results.Add(new(source, project, pair.Key, value.Text, classification, provenance, condition, assemblyName));
                        }
                    }
                }
            }
            return results;
        }

        private bool? BooleanProperty(string name, bool fallback)
        {
            if (!properties.TryGetValue(name, out var values)) return fallback;
            if (values.Count == 1 && !values[0].Uncertain && bool.TryParse(values[0].Text, out var boolean)) return boolean;
            Issue("PropertyUnresolved", name + " has no deterministic Boolean value.", project);
            return null;
        }

        private string? Nearest(string name)
        {
            var directory = DirectoryOf(project);
            while (true)
            {
                var path = directory + name;
                if (files.ContainsKey(path)) return path;
                if (directory.Length == 0) return null;
                directory = DirectoryOf(directory.TrimEnd('/'));
            }
        }

        private void WalkFile(string path, string? condition, bool uncertain, string? importCondition = null)
        {
            token.ThrowIfCancellationRequested();
            if (!active.Add(path)) { Issue("ImportCycle", "Repository imports contain a cycle.", path); return; }
            try
            {
                if (!files.TryGetValue(path, out var file)) { Issue("ImportMissing", "Repository import could not be read: " + path, path); return; }
                using var stream = new MemoryStream(file.Content.ToArray());
                using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
                var document = XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
                if (document.Root?.Name.LocalName != "Project") { Issue("InvalidXml", "Expected an MSBuild Project root.", path); return; }
                foreach (var element in document.Root.Elements()) Walk(element, path, condition, uncertain, importCondition);
            }
            catch (XmlException) { Issue("InvalidXml", "Malformed XML or prohibited DTD in repository source.", path); }
            finally { active.Remove(path); }
        }

        private void Walk(XElement element, string file, string? inheritedCondition, bool uncertain, string? importCondition = null)
        {
            token.ThrowIfCancellationRequested();
            var condition = Combine(inheritedCondition, (string?)element.Attribute("Condition"));
            switch (element.Name.LocalName)
            {
                case "PropertyGroup":
                    foreach (var property in element.Elements())
                    {
                        token.ThrowIfCancellationRequested();
                        var propertyCondition = Combine(condition, (string?)property.Attribute("Condition"));
                        var truth = EvaluateCondition(propertyCondition, file);
                        if (truth == false) continue;
                        var values = Expand(property.Value, file, XmlLocator.CreateOwner(file, property));
                        values = values.Select(value => value with
                        {
                            PropertyName = value.PropertyName ?? property.Name.LocalName,
                            Condition = Combine(importCondition, Combine(value.Condition, propertyCondition)),
                            Uncertain = value.Uncertain || uncertain || truth is null || property.HasElements
                        }).ToList();
                        if (truth is null || uncertain)
                        {
                            if (!properties.TryGetValue(property.Name.LocalName, out var previous))
                                previous = [new("$(" + property.Name.LocalName + ")", XmlLocator.CreateOwner(file, property), property.Name.LocalName, null, true)];
                            values.InsertRange(0, previous.Select(value => value with { Uncertain = true, Condition = Combine(value.Condition, "Not(" + propertyCondition + ")") }));
                        }
                        if (values.Count > 32)
                        {
                            Issue("AlternativeLimit", "Property evaluation exceeded the safe alternative limit.", file);
                            values = [new(property.Value, XmlLocator.CreateOwner(file, property), property.Name.LocalName, propertyCondition, true)];
                        }
                        properties[property.Name.LocalName] = values;
                    }
                    break;
                case "ItemGroup":
                    foreach (var item in element.Elements())
                    {
                        if (item.Name.LocalName is not ("PackageReference" or "PackageVersion")) continue;
                        var pending = new Pending(item, file, Combine(condition, (string?)item.Attribute("Condition")), uncertain, importCondition);
                        (item.Name.LocalName == "PackageReference" ? references : central).Add(pending);
                    }
                    break;
                case "Import":
                    var truthImport = EvaluateCondition(condition, file);
                    if (truthImport == false) break;
                    if ((string?)element.Attribute("Sdk") == "Microsoft.NET.Sdk" && (string?)element.Attribute("Project") is "Sdk.props" or "Sdk.targets") break;
                    if (truthImport is null) Issue("ImportCondition", "Import presence depends on an unresolved condition.", file);
                    var importOwner = XmlLocator.CreateOwner(file, element);
                    var imports = Expand((string?)element.Attribute("Project") ?? "", file, importOwner);
                    foreach (var import in imports)
                    {
                        if (import.Uncertain || IsDynamic(import.Text)) { Issue("ImportUnresolved", "Import path cannot be determined from repository properties.", file); continue; }
                        foreach (var part in import.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var path = ResolvePath(file, part);
                            if (path is null || AzureReposClient.IsExcluded(path)) { Issue("ImportUnsafe", "Import escapes the repository or targets excluded/generated sources.", file); continue; }
                            var matches = path.Contains('*') || path.Contains('?')
                                ? files.Keys.Where(candidate => FileSystemName.MatchesSimpleExpression(path, candidate, true)).Order(StringComparer.Ordinal).ToArray()
                                : [path];
                            if (matches.Length == 0) { Issue("ImportMissing", "Import pattern has no readable repository-owned matches.", file); continue; }
                            foreach (var match in matches) WalkFile(match, null, uncertain || truthImport is null, Combine(importCondition, condition));
                        }
                    }
                    break;
                case "ImportGroup":
                    foreach (var child in element.Elements()) Walk(child, file, condition, uncertain, importCondition);
                    break;
                case "Choose":
                    var chosen = false;
                    var unknown = false;
                    foreach (var branch in element.Elements())
                    {
                        var branchCondition = (string?)branch.Attribute("Condition");
                        var truthBranch = branch.Name.LocalName == "Otherwise" ? (unknown ? (bool?)null : !chosen) : EvaluateCondition(branchCondition, file);
                        if (truthBranch == false || chosen && !unknown) continue;
                        unknown |= truthBranch is null;
                        foreach (var child in branch.Elements()) Walk(child, file, Combine(condition, branchCondition), uncertain || unknown, importCondition);
                        chosen |= truthBranch == true;
                    }
                    break;
                case "Target":
                    foreach (var child in element.Elements()) Walk(child, file, Combine(condition, "RuntimeTarget(" + (string?)element.Attribute("Name") + ")"), true, importCondition);
                    break;
            }
        }

        private Dictionary<string, List<Declaration>> Materialize(List<Pending> items)
        {
            var result = new Dictionary<string, List<Declaration>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pending in items)
            {
                token.ThrowIfCancellationRequested();
                var truth = EvaluateCondition(pending.Condition, pending.File);
                if (truth == false) continue;
                var include = (string?)pending.Element.Attribute("Include");
                var update = (string?)pending.Element.Attribute("Update");
                var remove = (string?)pending.Element.Attribute("Remove");
                var identity = include ?? update ?? remove;
                if (identity is null) { Issue("ReferenceIdentity", "Package declaration has no Include, Update or Remove identity.", pending.File); continue; }
                var ids = Expand(identity, pending.File, XmlLocator.CreateOwner(pending.File, pending.Element));
                foreach (var value in ids)
                {
                    if (IsDynamic(value.Text) || value.Uncertain) { Issue("ReferenceIdentity", "Package identity cannot be determined statically.", pending.File); continue; }
                    var names = value.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var name in names)
                    {
                        if (include is not null && (name.Contains('*') || name.Contains('?'))) { Issue("ReferenceIdentity", "Wildcard package Include is unsupported.", pending.File); continue; }
                        var keys = include is not null ? [name] : result.Keys.Where(key => FileSystemName.MatchesSimpleExpression(name, key, true)).ToArray();
                        if (update is not null && keys.Length == 0) Issue("UpdateUnresolved", "Package Update has no known declared Include; implicit SDK references are not assumed.", pending.File);
                        foreach (var key in keys)
                        {
                            var uncertain = pending.Uncertain || truth is null;
                            if (include is not null)
                            {
                                if (!result.TryGetValue(key, out var existing)) result[key] = existing = [];
                                if (existing.Count > 0) Issue("DuplicateReference", "Multiple Include declarations or alternatives exist for " + key + ".", pending.File);
                                existing.Add(new(pending, pending, pending.Condition, uncertain || existing.Count > 0, names.Length > 1, pending));
                            }
                            else if (remove is not null)
                            {
                                if (!uncertain) result.Remove(key);
                                else
                                {
                                    Issue("ConditionalDeclaration", "Conditional Remove leaves package presence unresolved.", pending.File);
                                    result[key] = result[key].Select(item => item with { Uncertain = true, Condition = Combine(item.Condition, pending.Condition) }).ToList();
                                }
                            }
                            else
                            {
                                var previous = result[key];
                                var changesOverride = VersionCandidates(pending, "VersionOverride").Count > 0;
                                var changesVersion = VersionCandidates(pending, "Version").Count > 0;
                                var updated = previous.Select(item => item with
                                {
                                    Version = changesVersion ? pending : item.Version,
                                    OverrideVersion = changesOverride ? pending : item.OverrideVersion,
                                    Condition = Combine(item.Condition, pending.Condition),
                                    Uncertain = item.Uncertain || uncertain
                                }).ToList();
                                result[key] = uncertain ? [.. previous.Select(item => item with { Uncertain = true }), .. updated] : updated;
                            }
                        }
                    }
                }
            }
            return result;
        }

        private List<Value> Expand(string text, string file, XmlOwner owner)
        {
            token.ThrowIfCancellationRequested();
            var matches = PropertyPattern.Matches(text);
            var result = new List<Value> { new(text, owner, null, null, false) };
            foreach (Match match in matches)
            {
                var name = match.Groups[1].Value;
                List<Value> replacements;
                var builtIn = name.ToLowerInvariant() switch
                {
                    "msbuildthisfiledirectory" => "__soup_repository_root__/" + DirectoryOf(file),
                    "msbuildprojectdirectory" => "__soup_repository_root__/" + DirectoryOf(project).TrimEnd('/'),
                    "msbuildprojectname" => Path.GetFileNameWithoutExtension(project),
                    "msbuildthisfilefullpath" => "__soup_repository_root__/" + file,
                    _ => null
                };
                if (builtIn is not null) replacements = [new(builtIn, owner, null, null, false)];
                else if (properties.TryGetValue(name, out var values)) replacements = values;
                else replacements = [new(match.Value, owner, name, null, true)];
                var exactProperty = matches.Count == 1 && match.Value == text;
                result = result.SelectMany(previous => replacements.Select(value => new Value(
                    previous.Text.Replace(match.Value, value.Text, StringComparison.Ordinal), exactProperty ? value.Owner : owner,
                    exactProperty ? value.PropertyName : previous.PropertyName, Combine(previous.Condition, value.Condition),
                    previous.Uncertain || value.Uncertain, !exactProperty || value.Composite))).Take(33).ToList();
                if (result.Count > 32) return [new(text, owner, null, null, true, true)];
            }
            return result.Select(value => value with { Uncertain = value.Uncertain || IsDynamic(value.Text) }).ToList();
        }

        private bool? EvaluateCondition(string? condition, string file)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(condition)) return true;
            var text = condition.Trim();
            if (text.StartsWith('(') && text.EndsWith(')') && Encloses(text)) return EvaluateCondition(text[1..^1], file);
            foreach (var connective in new[] { " Or ", " And " })
            {
                var position = FindConnective(text, connective);
                if (position < 0) continue;
                var left = EvaluateCondition(text[..position], file);
                var right = EvaluateCondition(text[(position + connective.Length)..], file);
                if (connective == " And ") return left == false || right == false ? false : left == true && right == true ? true : null;
                return left == true || right == true ? true : left == false && right == false ? false : null;
            }
            if (text.StartsWith('!')) { var negated = EvaluateCondition(text[1..], file); return negated.HasValue ? !negated.Value : null; }
            if (text.StartsWith("Not(", StringComparison.OrdinalIgnoreCase) && text.EndsWith(')'))
            { var negated = EvaluateCondition(text[4..^1], file); return negated.HasValue ? !negated.Value : null; }
            var expansions = Expand(text, file, new(file, "/Project[1]"));
            if (expansions.Count != 1 || expansions[0].Uncertain) return null;
            text = expansions[0].Text.Trim();
            if (bool.TryParse(text.Trim('\'', '"'), out var boolean)) return boolean;
            if (text.StartsWith("Exists(", StringComparison.OrdinalIgnoreCase) && text.EndsWith(')'))
            {
                var path = ResolvePath(file, text[7..^1].Trim().Trim('\'', '"'));
                return path is not null && files.ContainsKey(path) ? true : null;
            }
            var compare = Comparison.Match(text);
            if (!compare.Success) return null;
            var equal = string.Equals(compare.Groups[1].Value[1..^1], compare.Groups[3].Value[1..^1], StringComparison.OrdinalIgnoreCase);
            return compare.Groups[2].Value == "==" ? equal : !equal;
        }

        private static int FindConnective(string text, string connective)
        {
            var depth = 0;
            var quote = '\0';
            for (var index = 0; index <= text.Length - connective.Length; index++)
            {
                var character = text[index];
                if (quote != '\0') { if (character == quote) quote = '\0'; continue; }
                if (character is '\'' or '"') { quote = character; continue; }
                if (character == '(') depth++;
                if (character == ')') depth--;
                if (depth == 0 && text.AsSpan(index, connective.Length).Equals(connective.AsSpan(), StringComparison.OrdinalIgnoreCase)) return index;
            }
            return -1;
        }

        private static bool Encloses(string text)
        {
            var depth = 0;
            var quote = '\0';
            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                if (quote != '\0') { if (character == quote) quote = '\0'; continue; }
                if (character is '\'' or '"') { quote = character; continue; }
                if (character == '(') depth++;
                if (character == ')' && --depth == 0 && index != text.Length - 1) return false;
            }
            return depth == 0;
        }

        private static string? ResolvePath(string file, string value)
        {
            value = value.Replace('\\', '/');
            if (value.Contains(':') || value.StartsWith('/') || IsDynamic(value)) return null;
            const string repositoryRoot = "__soup_repository_root__/";
            var combined = value.StartsWith(repositoryRoot, StringComparison.Ordinal) ? value[repositoryRoot.Length..] : DirectoryOf(file) + value;
            var segments = new List<string>();
            foreach (var segment in combined.Split('/'))
            {
                if (segment is "" or ".") continue;
                if (segment == "..") { if (segments.Count == 0) return null; segments.RemoveAt(segments.Count - 1); }
                else if (segment.Any(char.IsControl)) return null;
                else segments.Add(segment);
            }
            return segments.Count == 0 ? null : string.Join('/', segments);
        }

        private List<(Pending Pending, XObject Node)> VersionCandidates(Pending pending, string name)
        {
            var nodes = pending.Element.Attributes().Where(attribute => attribute.Name.LocalName == name).Cast<XObject>()
                .Concat(pending.Element.Elements().Where(child => child.Name.LocalName == name));
            var candidates = new List<(Pending, XObject)>();
            foreach (var node in nodes)
            {
                var condition = node is XElement child ? Combine(pending.Condition, (string?)child.Attribute("Condition")) : pending.Condition;
                var truth = EvaluateCondition(condition, pending.File);
                if (truth == false) continue;
                candidates.Add((pending with { Condition = condition, Uncertain = pending.Uncertain || truth is null }, node));
            }
            return candidates;
        }

        private static bool IsDynamic(string text) => text.Contains("$(", StringComparison.Ordinal) || text.Contains("@(", StringComparison.Ordinal) || text.Contains("%(", StringComparison.Ordinal);
        private static string DirectoryOf(string path) => path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";
        private static string? Combine(string? left, string? right) => string.IsNullOrWhiteSpace(left) ? right
            : string.IsNullOrWhiteSpace(right) || left == right ? left : "(" + left + ") And (" + right + ")";
        private void Issue(string code, string message, string file) => issues.Add(new(code, message, file));
        private sealed record Value(string Text, XmlOwner Owner, string? PropertyName, string? Condition, bool Uncertain, bool Composite = false);
        private sealed record Pending(XElement Element, string File, string? Condition, bool Uncertain, string? ImportCondition = null);
        private sealed record Declaration(Pending Reference, Pending Version, string? Condition, bool Uncertain, bool MultipleIds, Pending? OverrideVersion = null);
    }
}