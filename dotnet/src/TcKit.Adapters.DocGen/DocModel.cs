using System.Text.RegularExpressions;

namespace TcKit.Adapters.DocGen;

// -- Doc model records --------------------------------------------------------

internal sealed record VariableDoc(string Name, string VarType, string Comment = "", string DefaultValue = "");

internal sealed record MethodDoc
{
    public required string Name { get; init; }

    public required string ReturnType { get; init; }

    public required CommentDoc Comment { get; init; }

    public string Visibility { get; init; } = "";

    public bool IsAbstract { get; init; }

    public bool IsFinal { get; init; }

    public IReadOnlyList<VariableDoc> Inputs { get; init; } = [];

    public IReadOnlyList<VariableDoc> Outputs { get; init; } = [];

    public IReadOnlyList<VariableDoc> Inout { get; init; } = [];

    public string Body { get; init; } = "";
}

internal sealed record PropertyDoc
{
    public required string Name { get; init; }

    public required string ReturnType { get; init; }

    public required CommentDoc Comment { get; init; }

    public string Visibility { get; init; } = "";

    public bool HasGet { get; init; } = true;

    public bool HasSet { get; init; }
}

internal sealed record ObjectDoc
{
    public required string Name { get; init; }

    /// <summary>function_block | function | program | interface | gvl | struct | enum.</summary>
    public required string ObjType { get; init; }

    public required string Declaration { get; init; }

    public required CommentDoc Comment { get; init; }

    public string PlcName { get; init; } = "";

    public string Visibility { get; init; } = "";

    public bool IsAbstract { get; init; }

    public bool IsFinal { get; init; }

    public string Extends { get; init; } = "";

    public IReadOnlyList<string> Implements { get; init; } = [];

    public IReadOnlyList<VariableDoc> Inputs { get; init; } = [];

    public IReadOnlyList<VariableDoc> Outputs { get; init; } = [];

    public IReadOnlyList<VariableDoc> Inout { get; init; } = [];

    public IReadOnlyList<VariableDoc> Variables { get; init; } = [];

    public IReadOnlyList<MethodDoc> Methods { get; init; } = [];

    public IReadOnlyList<PropertyDoc> Properties { get; init; } = [];

    public IReadOnlyList<string> Actions { get; init; } = [];

    /// <summary>Names of objects (within the same PLC) that reference this type.</summary>
    public List<string> UsedBy { get; init; } = [];
}

internal sealed record PlcDoc(string Name, string PlcprojPath, IReadOnlyList<ObjectDoc> Objects);

internal sealed record ProjectDoc(string Name, IReadOnlyDictionary<string, PlcDoc> Plcs);

/// <summary>Raised by <see cref="DocModel.BuildProjectDoc"/> when no TwinCAT source files are found.</summary>
internal sealed class NoSourceFilesException(string message) : Exception(message);

/// <summary>
/// Build a structured documentation model from a TwinCAT project. Ported from the Python
/// <c>_doc_model.py</c>: orchestrates <see cref="TcSource"/> (structure) and
/// <see cref="CommentExtractor"/> (comments) into a <see cref="ProjectDoc"/> tree the renderers
/// consume directly.
/// </summary>
internal static class DocModel
{
    // -- Variable block parsing -----------------------------------------------

    private static readonly Regex s_varBlock = new(
        @"VAR(?<kind>_INPUT|_OUTPUT|_IN_OUT|_STAT|_TEMP|_GLOBAL)?\b.*?END_VAR",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Every horizontal-whitespace match uses [ \t] (not \s) to keep parsing line-scoped: otherwise the
    // trailing-comment branch would cross a newline and attribute the next variable's (* ... *) comment
    // to the variable above.
    private static readonly Regex s_varLine = new(
        @"^[ \t]*(?<name>[A-Za-z_]\w*)[ \t]*:[ \t]*(?<type>[^:;=\n]+?)"
        + @"[ \t]*(?::=[ \t]*(?<default>[^;\n]+?))?[ \t]*;"
        + @"(?:[ \t]*(?://[ \t]*(?<comment>.*)|\(\*[ \t]*(?<block_comment>.*?)[ \t]*\*\)))?",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex s_structBlock = new(
        @"\bSTRUCT\b(?<body>.*?)\bEND_STRUCT\b", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_unionBlock = new(
        @"\bUNION\b(?<body>.*?)\bEND_UNION\b", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_enumBlock = new(
        @"TYPE\s+\w+\s*:\s*\((?<body>.*?)\)\s*;?\s*END_TYPE",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_enumMember = new(
        @"^\s*(?<name>[A-Za-z_]\w*)\s*(?::=\s*(?<value>[^,/\n]+?))?\s*(?:,)?\s*(?://\s*(?<comment>.*))?\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly HashSet<string> s_varSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "VAR", "END_VAR", "VAR_GLOBAL", "CONSTANT", "PERSISTENT",
    };

    private static readonly HashSet<string> s_structSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "VAR", "END_VAR", "STRUCT", "END_STRUCT", "UNION", "END_UNION", "CONSTANT", "PERSISTENT",
    };

    internal sealed record VarLists
    {
        public List<VariableDoc> Input { get; } = [];

        public List<VariableDoc> Output { get; } = [];

        public List<VariableDoc> Inout { get; } = [];

        public List<VariableDoc> Variable { get; } = [];
    }

    /// <summary>Extract input / output / inout / variable lists from a declaration block.</summary>
    internal static VarLists ParseVariables(string declaration)
    {
        var result = new VarLists();
        foreach (Match block in s_varBlock.Matches(declaration))
        {
            var kind = (block.Groups["kind"].Value).ToUpperInvariant();
            var target = kind switch
            {
                "_INPUT" => result.Input,
                "_OUTPUT" => result.Output,
                "_IN_OUT" => result.Inout,
                _ => result.Variable,
            };

            foreach (Match line in s_varLine.Matches(block.Value))
            {
                var name = line.Groups["name"].Value;
                if (s_varSkip.Contains(name))
                {
                    continue;
                }

                target.Add(MakeVariable(line));
            }
        }

        return result;
    }

    /// <summary>Extract the field list from a STRUCT or UNION body (TwinCAT structs do not use VAR ... END_VAR).</summary>
    internal static List<VariableDoc> ParseStructFields(string declaration)
    {
        var fields = new List<VariableDoc>();
        foreach (var blockRe in new[] { s_structBlock, s_unionBlock })
        {
            foreach (Match block in blockRe.Matches(declaration))
            {
                foreach (Match line in s_varLine.Matches(block.Groups["body"].Value))
                {
                    var name = line.Groups["name"].Value;
                    if (s_structSkip.Contains(name))
                    {
                        continue;
                    }

                    fields.Add(MakeVariable(line));
                }
            }
        }

        return fields;
    }

    /// <summary>
    /// Extract members from a TwinCAT enum (<c>TYPE E_X : ( Name := value, ... ); END_TYPE</c>). The
    /// literal value is stored in <see cref="VariableDoc.VarType"/> so the var-table renderer is reused
    /// with a relabelled column.
    /// </summary>
    internal static List<VariableDoc> ParseEnumMembers(string declaration)
    {
        var members = new List<VariableDoc>();
        var block = s_enumBlock.Match(declaration);
        if (!block.Success)
        {
            return members;
        }

        foreach (Match member in s_enumMember.Matches(block.Groups["body"].Value))
        {
            var name = member.Groups["name"].Value;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            members.Add(new VariableDoc(
                name,
                member.Groups["value"].Value.Trim(),
                member.Groups["comment"].Value.Trim()));
        }

        return members;
    }

    private static VariableDoc MakeVariable(Match line)
    {
        var comment = line.Groups["comment"].Success && line.Groups["comment"].Value.Length > 0
            ? line.Groups["comment"].Value
            : line.Groups["block_comment"].Value;
        return new VariableDoc(
            line.Groups["name"].Value,
            line.Groups["type"].Value.Trim(),
            comment.Trim(),
            line.Groups["default"].Value.Trim());
    }

    // -- Declaration meta -----------------------------------------------------

    private static readonly Regex s_methodReturn = new(@"METHOD\s+\w+\s*:\s*(\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_propReturn = new(@"PROPERTY\s+\w+\s*:\s*(\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_extends = new(@"\bEXTENDS\s+([\w.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_implements = new(@"\bIMPLEMENTS\s+([\w.,\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_baseType = new(
        @"^(?:ARRAY\s*\[[^\]]*\]\s*OF\s*|POINTER\s+TO\s*|REFERENCE\s+TO\s*)*(\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> s_visibilityWords = new(StringComparer.Ordinal)
    {
        "PUBLIC", "PRIVATE", "PROTECTED", "INTERNAL",
    };

    private static readonly HashSet<string> s_metaKeywords = new(StringComparer.Ordinal)
    {
        "FUNCTION_BLOCK", "FUNCTION", "PROGRAM", "INTERFACE", "METHOD", "PROPERTY", "TYPE",
    };

    internal sealed record DeclarationMeta(
        string Visibility, bool IsAbstract, bool IsFinal, string Extends, IReadOnlyList<string> Implements);

    internal static string ExtractReturnType(string declaration)
    {
        var m = s_methodReturn.Match(declaration);
        return m.Success ? m.Groups[1].Value : "";
    }

    internal static string ExtractPropertyType(string declaration)
    {
        var m = s_propReturn.Match(declaration);
        return m.Success ? m.Groups[1].Value : "";
    }

    internal static DeclarationMeta ExtractDeclarationMeta(string declaration)
    {
        foreach (var line in declaration.Split('\n'))
        {
            var words = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                continue;
            }

            if (!s_metaKeywords.Contains(words[0].ToUpperInvariant()))
            {
                continue;
            }

            var upperWords = words.Select(w => w.ToUpperInvariant().TrimEnd(',')).ToHashSet(StringComparer.Ordinal);
            var visibility = words.FirstOrDefault(w => s_visibilityWords.Contains(w.ToUpperInvariant())) ?? "";

            var extMatch = s_extends.Match(line);
            var extends = extMatch.Success ? extMatch.Groups[1].Value : "";

            var implMatch = s_implements.Match(line);
            var implements = implMatch.Success
                ? implMatch.Groups[1].Value.Split(',')
                    .Select(i => i.Trim().TrimEnd(','))
                    .Where(i => i.Length > 0)
                    .ToList()
                : [];

            return new DeclarationMeta(
                visibility, upperWords.Contains("ABSTRACT"), upperWords.Contains("FINAL"), extends, implements);
        }

        return new DeclarationMeta("", false, false, "", []);
    }

    private static string BaseType(string typeStr)
    {
        var m = s_baseType.Match(typeStr.Trim());
        return m.Success ? m.Groups[1].Value : typeStr.Trim();
    }

    internal static string BaseTypeName(string typeStr) => BaseType(typeStr);

    // -- Enrichment + cross-reference -----------------------------------------

    private static List<VariableDoc> EnrichVars(List<VariableDoc> vars, IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.Count == 0)
        {
            return vars;
        }

        for (var i = 0; i < vars.Count; i++)
        {
            if (vars[i].Comment.Length == 0 && parameters.TryGetValue(vars[i].Name, out var doc))
            {
                vars[i] = vars[i] with { Comment = doc };
            }
        }

        return vars;
    }

    /// <summary>
    /// Populate <see cref="ObjectDoc.UsedBy"/> by scanning variable, method-return, and property-return
    /// types across the PLC. References are scoped within one PLC project (ADR-0005).
    /// </summary>
    private static void ComputeUsedBy(IReadOnlyList<ObjectDoc> objects)
    {
        var known = new Dictionary<string, ObjectDoc>(StringComparer.Ordinal);
        foreach (var obj in objects)
        {
            known[obj.Name] = obj;
        }

        void Record(string typeStr, string referencingName)
        {
            var b = BaseType(typeStr);
            if (b != referencingName && known.TryGetValue(b, out var target) && !target.UsedBy.Contains(referencingName))
            {
                target.UsedBy.Add(referencingName);
            }
        }

        foreach (var obj in objects)
        {
            foreach (var v in obj.Inputs.Concat(obj.Outputs).Concat(obj.Inout).Concat(obj.Variables))
            {
                Record(v.VarType, obj.Name);
            }

            foreach (var m in obj.Methods)
            {
                if (m.ReturnType.Length > 0)
                {
                    Record(m.ReturnType, obj.Name);
                }

                foreach (var v in m.Inputs.Concat(m.Outputs).Concat(m.Inout))
                {
                    Record(v.VarType, obj.Name);
                }
            }

            foreach (var p in obj.Properties)
            {
                if (p.ReturnType.Length > 0)
                {
                    Record(p.ReturnType, obj.Name);
                }
            }
        }
    }

    // -- Public builder -------------------------------------------------------

    /// <summary>
    /// Build a full <see cref="ProjectDoc"/> from a TwinCAT solution directory: one <see cref="PlcDoc"/>
    /// per discovered <c>.plcproj</c>, or a single anonymous PLC built from the directory when none is
    /// found. Throws <see cref="NoSourceFilesException"/> when no source files exist anywhere below it.
    /// </summary>
    internal static ProjectDoc BuildProjectDoc(string projectPath)
    {
        var project = new DirectoryInfo(Path.GetFullPath(projectPath));
        var plcprojPaths = SafeEnumerate(project.FullName, "*.plcproj").OrderBy(p => p, StringComparer.Ordinal).ToList();

        var plcs = new Dictionary<string, PlcDoc>(StringComparer.Ordinal);
        var totalObjects = 0;
        if (plcprojPaths.Count > 0)
        {
            foreach (var plcproj in plcprojPaths)
            {
                var plcDoc = BuildPlcDocFromRoot(
                    Path.GetDirectoryName(plcproj)!, Path.GetFileNameWithoutExtension(plcproj), plcproj);
                plcs[plcDoc.Name] = plcDoc;
                totalObjects += plcDoc.Objects.Count;
            }
        }
        else
        {
            var plcDoc = BuildPlcDocFromRoot(project.FullName, project.Name, "");
            if (plcDoc.Objects.Count > 0)
            {
                plcs[plcDoc.Name] = plcDoc;
                totalObjects += plcDoc.Objects.Count;
            }
        }

        if (totalObjects == 0)
        {
            throw new NoSourceFilesException($"No TwinCAT source files found in {projectPath}");
        }

        return new ProjectDoc(project.Name, plcs);
    }

    private static PlcDoc BuildPlcDocFromRoot(string root, string plcName, string plcprojPath)
    {
        // .TcIO is the dedicated interface-file extension some projects (e.g. TcUnit) use; TcSource
        // handles <Itf> roots, so both are globbed.
        var tcpouFiles = SafeEnumerate(root, "*.TcPOU")
            .Concat(SafeEnumerate(root, "*.TcIO"))
            .OrderBy(p => p, StringComparer.Ordinal);
        var tcgvlFiles = SafeEnumerate(root, "*.TcGVL").OrderBy(p => p, StringComparer.Ordinal);
        var tcdutFiles = SafeEnumerate(root, "*.TcDUT").OrderBy(p => p, StringComparer.Ordinal);

        var objects = new List<ObjectDoc>();

        foreach (var path in tcpouFiles)
        {
            TcSource.SourcePou pou;
            try
            {
                pou = TcSource.ParsePou(path);
            }
            catch (Exception) when (NotCritical())
            {
                continue;
            }

            var comment = CommentExtractor.Extract(pou.Declaration);
            var vars = ParseVariables(pou.Declaration);
            EnrichVars(vars.Input, comment.Params);
            EnrichVars(vars.Inout, comment.Params);
            var meta = ExtractDeclarationMeta(pou.Declaration);

            var methods = new List<MethodDoc>();
            foreach (var m in pou.Methods)
            {
                var mComment = CommentExtractor.Extract(m.Declaration);
                var mVars = ParseVariables(m.Declaration);
                var mMeta = ExtractDeclarationMeta(m.Declaration);
                EnrichVars(mVars.Input, mComment.Params);
                EnrichVars(mVars.Output, mComment.Params);
                methods.Add(new MethodDoc
                {
                    Name = m.Name,
                    ReturnType = ExtractReturnType(m.Declaration),
                    Comment = mComment,
                    Visibility = mMeta.Visibility,
                    IsAbstract = mMeta.IsAbstract,
                    IsFinal = mMeta.IsFinal,
                    Inputs = mVars.Input,
                    Outputs = mVars.Output,
                    Inout = mVars.Inout,
                    Body = m.Body,
                });
            }

            var properties = new List<PropertyDoc>();
            foreach (var p in pou.Properties)
            {
                var pComment = CommentExtractor.Extract(p.Declaration);
                var pMeta = ExtractDeclarationMeta(p.Declaration);
                properties.Add(new PropertyDoc
                {
                    Name = p.Name,
                    ReturnType = ExtractPropertyType(p.Declaration),
                    Comment = pComment,
                    Visibility = pMeta.Visibility,
                    HasGet = p.HasGet,
                    HasSet = p.HasSet,
                });
            }

            objects.Add(new ObjectDoc
            {
                Name = pou.Name,
                ObjType = pou.PouType,
                Declaration = pou.Declaration,
                Comment = comment,
                PlcName = plcName,
                Visibility = meta.Visibility,
                IsAbstract = meta.IsAbstract,
                IsFinal = meta.IsFinal,
                Extends = meta.Extends,
                Implements = meta.Implements,
                Inputs = vars.Input,
                Outputs = vars.Output,
                Inout = vars.Inout,
                Variables = vars.Variable,
                Methods = methods,
                Properties = properties,
                Actions = pou.Actions,
            });
        }

        foreach (var path in tcgvlFiles)
        {
            TcSource.SourceGvl gvl;
            try
            {
                gvl = TcSource.ParseGvl(path);
            }
            catch (Exception) when (NotCritical())
            {
                continue;
            }

            var comment = CommentExtractor.Extract(gvl.Declaration);
            var vars = ParseVariables(gvl.Declaration);
            objects.Add(new ObjectDoc
            {
                Name = gvl.Name,
                ObjType = "gvl",
                Declaration = gvl.Declaration,
                Comment = comment,
                PlcName = plcName,
                Variables = vars.Variable,
            });
        }

        foreach (var path in tcdutFiles)
        {
            TcSource.SourceDut dut;
            try
            {
                dut = TcSource.ParseDut(path);
            }
            catch (Exception) when (NotCritical())
            {
                continue;
            }

            var comment = CommentExtractor.Extract(dut.Declaration);
            var declUpper = dut.Declaration.ToUpperInvariant();

            // TwinCAT enums use ( ... ) syntax, not an ENUM keyword; structs and unions use the keyword.
            var isStruct = declUpper.Contains("STRUCT", StringComparison.Ordinal)
                || declUpper.Contains("UNION", StringComparison.Ordinal);
            var variables = isStruct ? ParseStructFields(dut.Declaration) : ParseEnumMembers(dut.Declaration);
            objects.Add(new ObjectDoc
            {
                Name = dut.Name,
                ObjType = isStruct ? "struct" : "enum",
                Declaration = dut.Declaration,
                Comment = comment,
                PlcName = plcName,
                Variables = variables,
            });
        }

        ComputeUsedBy(objects);
        return new PlcDoc(plcName, plcprojPath, objects);
    }

    private static IEnumerable<string> SafeEnumerate(string root, string pattern)
        => Directory.Exists(root)
            ? Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            : [];

    // The parse loops mirror the Python `except Exception: continue` — a malformed file is skipped, not
    // fatal. Genuinely fatal conditions (OOM, stack overflow) are not swallowed.
    private static bool NotCritical() => true;
}
