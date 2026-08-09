using System.Text.RegularExpressions;

namespace TcKit.Core.Analysis;

/// <summary>
/// Parses a TwinCAT declaration block into a header plus the variables it declares. Works on the
/// masked form from <see cref="StSource"/>, so comments, string literals and pragmas cannot be
/// mistaken for code, and every reported line agrees with the original text.
///
/// This is deliberately a declaration parser, not an ST parser: the naming lane needs
/// (name, type, section, qualifiers, line) and nothing more.
/// </summary>
public static partial class DeclarationParser
{
    /// <summary>Parse a POU, method, property or GVL declaration block.</summary>
    public static StDeclaration Parse(string declarationText)
    {
        ArgumentNullException.ThrowIfNull(declarationText);

        var mask = StSource.Mask(declarationText);
        var variables = new List<StVariable>();
        VarSection? openSection = null;
        var openQualifiers = VarQualifiers.None;
        var blockStart = 0;
        var firstOpener = -1;

        foreach (Match boundary in BlockBoundary().Matches(mask))
        {
            if (boundary.Groups["close"].Success)
            {
                if (openSection is not null)
                {
                    foreach (var parsed in CollectDeclarations(mask, blockStart, boundary.Index))
                    {
                        variables.Add(new StVariable
                        {
                            Name = parsed.Name,
                            TypeExpression = parsed.TypeExpression,
                            Section = openSection.Value,
                            Qualifiers = openQualifiers,
                            Line = parsed.Line,
                            Address = parsed.Address,
                        });
                    }

                    openSection = null;
                }

                continue;
            }

            if (firstOpener < 0)
            {
                firstOpener = boundary.Index;
            }

            // A second opener with no intervening END_VAR is malformed; the newer opener wins.
            var keyword = boundary.Groups["sec"].Value;
            openSection = SectionFor(keyword);
            openQualifiers = QualifiersFor(keyword, boundary.Groups["quals"].Value);
            blockStart = boundary.Index + boundary.Length;
        }

        // A header always precedes the first VAR block. Bounding the search there stops a
        // variable that happens to be called "Program" from being read as a POU header.
        var headerLimit = firstOpener >= 0 ? firstOpener : mask.Length;
        return new StDeclaration
        {
            Header = ParseHeader(mask[..headerLimit]),
            Variables = variables,
        };
    }

    /// <summary>Parse a DUT declaration into its type name and members (struct/union fields, or enum constants).</summary>
    public static StTypeDeclaration ParseType(string declarationText)
    {
        ArgumentNullException.ThrowIfNull(declarationText);

        var mask = StSource.Mask(declarationText);
        var header = TypeHeader().Match(mask);
        var members = new List<StTypeMember>();

        var structBody = StructBody().Match(mask);
        if (structBody.Success)
        {
            var body = structBody.Groups["body"];
            foreach (var parsed in CollectDeclarations(mask, body.Index, body.Index + body.Length))
            {
                members.Add(new StTypeMember
                {
                    Name = parsed.Name,
                    TypeExpression = parsed.TypeExpression,
                    Line = parsed.Line,
                });
            }
        }
        else
        {
            var enumBody = EnumBody().Match(mask);
            if (enumBody.Success)
            {
                var body = enumBody.Groups["body"];
                foreach (var (start, end) in SplitTopLevel(mask, body.Index, body.Index + body.Length, ','))
                {
                    var identifier = LeadingIdentifier().Match(mask[start..end]);
                    if (identifier.Success)
                    {
                        members.Add(new StTypeMember
                        {
                            Name = identifier.Groups[1].Value,
                            Line = StSource.LineAt(mask, start + identifier.Groups[1].Index),
                        });
                    }
                }
            }
        }

        return new StTypeDeclaration
        {
            Name = header.Success ? header.Groups["name"].Value : "",
            Line = header.Success ? StSource.LineAt(mask, header.Groups["name"].Index) : 1,
            Members = members,
        };
    }

    private readonly record struct ParsedName(string Name, string TypeExpression, int Line, string Address);

    /// <summary>
    /// Split a region into <c>;</c>-terminated declarations and parse each into its names and type.
    /// Declarations may span lines, so this works on character offsets rather than line by line.
    /// </summary>
    private static List<ParsedName> CollectDeclarations(string mask, int start, int end)
    {
        var result = new List<ParsedName>();
        foreach (var (chunkStart, chunkEnd) in SplitTopLevel(mask, start, end, ';'))
        {
            var colon = IndexOfTypeSeparator(mask, chunkStart, chunkEnd);
            if (colon < 0)
            {
                continue;
            }

            var typeExpression = ExtractType(mask, colon + 1, chunkEnd);
            foreach (var (nameStart, nameEnd) in SplitTopLevel(mask, chunkStart, colon, ','))
            {
                var piece = mask[nameStart..nameEnd];
                var identifier = LeadingIdentifier().Match(piece);
                if (!identifier.Success)
                {
                    continue;
                }

                var address = AtAddress().Match(piece);
                result.Add(new ParsedName(
                    identifier.Groups[1].Value,
                    typeExpression,
                    StSource.LineAt(mask, nameStart + identifier.Groups[1].Index),
                    address.Success ? address.Groups[1].Value : ""));
            }
        }

        return result;
    }

    /// <summary>Split a region on <paramref name="separator"/>, ignoring separators nested in brackets or parentheses.</summary>
    private static List<(int Start, int End)> SplitTopLevel(string mask, int start, int end, char separator)
    {
        var pieces = new List<(int, int)>();
        if (end <= start)
        {
            return pieces;
        }

        var depth = 0;
        var pieceStart = start;
        for (var i = start; i < end; i++)
        {
            var current = mask[i];
            if (current is '[' or '(')
            {
                depth++;
            }
            else if (current is ']' or ')')
            {
                depth--;
            }
            else if (current == separator && depth <= 0)
            {
                Append(pieces, mask, pieceStart, i);
                pieceStart = i + 1;
            }
        }

        Append(pieces, mask, pieceStart, end);
        return pieces;

        static void Append(List<(int, int)> into, string text, int from, int to)
        {
            if (to > from && !text.AsSpan(from, to - from).IsWhiteSpace())
            {
                into.Add((from, to));
            }
        }
    }

    /// <summary>Index of the <c>:</c> separating names from the type, skipping <c>:=</c> and nested regions.</summary>
    private static int IndexOfTypeSeparator(string mask, int start, int end)
    {
        var depth = 0;
        for (var i = start; i < end; i++)
        {
            var current = mask[i];
            if (current is '[' or '(')
            {
                depth++;
            }
            else if (current is ']' or ')')
            {
                depth--;
            }
            else if (current == ':' && depth <= 0)
            {
                if (i + 1 < end && mask[i + 1] == '=')
                {
                    i++;
                    continue;
                }

                return i;
            }
        }

        return -1;
    }

    /// <summary>The type expression between the separator and any <c>:=</c> initialiser, whitespace-normalised.</summary>
    private static string ExtractType(string mask, int start, int end)
    {
        var depth = 0;
        var stop = end;
        for (var i = start; i < end; i++)
        {
            var current = mask[i];
            if (current is '[' or '(')
            {
                depth++;
            }
            else if (current is ']' or ')')
            {
                depth--;
            }
            else if (current == ':' && depth <= 0 && i + 1 < end && mask[i + 1] == '=')
            {
                stop = i;
                break;
            }
        }

        return stop > start ? Whitespace().Replace(mask[start..stop], " ").Trim() : "";
    }

    private static StHeader ParseHeader(string mask)
    {
        var match = HeaderLine().Match(mask);
        if (!match.Success)
        {
            return new StHeader();
        }

        var rest = match.Groups["rest"].Value;
        var returnType = "";
        var colon = IndexOfTypeSeparator(rest, 0, rest.Length);
        if (colon >= 0)
        {
            returnType = Whitespace().Replace(rest[(colon + 1)..], " ").Trim();
            rest = rest[..colon];
        }

        var accessibility = StAccessibility.Public;
        var isAbstract = false;
        var isFinal = false;
        var name = "";
        var extends = "";
        var implements = new List<string>();
        var clause = HeaderClause.Name;

        foreach (var token in rest.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token.ToUpperInvariant())
            {
                case "PUBLIC": accessibility = StAccessibility.Public; continue;
                case "PRIVATE": accessibility = StAccessibility.Private; continue;
                case "PROTECTED": accessibility = StAccessibility.Protected; continue;
                case "INTERNAL": accessibility = StAccessibility.Internal; continue;
                case "ABSTRACT": isAbstract = true; continue;
                case "FINAL": isFinal = true; continue;
                case "EXTENDS": clause = HeaderClause.Extends; continue;
                case "IMPLEMENTS": clause = HeaderClause.Implements; continue;
                default: break;
            }

            switch (clause)
            {
                case HeaderClause.Extends when extends.Length == 0:
                    extends = token;
                    break;
                case HeaderClause.Implements:
                    implements.Add(token);
                    break;
                case HeaderClause.Name when name.Length == 0:
                    name = token;
                    break;
                default:
                    break;
            }
        }

        return new StHeader
        {
            Keyword = match.Groups["kw"].Value.ToUpperInvariant(),
            Line = StSource.LineAt(mask, match.Index),
            Name = name,
            ReturnType = returnType,
            Accessibility = accessibility,
            Extends = extends,
            Implements = implements,
            IsAbstract = isAbstract,
            IsFinal = isFinal,
        };
    }

    private enum HeaderClause
    {
        Name,
        Extends,
        Implements,
    }

    private static VarSection SectionFor(string keyword) => keyword.ToUpperInvariant() switch
    {
        "VAR_INPUT" => VarSection.VarInput,
        "VAR_OUTPUT" => VarSection.VarOutput,
        "VAR_IN_OUT" => VarSection.VarInOut,
        "VAR_GLOBAL" => VarSection.VarGlobal,
        "VAR_STAT" => VarSection.VarStat,
        "VAR_TEMP" => VarSection.VarTemp,
        "VAR_INST" => VarSection.VarInst,
        _ => VarSection.Var,
    };

    private static VarQualifiers QualifiersFor(string keyword, string qualifierText)
    {
        // The TwinCAT 2 spelling VAR_PERSISTENT survives in older projects.
        var result = keyword.Equals("VAR_PERSISTENT", StringComparison.OrdinalIgnoreCase)
            ? VarQualifiers.Persistent
            : VarQualifiers.None;

        foreach (Match qualifier in QualifierWord().Matches(qualifierText))
        {
            result |= qualifier.Value.ToUpperInvariant() switch
            {
                "CONSTANT" => VarQualifiers.Constant,
                "RETAIN" => VarQualifiers.Retain,
                _ => VarQualifiers.Persistent,
            };
        }

        return result;
    }

    [GeneratedRegex(
        @"(?<open>^[ \t]*(?<sec>VAR_INPUT|VAR_OUTPUT|VAR_IN_OUT|VAR_GLOBAL|VAR_STAT|VAR_TEMP|VAR_INST|VAR_PERSISTENT|VAR)(?<quals>(?:[ \t]+(?:CONSTANT|RETAIN|PERSISTENT))*)[ \t]*\r?$)"
        + @"|(?<close>^[ \t]*END_VAR[ \t]*\r?$)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BlockBoundary();

    [GeneratedRegex(
        @"^[ \t]*(?<kw>FUNCTION_BLOCK|FUNCTION|PROGRAM|INTERFACE|METHOD|PROPERTY|ACTION)\b(?<rest>[^\r\n]*)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex HeaderLine();

    // Anchored with [ \t]* rather than \s*, which would cross newlines and report the header as
    // starting at the top of the block: TcOpen prefixes its DUTs with {attribute 'qualified_only'}
    // pragma lines, and those mask to blank lines that \s* would swallow.
    //
    // The access specifier is skipped so the name group is the type's name and not "INTERNAL".
    [GeneratedRegex(
        @"^[ \t]*TYPE\s+(?:(?:PUBLIC|PRIVATE|PROTECTED|INTERNAL|FINAL)\s+)*(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex TypeHeader();

    [GeneratedRegex(@"\b(?:STRUCT|UNION)\b(?<body>[\s\S]*?)\bEND_(?:STRUCT|UNION)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StructBody();

    [GeneratedRegex(@":\s*\((?<body>[\s\S]*?)\)", RegexOptions.IgnoreCase)]
    private static partial Regex EnumBody();

    [GeneratedRegex(@"^\s*([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex LeadingIdentifier();

    [GeneratedRegex(@"\bAT\s+(%[^\s:]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AtAddress();

    [GeneratedRegex(@"\b(?:CONSTANT|RETAIN|PERSISTENT)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualifierWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
