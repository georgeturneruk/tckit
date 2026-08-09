using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TcKit.Core.Models;

namespace TcKit.Adapters.Xml;

/// <summary>
/// Metadata extractors for TwinCAT project files (.TcPOU / .TcGVL / .TcDUT / .TcTTO /
/// .tsproj / .plcproj). Ported from the Python <c>tckit/utils/tc_file_parser.py</c>;
/// only the fields <c>get_structure</c> needs are extracted (names, types, tasks,
/// libraries), not declarations or bodies. Stdlib XML only.
/// </summary>
internal static class TcFileParser
{
    internal sealed record PouMeta(string Name, PouType Type);

    internal sealed record DutMeta(string Name, DutKind Kind);

    internal sealed record LibRaw(string Name, string Version, string? Placeholder);

    internal sealed record TaskRaw(string Name, int? CycleTimeUs, int? Priority, IReadOnlyList<string> Programs);

    internal sealed record AccessorPart(string Declaration, string Body);

    internal sealed record MemberPart(string Name, string Declaration, string Body);

    internal sealed record PropertyPart(string Name, string Declaration, AccessorPart? Get, AccessorPart? Set);

    internal sealed record PouFull(
        string Name, PouType Type, string Declaration, string Body,
        IReadOnlyList<MemberPart> Methods, IReadOnlyList<MemberPart> Actions, IReadOnlyList<PropertyPart> Properties);

    internal sealed record GvlFull(string Name, string Declaration);

    internal sealed record DutFull(string Name, string Declaration, DutKind Kind, string BaseType);

    /// <summary>Parse a .TcPOU for its name and POU type. Handles &lt;POU&gt; and &lt;Itf&gt; roots.</summary>
    internal static PouMeta ParsePou(string path)
    {
        var root = Load(path);
        var container = Child(root, "POU") ?? Child(root, "Itf")
            ?? throw new InvalidDataException($"No <POU> or <Itf> element found in {path}");
        var name = container.Attribute("Name")?.Value ?? "";
        var declaration = Declaration(container);
        return new PouMeta(name, DetectPouType(declaration, container.Name.LocalName));
    }

    /// <summary>Parse a .TcGVL for its name.</summary>
    internal static string ParseGvl(string path)
    {
        var root = Load(path);
        var gvl = Child(root, "GVL")
            ?? throw new InvalidDataException($"No <GVL> element found in {path}");
        return gvl.Attribute("Name")?.Value ?? "";
    }

    /// <summary>Parse a .TcPOU in full: declaration, body, methods, actions, and properties (with accessors).</summary>
    internal static PouFull ParsePouFull(string path)
    {
        var root = Load(path);
        var container = Child(root, "POU") ?? Child(root, "Itf")
            ?? throw new InvalidDataException($"No <POU> or <Itf> element found in {path}");
        var name = container.Attribute("Name")?.Value ?? "";
        var declaration = Declaration(container);
        var type = DetectPouType(declaration, container.Name.LocalName);

        var methods = container.Elements().Where(e => e.Name.LocalName == "Method")
            .Select(m => new MemberPart(m.Attribute("Name")?.Value ?? "", Declaration(m), StBody(m)))
            .ToList();
        var actions = container.Elements().Where(e => e.Name.LocalName == "Action")
            .Select(a => new MemberPart(a.Attribute("Name")?.Value ?? "", Declaration(a), StBody(a)))
            .ToList();
        var properties = container.Elements().Where(e => e.Name.LocalName == "Property")
            .Select(p =>
            {
                var get = Child(p, "Get");
                var set = Child(p, "Set");
                return new PropertyPart(
                    p.Attribute("Name")?.Value ?? "",
                    Declaration(p),
                    get is not null ? new AccessorPart(Declaration(get), StBody(get)) : null,
                    set is not null ? new AccessorPart(Declaration(set), StBody(set)) : null);
            })
            .ToList();

        return new PouFull(name, type, declaration, StBody(container), methods, actions, properties);
    }

    /// <summary>Parse a .TcGVL for its name and declaration block.</summary>
    internal static GvlFull ParseGvlFull(string path)
    {
        var root = Load(path);
        var gvl = Child(root, "GVL")
            ?? throw new InvalidDataException($"No <GVL> element found in {path}");
        return new GvlFull(gvl.Attribute("Name")?.Value ?? "", Declaration(gvl));
    }

    /// <summary>Parse a .TcDUT for its name, declaration, kind, and (for aliases) base type.</summary>
    internal static DutFull ParseDutFull(string path)
    {
        var root = Load(path);
        var dut = Child(root, "DUT")
            ?? throw new InvalidDataException($"No <DUT> element found in {path}");
        var declaration = Declaration(dut);
        var (kind, baseType) = ClassifyDutFull(declaration);
        return new DutFull(dut.Attribute("Name")?.Value ?? "", declaration, kind, baseType);
    }

    /// <summary>Parse a .TcDUT for its name and kind discriminator.</summary>
    internal static DutMeta ParseDut(string path)
    {
        var root = Load(path);
        var dut = Child(root, "DUT")
            ?? throw new InvalidDataException($"No <DUT> element found in {path}");
        var name = dut.Attribute("Name")?.Value ?? "";
        return new DutMeta(name, ClassifyDut(Declaration(dut)));
    }

    /// <summary>Parse a .plcproj for its library references.</summary>
    internal static List<LibRaw> ParsePlcproj(string path)
    {
        var root = Load(path);
        var libraries = new List<LibRaw>();
        foreach (var itemGroup in root.Elements().Where(e => e.Name.LocalName == "ItemGroup"))
        {
            foreach (var reference in itemGroup.Elements())
            {
                var tag = reference.Name.LocalName;
                var include = (reference.Attribute("Include")?.Value ?? "").Trim();
                if (tag == "PlaceholderReference")
                {
                    var resolution = ChildText(reference, "DefaultResolution");
                    var (version, resolvedName) = SplitResolution(resolution, include);
                    libraries.Add(new LibRaw(
                        string.IsNullOrEmpty(resolvedName) ? include : resolvedName,
                        version,
                        string.IsNullOrEmpty(include) ? null : include));
                }
                else if (tag == "LibraryReference")
                {
                    var parts = include.Split(',').Select(p => p.Trim()).ToArray();
                    var name = parts.Length > 0 ? parts[0] : include;
                    var version = parts.Length > 1 ? parts[1] : "";
                    libraries.Add(new LibRaw(name, version, null));
                }
            }
        }

        return libraries;
    }

    /// <summary>
    /// Return the project paths referenced by a .sln, as written (usually relative to the
    /// solution directory). Solution-folder entries carry the folder name in the path slot;
    /// callers filter those out with a File.Exists check on the resolved path.
    /// </summary>
    internal static List<string> ParseSlnProjectPaths(string path)
    {
        var result = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            var match = s_slnProject.Match(line);
            if (match.Success)
            {
                // .sln files always store Windows-style backslash paths; normalise so the
                // resolution also works when the reader runs on a non-Windows host (CI).
                result.Add(match.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar));
            }
        }

        return result;
    }

    private static readonly Regex s_slnProject = new(
        @"^Project\(""\{[^}]*\}""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+)""",
        RegexOptions.Compiled);

    /// <summary>
    /// Parse a .tsproj for System Manager task definitions. CycleTime is in 100 ns ticks;
    /// converted to microseconds for consistency with .TcTTO. Programs are never bound here.
    /// </summary>
    internal static List<TaskRaw> ParseTsproj(string path)
    {
        var root = Load(path);
        var tasks = new List<TaskRaw>();
        foreach (var task in root.Descendants().Where(e => e.Name.LocalName == "Task"))
        {
            // System-manager tasks carry numeric Id + CycleTime attributes; this filters
            // out unrelated <Task> nodes that may appear elsewhere in the file.
            if (task.Attribute("Id") is null || task.Attribute("CycleTime") is null)
            {
                continue;
            }

            var name = ChildText(task, "Name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var cycleTicks = ToInt(task.Attribute("CycleTime")?.Value);
            var cycleUs = cycleTicks is not null ? cycleTicks / 10 : null;
            tasks.Add(new TaskRaw(name, cycleUs, ToInt(task.Attribute("Priority")?.Value), []));
        }

        return tasks;
    }

    /// <summary>
    /// Parse a .TcTTO PLC task object: the authoritative source for cycle time (already in
    /// microseconds), priority, and the POU bound to the task via &lt;PouCall&gt;&lt;Name&gt;.
    /// </summary>
    internal static TaskRaw ParseTctto(string path)
    {
        var root = Load(path);
        var task = Child(root, "Task")
            ?? throw new InvalidDataException($"No <Task> element found in {path}");
        var name = task.Attribute("Name")?.Value ?? "";
        var cycleUs = ToInt(ChildText(task, "CycleTime"));
        var priority = ToInt(ChildText(task, "Priority"));

        var programs = new List<string>();
        foreach (var pouCall in task.Elements().Where(e => e.Name.LocalName == "PouCall"))
        {
            var pouName = ChildText(pouCall, "Name");
            if (!string.IsNullOrEmpty(pouName))
            {
                programs.Add(pouName);
            }
        }

        return new TaskRaw(name, cycleUs, priority, programs);
    }

    /// <summary>
    /// Detect the POU type from the element tag and declaration text. &lt;Itf&gt; is always
    /// an interface; otherwise the first keyword in the declaration wins. FUNCTION_BLOCK must
    /// be tested before FUNCTION because it contains it.
    /// </summary>
    internal static PouType DetectPouType(string declaration, string elementTag)
    {
        if (string.Equals(elementTag, "Itf", StringComparison.OrdinalIgnoreCase))
        {
            return PouType.Interface;
        }

        var text = declaration.ToUpperInvariant();
        if (text.Contains("FUNCTION_BLOCK", StringComparison.Ordinal))
        {
            return PouType.FunctionBlock;
        }

        if (text.Contains("FUNCTION", StringComparison.Ordinal))
        {
            return PouType.Function;
        }

        if (text.Contains("PROGRAM", StringComparison.Ordinal))
        {
            return PouType.Program;
        }

        if (text.Contains("INTERFACE", StringComparison.Ordinal))
        {
            return PouType.Interface;
        }

        return PouType.FunctionBlock;
    }

    private static readonly Regex s_blockComment = new(@"\(\*[\s\S]*?\*\)", RegexOptions.Compiled);
    private static readonly Regex s_lineComment = new(@"//[^\r\n]*", RegexOptions.Compiled);
    private static readonly Regex s_pragma = new(@"\{[^}]*\}", RegexOptions.Compiled);
    private static readonly Regex s_typeBody = new(
        @"TYPE\s+[A-Za-z_][A-Za-z0-9_]*\s*(?:EXTENDS\s+[A-Za-z_][A-Za-z0-9_.]*\s*)?:\s*(.+?)(?:END_TYPE|$)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Discriminate a DUT declaration into its kind (base type discarded).</summary>
    internal static DutKind ClassifyDut(string declaration) => ClassifyDutFull(declaration).Kind;

    /// <summary>
    /// Discriminate a DUT declaration into (kind, baseType). Comments and pragmas are stripped
    /// first so annotated DUTs classify correctly. baseType is the aliased type for ALIAS DUTs and
    /// empty otherwise. Falls back to STRUCT when unparseable.
    /// </summary>
    internal static (DutKind Kind, string BaseType) ClassifyDutFull(string declaration)
    {
        var stripped = s_pragma.Replace(s_lineComment.Replace(s_blockComment.Replace(declaration, " "), " "), " ");
        var match = s_typeBody.Match(stripped);
        if (!match.Success)
        {
            return (DutKind.Struct, "");
        }

        var body = match.Groups[1].Value.Trim();
        var upper = body.ToUpperInvariant();
        if (upper.StartsWith("STRUCT", StringComparison.Ordinal))
        {
            return (DutKind.Struct, "");
        }

        if (upper.StartsWith("UNION", StringComparison.Ordinal))
        {
            return (DutKind.Union, "");
        }

        if (body.StartsWith('('))
        {
            return (DutKind.Enum, "");
        }

        // ALIAS: the body is a type expression terminated by ';'.
        var semi = body.IndexOf(';', StringComparison.Ordinal);
        var baseType = semi >= 0 ? body[..semi].Trim() : body.Trim();
        return (DutKind.Alias, baseType);
    }

    private const string AccessModifiers = @"(?:(?:PUBLIC|PRIVATE|PROTECTED|INTERNAL|FINAL|ABSTRACT)\s+)*";
    private static readonly Regex s_methodReturn = new(
        $@"METHOD\s+{AccessModifiers}\w+\s*:\s*(\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_propertyReturn = new(
        $@"PROPERTY\s+{AccessModifiers}\w+\s*:\s*(\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Implementation-only VAR blocks (locals / VAR CONSTANT / VAR_TEMP). The opener must be the
    // only token on its line so VAR_INPUT/OUTPUT/IN_OUT/INST (the API surface) are preserved.
    private static readonly Regex s_localVarBlock = new(
        @"^[ \t]*(?:VAR(?:[ \t]+CONSTANT)?|VAR_TEMP)[ \t]*\r?\n.*?^[ \t]*END_VAR[ \t]*\r?\n?",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Extract the return type from a METHOD declaration, or "" if absent.</summary>
    internal static string ExtractMethodReturnType(string declaration)
    {
        var match = s_methodReturn.Match(declaration);
        return match.Success ? match.Groups[1].Value : "";
    }

    /// <summary>Extract the return type from a PROPERTY declaration, or "" if absent.</summary>
    internal static string ExtractPropertyReturnType(string declaration)
    {
        var match = s_propertyReturn.Match(declaration);
        return match.Success ? match.Groups[1].Value : "";
    }

    /// <summary>
    /// Strip implementation-only VAR blocks (locals, VAR_TEMP, VAR CONSTANT) from a method
    /// declaration while preserving VAR_INPUT/OUTPUT/IN_OUT/INST. Used for interface signatures;
    /// get_pou_item keeps the full declaration.
    /// </summary>
    internal static string StripMethodLocals(string declaration)
        => s_localVarBlock.Replace(declaration, "").TrimEnd();

    /// <summary>Split a placeholder DefaultResolution like "Name, * (Vendor)" into (version, name).</summary>
    internal static (string Version, string Name) SplitResolution(string resolution, string fallbackName)
    {
        if (string.IsNullOrEmpty(resolution))
        {
            return ("", fallbackName);
        }

        var comma = resolution.IndexOf(',', StringComparison.Ordinal);
        if (comma >= 0)
        {
            var namePart = resolution[..comma];
            var rest = resolution[(comma + 1)..].Trim();
            var paren = rest.IndexOf('(', StringComparison.Ordinal);
            var version = paren >= 0 ? rest[..paren].Trim() : rest;
            return (version, namePart.Trim());
        }

        return ("", resolution.Trim());
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

    private static string ChildText(XElement element, string localName)
        => (Child(element, localName)?.Value ?? "").Trim();

    private static int? ToInt(string? value)
        => int.TryParse(value?.Trim(), out var result) ? result : null;
}
