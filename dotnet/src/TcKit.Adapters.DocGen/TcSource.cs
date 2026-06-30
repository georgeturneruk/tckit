using System.Xml;
using System.Xml.Linq;

namespace TcKit.Adapters.DocGen;

/// <summary>
/// Minimal reader for TwinCAT source files (.TcPOU / .TcGVL / .TcDUT), extracting only the
/// declaration text, member structure, and property accessors the doc model needs. Stdlib XML only.
/// </summary>
/// <remarks>
/// Adapter isolation (the one rule) forbids reusing the Reader adapter's TcFileParser, so the
/// doc-gen lane carries its own slim parse. The POU-type detection mirrors the Python
/// <c>tc_file_parser.detect_pou_type</c> so obj_type values match the reference (function_block /
/// function / program / interface).
/// </remarks>
internal static class TcSource
{
    internal sealed record SourceMember(string Name, string Declaration, string Body);

    internal sealed record SourceProperty(string Name, string Declaration, bool HasGet, bool HasSet);

    internal sealed record SourcePou(
        string Name,
        string PouType,
        string Declaration,
        IReadOnlyList<SourceMember> Methods,
        IReadOnlyList<string> Actions,
        IReadOnlyList<SourceProperty> Properties);

    internal sealed record SourceGvl(string Name, string Declaration);

    internal sealed record SourceDut(string Name, string Declaration);

    internal static SourcePou ParsePou(string path)
    {
        var root = Load(path);
        var container = Child(root, "POU") ?? Child(root, "Itf")
            ?? throw new InvalidDataException($"No <POU> or <Itf> element found in {path}");
        var declaration = Declaration(container);
        var type = DetectPouType(declaration, container.Name.LocalName);

        var methods = container.Elements().Where(e => e.Name.LocalName == "Method")
            .Select(m => new SourceMember(m.Attribute("Name")?.Value ?? "", Declaration(m), StBody(m)))
            .ToList();
        var actions = container.Elements().Where(e => e.Name.LocalName == "Action")
            .Select(a => a.Attribute("Name")?.Value ?? "")
            .ToList();
        var properties = container.Elements().Where(e => e.Name.LocalName == "Property")
            .Select(p => new SourceProperty(
                p.Attribute("Name")?.Value ?? "",
                Declaration(p),
                Child(p, "Get") is not null,
                Child(p, "Set") is not null))
            .ToList();

        return new SourcePou(
            container.Attribute("Name")?.Value ?? "", type, declaration, methods, actions, properties);
    }

    internal static SourceGvl ParseGvl(string path)
    {
        var root = Load(path);
        var gvl = Child(root, "GVL")
            ?? throw new InvalidDataException($"No <GVL> element found in {path}");
        return new SourceGvl(gvl.Attribute("Name")?.Value ?? "", Declaration(gvl));
    }

    internal static SourceDut ParseDut(string path)
    {
        var root = Load(path);
        var dut = Child(root, "DUT")
            ?? throw new InvalidDataException($"No <DUT> element found in {path}");
        return new SourceDut(dut.Attribute("Name")?.Value ?? "", Declaration(dut));
    }

    /// <summary>
    /// Detect the POU type string. &lt;Itf&gt; is always "interface"; otherwise the first keyword in
    /// the declaration wins (FUNCTION_BLOCK before FUNCTION because it contains it), defaulting to
    /// "function_block".
    /// </summary>
    private static string DetectPouType(string declaration, string elementTag)
    {
        if (string.Equals(elementTag, "Itf", StringComparison.OrdinalIgnoreCase))
        {
            return "interface";
        }

        var text = declaration.ToUpperInvariant();
        if (text.Contains("FUNCTION_BLOCK", StringComparison.Ordinal))
        {
            return "function_block";
        }

        if (text.Contains("FUNCTION", StringComparison.Ordinal))
        {
            return "function";
        }

        if (text.Contains("PROGRAM", StringComparison.Ordinal))
        {
            return "program";
        }

        if (text.Contains("INTERFACE", StringComparison.Ordinal))
        {
            return "interface";
        }

        return "function_block";
    }

    private static XElement Load(string path)
    {
        try
        {
            return XDocument.Load(path).Root
                ?? throw new InvalidDataException($"Empty XML document: {path}");
        }
        catch (XmlException exc)
        {
            throw new InvalidDataException($"XML parse error in {path}: {exc.Message}", exc);
        }
    }

    private static XElement? Child(XElement parent, string localName)
        => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string Declaration(XElement element)
        => (Child(element, "Declaration")?.Value ?? "").Trim();

    private static string StBody(XElement element)
    {
        var implementation = Child(element, "Implementation");
        return implementation is null ? "" : (Child(implementation, "ST")?.Value ?? "").Trim();
    }
}
