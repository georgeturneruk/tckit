using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace TcKit.Adapters.Docs;

/// <summary>
/// One parsed parameter-table row (name / type / direction / description), before the FB split into
/// inputs and outputs.
/// </summary>
internal sealed record ParamRow(string Name, string Type, string Direction, string Description);

/// <summary>One property/value row from a hardware "Technical data" table.</summary>
internal sealed record TechRow(string Property, string Value);

/// <summary>
/// HTML parsing for infosys.beckhoff.com pages: clean text, titles, the first descriptive paragraph,
/// and parameter tables. Mirrors the Python <c>_infosys_parser</c> module; AngleSharp's CSS selectors
/// stand in for BeautifulSoup's <c>select_one</c>/<c>find_all</c>.
///
/// Infosys HTML (as of 2024): main content sits in <c>div#content</c> or <c>div.topic</c>; parameter
/// tables carry <c>Name</c>/<c>Type</c>/<c>Direction</c> headers, either as one table with a
/// Direction column (older style) or one table per VAR_INPUT/VAR_OUTPUT block (newer TF6xxx style).
/// </summary>
internal static class InfosysParser
{
    private static readonly HtmlParser Parser = new();

    private static readonly string[] InputHeadings =
        ["var_input", "input", "inputs", "varat_eingang", "eingang"];

    private static readonly string[] OutputHeadings =
        ["var_output", "output", "outputs", "var_in_out", "in_out", "ausgang"];

    public static IDocument Parse(string html) => Parser.ParseDocument(html);

    /// <summary>Extract the page title from &lt;title&gt;, stripping Beckhoff branding suffixes.</summary>
    public static string ExtractTitle(IDocument doc)
    {
        var title = doc.QuerySelector("title")?.TextContent.Trim() ?? "";
        foreach (var suffix in new[] { " - Beckhoff Infosys", " | Beckhoff", " - TwinCAT" })
        {
            if (title.EndsWith(suffix, StringComparison.Ordinal))
            {
                title = title[..^suffix.Length].Trim();
            }
        }

        return title;
    }

    /// <summary>Extract the main text content, stripping nav/header/footer/script/style boilerplate.</summary>
    public static string ExtractMainContent(IDocument doc)
    {
        foreach (var node in doc.QuerySelectorAll("nav, header, footer, script, style"))
        {
            node.Remove();
        }

        foreach (var selector in new[] { "div#content", "div.topic", "main", "article", "div.content" })
        {
            var container = doc.QuerySelector(selector);
            if (container is not null)
            {
                return CleanText(NewlineText(container));
            }
        }

        var body = doc.Body;
        return body is not null ? CleanText(NewlineText(body)) : CleanText(NewlineText(doc.DocumentElement));
    }

    /// <summary>Extract the first meaningful paragraph as the FB/function description.</summary>
    public static string ExtractDescription(IDocument doc)
    {
        foreach (var selector in new[] { "div#content p", "div.topic p", "main p", "p" })
        {
            var tag = doc.QuerySelector(selector);
            if (tag is null)
            {
                continue;
            }

            var text = SpaceText(tag);
            if (text.Length > 20)
            {
                return text;
            }
        }

        return "";
    }

    /// <summary>
    /// Extract parameter rows from infosys parameter tables. Handles both the single-table layout
    /// (Direction column) and the per-direction-block layout (direction inferred from the nearest
    /// preceding heading or the table caption).
    /// </summary>
    public static IReadOnlyList<ParamRow> ExtractParameterTable(IDocument doc)
    {
        var rows = new List<ParamRow>();

        foreach (var table in doc.QuerySelectorAll("table"))
        {
            var headerRow = table.QuerySelector("tr");
            if (headerRow is null)
            {
                continue;
            }

            var headers = headerRow.QuerySelectorAll("th, td")
                .Select(c => StripText(c).ToLowerInvariant())
                .ToList();

            if (!headers.Any(h => h is "name" or "variable" or "parameter"))
            {
                continue;
            }

            var nameCol = FindCol(headers, "name", "variable", "parameter");
            var typeCol = FindCol(headers, "type", "data type");
            var directionCol = FindCol(headers, "direction", "access", "i/o");
            var descCol = FindCol(headers, "description", "comment", "meaning");

            var headingDirection = directionCol is null ? InferDirectionFromHeading(table) : "";

            foreach (var tr in table.QuerySelectorAll("tr").Skip(1))
            {
                var cells = tr.QuerySelectorAll("td, th").Select(StripText).ToList();
                if (cells.Count == 0)
                {
                    continue;
                }

                var direction = Cell(cells, directionCol);
                if (direction.Length == 0)
                {
                    direction = headingDirection;
                }

                rows.Add(new ParamRow(
                    Cell(cells, nameCol),
                    Cell(cells, typeCol),
                    direction,
                    Cell(cells, descCol)));
            }
        }

        return rows;
    }

    /// <summary>Read a &lt;meta name="..."&gt; content value, or null when absent.</summary>
    public static string? ExtractMeta(IDocument doc, string name)
    {
        var value = doc.QuerySelector($"meta[name={name}]")?.GetAttribute("content");
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Return the href of the first anchor whose visible text equals <paramref name="text"/>
    /// (case-insensitive, whitespace-normalised), or null. Used to resolve a terminal's page from a
    /// product-overview table where the order number is the link text.</summary>
    public static string? FindLinkByText(IDocument doc, string text)
    {
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            if (StripText(a).Equals(text, StringComparison.OrdinalIgnoreCase))
            {
                return a.GetAttribute("href");
            }
        }

        return null;
    }

    /// <summary>
    /// Extract a hardware "Technical data" table as property/value rows. Infosys renders it as a
    /// two-column table whose first row reads "Technical data | &lt;order number&gt;"; rows with fewer
    /// than two cells (sub-headings) are skipped. Returns an empty list when no such table is present.
    /// </summary>
    public static IReadOnlyList<TechRow> ExtractTechnicalData(IDocument doc)
    {
        foreach (var table in doc.QuerySelectorAll("table"))
        {
            var trs = table.QuerySelectorAll("tr");
            if (trs.Length == 0)
            {
                continue;
            }

            var header = string.Join(" ", trs[0].QuerySelectorAll("td, th").Select(StripText)).ToLowerInvariant();
            if (!header.Contains("technical data", StringComparison.Ordinal))
            {
                continue;
            }

            var rows = new List<TechRow>();
            foreach (var tr in trs.Skip(1))
            {
                var cells = tr.QuerySelectorAll("td, th").Select(StripText).ToList();
                if (cells.Count >= 2 && cells[0].Length > 0)
                {
                    rows.Add(new TechRow(cells[0], cells[1]));
                }
            }

            if (rows.Count > 0)
            {
                return rows;
            }
        }

        return [];
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>Text content with a newline between every text node (BeautifulSoup get_text("\n")).</summary>
    private static string NewlineText(INode? node) =>
        node is null ? "" : string.Join("\n", node.Descendants<IText>().Select(t => t.Text));

    /// <summary>Whitespace-normalised text content (BeautifulSoup get_text(" ").strip()).</summary>
    private static string SpaceText(IElement el) => Regex.Replace(el.TextContent, @"\s+", " ").Trim();

    /// <summary>Whitespace-normalised text content for a single cell/title (BeautifulSoup strip=True).</summary>
    private static string StripText(IElement el) => Regex.Replace(el.TextContent, @"\s+", " ").Trim();

    private static string CleanText(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim());
        var result = new List<string>();
        var prevBlank = false;
        foreach (var line in lines)
        {
            var isBlank = line.Length == 0;
            if (isBlank && prevBlank)
            {
                continue;
            }

            result.Add(line);
            prevBlank = isBlank;
        }

        return string.Join("\n", result).Trim();
    }

    private static string InferDirectionFromHeading(IElement table)
    {
        for (var sibling = table.PreviousElementSibling; sibling is not null; sibling = sibling.PreviousElementSibling)
        {
            if (sibling.TagName is "H1" or "H2" or "H3" or "H4" or "H5" or "P" or "CAPTION")
            {
                var text = StripText(sibling).ToLowerInvariant();
                if (InputHeadings.Any(text.Contains))
                {
                    return "input";
                }

                if (OutputHeadings.Any(text.Contains))
                {
                    return "output";
                }

                break; // stop at the first heading-like element
            }
        }

        var caption = table.QuerySelector("caption");
        if (caption is not null)
        {
            var text = StripText(caption).ToLowerInvariant();
            if (InputHeadings.Any(text.Contains))
            {
                return "input";
            }

            if (OutputHeadings.Any(text.Contains))
            {
                return "output";
            }
        }

        return "";
    }

    private static int? FindCol(List<string> headers, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            for (var i = 0; i < headers.Count; i++)
            {
                if (headers[i].Contains(candidate, StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }

        return null;
    }

    private static string Cell(List<string> cells, int? col) =>
        col is null || col >= cells.Count ? "" : cells[col.Value];
}
