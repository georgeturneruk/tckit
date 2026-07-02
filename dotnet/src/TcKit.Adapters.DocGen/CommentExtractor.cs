using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TcKit.Adapters.DocGen;

/// <summary>Parsed documentation comment extracted from a declaration block.</summary>
internal sealed record CommentDoc
{
    public string Description { get; init; } = "";

    public IReadOnlyDictionary<string, string> Params { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string Returns { get; init; } = "";

    public string Remarks { get; init; } = "";
}

/// <summary>
/// Auto-detect and parse doc comments from ST declaration strings. Ported from the Python
/// <c>_comment_extractor.py</c>. Supports four styles found in TwinCAT projects:
/// <list type="bullet">
///   <item><c>xml_docu</c> — <c>(*~ &lt;docu&gt;&lt;summary&gt;...&lt;/summary&gt;&lt;/docu&gt; ~*)</c> (TcOpen / TE1030)</item>
///   <item><c>block_rst</c> — <c>(* :Description: ... :param x: ... *)</c> (plcdoc convention)</item>
///   <item><c>line_rst</c> — <c>// :Description: ... // :param x: ...</c> (common informal style)</item>
///   <item><c>plain</c> — an unstructured comment (yields an empty <see cref="CommentDoc"/>)</item>
/// </list>
/// </summary>
internal static class CommentExtractor
{
    // PLC keyword list: the preamble (the comment region) ends at the first of these. VAR_GLOBAL
    // bounds a GVL preamble so an inline (* per-variable *) comment in the body is not misread as the
    // object's doc comment.
    private static readonly string[] s_keywords =
    [
        "FUNCTION_BLOCK", "FUNCTION", "PROGRAM", "INTERFACE", "METHOD", "PROPERTY", "TYPE", "VAR_GLOBAL",
    ];

    /// <summary>
    /// Extract and parse a doc comment from a ST declaration string. Scans the text before the first
    /// PLC keyword, detects the comment style, and returns a normalised <see cref="CommentDoc"/> (empty
    /// when no structured comment is found).
    /// </summary>
    internal static CommentDoc Extract(string declaration)
    {
        var preamble = ExtractPreamble(declaration);
        if (string.IsNullOrWhiteSpace(preamble))
        {
            return new CommentDoc();
        }

        var style = DetectStyle(preamble);
        return style switch
        {
            "xml_docu" => ParseXmlDocu(preamble),
            "block_rst" or "line_rst" => ParseRstLines(NormaliseToLines(preamble, style)),
            _ => new CommentDoc(),
        };
    }

    // -- Preamble extraction --------------------------------------------------

    /// <summary>
    /// Return the text before the first PLC keyword. Keywords match only at the start of a line (after
    /// trimming) so keyword words inside comment text are not treated as declaration boundaries.
    /// </summary>
    internal static string ExtractPreamble(string declaration)
    {
        var collected = new List<string>();
        foreach (var line in SplitKeepEnds(declaration))
        {
            var stripped = line.Trim().ToUpperInvariant();
            if (s_keywords.Any(kw => stripped.StartsWith(kw, StringComparison.Ordinal)))
            {
                break;
            }

            collected.Add(line);
        }

        return string.Concat(collected);
    }

    // -- Style detection ------------------------------------------------------

    /// <summary>
    /// Detect the comment style. A block-comment marker <c>(*</c> must begin a non-empty line to count
    /// as a doc comment, so inline block comments inside variable declarations do not trigger it.
    /// </summary>
    internal static string DetectStyle(string preamble)
    {
        foreach (var line in preamble.Split('\n'))
        {
            var stripped = line.Trim();
            if (stripped.Length == 0)
            {
                continue;
            }

            if (stripped.StartsWith("(*~", StringComparison.Ordinal))
            {
                return "xml_docu";
            }

            if (stripped.StartsWith("(*", StringComparison.Ordinal))
            {
                return "block_rst";
            }

            if (stripped.StartsWith("//", StringComparison.Ordinal))
            {
                return "line_rst";
            }
        }

        return "plain";
    }

    // -- XML <docu> parser ----------------------------------------------------

    private static readonly Regex s_docuBlock = new(@"\(\*~(.*?)~\*\)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex s_plainBlock = new(@"\(\*(.*?)\*\)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex s_whitespace = new(@"\s+", RegexOptions.Compiled);

    private static CommentDoc ParseXmlDocu(string preamble)
    {
        var match = s_docuBlock.Match(preamble);
        if (!match.Success)
        {
            match = s_plainBlock.Match(preamble);
        }

        if (!match.Success)
        {
            return new CommentDoc();
        }

        var raw = match.Groups[1].Value.Trim();

        XElement root;
        try
        {
            root = XElement.Parse($"<root>{raw}</root>");
        }
        catch (System.Xml.XmlException)
        {
            // Not valid XML — fall through to the RST parser.
            return ParseRstLines(StripCommentMarkers(raw));
        }

        var description = XmlText(root, "summary");
        if (description.Length == 0)
        {
            description = XmlText(root, "description");
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var paramEl in root.Descendants().Where(e => e.Name.LocalName == "param"))
        {
            var directText = DirectText(paramEl);
            var name = paramEl.Attribute("name")?.Value;
            if (string.IsNullOrEmpty(name))
            {
                name = directText.Length > 0
                    ? directText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ""
                    : "";
            }

            var text = CleanXmlText(directText);
            if (!string.IsNullOrEmpty(name))
            {
                parameters[name.Trim()] = text;
            }
        }

        return new CommentDoc
        {
            Description = CleanXmlText(description),
            Params = parameters,
            Returns = CleanXmlText(XmlText(root, "returns")),
            Remarks = CleanXmlText(XmlText(root, "remarks")),
        };
    }

    /// <summary>Concatenated, space-joined text of all matching descendant elements.</summary>
    private static string XmlText(XElement root, string tag)
    {
        var parts = root.Descendants()
            .Where(e => e.Name.LocalName == tag)
            .Select(e => e.Value.Trim())
            .Where(s => s.Length > 0);
        return string.Join(" ", parts);
    }

    /// <summary>Direct text of an element (text nodes that are immediate children), mirroring ElementTree's <c>.text</c>.</summary>
    private static string DirectText(XElement element)
        => string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value));

    private static string CleanXmlText(string text) => s_whitespace.Replace(text, " ").Trim();

    // -- RST-style parser (line_rst and block_rst) ----------------------------

    private static readonly Regex s_blockOpen = new(@"\(\*~?", RegexOptions.Compiled);
    private static readonly Regex s_blockClose = new(@"~?\*\)", RegexOptions.Compiled);

    private static string NormaliseToLines(string preamble, string style)
    {
        if (style == "block_rst")
        {
            var text = s_blockClose.Replace(s_blockOpen.Replace(preamble, ""), "");
            return text.Trim();
        }

        // line_rst: strip leading // from each line, skip {attribute ...} pragmas, drop other lines.
        var lines = new List<string>();
        foreach (var line in preamble.Split('\n'))
        {
            var stripped = line.Trim();
            if (stripped.StartsWith('{'))
            {
                continue;
            }

            if (stripped.StartsWith("//", StringComparison.Ordinal))
            {
                lines.Add(stripped[2..].Trim());
            }
        }

        return string.Join("\n", lines);
    }

    private static string StripCommentMarkers(string text)
    {
        text = s_blockClose.Replace(s_blockOpen.Replace(text, ""), "");
        var hasLine = text.Split('\n').Any(line => line.Trim().StartsWith("//", StringComparison.Ordinal));
        if (hasLine)
        {
            return string.Join("\n", text.Split('\n').Select(ln => ln.Trim().TrimStart('/').Trim()));
        }

        return text.Trim();
    }

    // :param name: value (two colons, name argument)
    private static readonly Regex s_paramRe = new(@"^:param\s+(?<name>\w+):\s*(?<value>.*)$", RegexOptions.Compiled);

    // :field: value (single colon, no name argument)
    private static readonly Regex s_fieldRe = new(@"^:(?<field>\w[\w ]*?):\s*(?<value>.*)$", RegexOptions.Compiled);

    private static CommentDoc ParseRstLines(string text)
    {
        var descriptionLines = new List<string>();
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var returns = "";
        var remarks = "";

        var inDescription = true;
        (string Type, string Value)? currentField = null;

        void Flush()
        {
            if (currentField is not { } field)
            {
                return;
            }

            var value = field.Value.Trim();
            switch (field.Type)
            {
                case "description":
                    descriptionLines.Add(value);
                    break;
                case "returns":
                    returns = value;
                    break;
                case "remarks":
                    remarks = value;
                    break;
            }
        }

        foreach (var line in text.Split('\n'))
        {
            var stripped = line.Trim();

            var pm = s_paramRe.Match(stripped);
            if (pm.Success)
            {
                Flush();
                inDescription = false;
                currentField = null;
                parameters[pm.Groups["name"].Value] = pm.Groups["value"].Value.Trim();
                continue;
            }

            var m = s_fieldRe.Match(stripped);
            if (m.Success)
            {
                Flush();
                inDescription = false;
                var fieldName = m.Groups["field"].Value.ToLowerInvariant();
                var value = m.Groups["value"].Value.Trim();
                currentField = fieldName switch
                {
                    "description" or "summary" => ("description", value),
                    "returns" or "return" => ("returns", value),
                    "remarks" => ("remarks", value),
                    _ => null,
                };
            }
            else if (inDescription && stripped.Length > 0)
            {
                descriptionLines.Add(stripped);
            }
            else if (currentField is { } cf && stripped.Length > 0)
            {
                // Continuation line.
                currentField = (cf.Type, cf.Value + " " + stripped);
            }
        }

        Flush();

        return new CommentDoc
        {
            Description = string.Join(" ", descriptionLines).Trim(),
            Params = parameters,
            Returns = returns,
            Remarks = remarks,
        };
    }

    /// <summary>Split text into lines while keeping the line terminators, mirroring Python's <c>splitlines(keepends=True)</c>.</summary>
    private static IEnumerable<string> SplitKeepEnds(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                yield return text[start..(i + 1)];
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }
}
