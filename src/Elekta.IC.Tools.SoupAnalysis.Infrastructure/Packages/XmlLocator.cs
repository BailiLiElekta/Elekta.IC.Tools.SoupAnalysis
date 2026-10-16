using System.Xml;
using System.Xml.Linq;

namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure.Packages;

public static class XmlLocator
{
    public static string GetPath(XObject node)
    {
        if (node is XAttribute attribute) return GetPath(attribute.Parent ?? throw new ArgumentException("Detached XML attribute.")) + "/@" + attribute.Name.LocalName;
        if (node is not XElement element) throw new ArgumentException("An XML element or attribute is required.", nameof(node));
        var index = element.ElementsBeforeSelf().Count(sibling => sibling.Name.LocalName == element.Name.LocalName) + 1;
        return (element.Parent is null ? "" : GetPath(element.Parent)) + "/" + element.Name.LocalName + "[" + index + "]";
    }

    public static XObject? Find(XDocument document, string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/')) return null;
        XElement? current = null;
        var segments = path[1..].Split('/');
        for (var offset = 0; offset < segments.Length; offset++)
        {
            var segment = segments[offset];
            if (segment.StartsWith('@'))
                return offset == segments.Length - 1 ? current?.Attributes().SingleOrDefault(attribute => attribute.Name.LocalName == segment[1..]) : null;
            var bracket = segment.LastIndexOf('[');
            if (bracket <= 0 || !segment.EndsWith(']') || !int.TryParse(segment.AsSpan(bracket + 1, segment.Length - bracket - 2), out var index) || index < 1) return null;
            var name = segment[..bracket];
            var children = offset == 0 ? document.Elements() : current?.Elements() ?? [];
            current = children.Where(element => element.Name.LocalName == name).Skip(index - 1).FirstOrDefault();
            if (current is null) return null;
        }
        return current;
    }

    public static Elekta.IC.Tools.SoupAnalysis.Domain.XmlOwner CreateOwner(string sourceFile, XObject node) =>
        new(sourceFile, GetPath(node), node is IXmlLineInfo line && line.HasLineInfo() ? line.LineNumber : null);
}