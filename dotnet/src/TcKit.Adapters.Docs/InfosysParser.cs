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

    // A Beckhoff order number, e.g. EL3004, EPP1008-0001 — used to drop comparison-header rows.
    private static readonly Regex OrderNumberLike =
        new(@"^[A-Z]{2,4}\d{3,4}(-\d{3,4})?$", RegexOptions.Compiled);

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

    /// <summary>
    /// Return the href of the anchor whose visible text names the order number, or null. The anchor
    /// text may carry an exact order ("EL3004"), a variant-suffixed order ("EPP1008-0001"), or one-or-
    /// more orders in family-wildcard form ("EK110x-00xx, EK15xx", "EPI1008-000x, ERI1008-000x"). Each
    /// comma/space/slash-separated token is matched with the same x-wildcard and variant-suffix rules
    /// used to pick the section (<see cref="InfosysNavigator.SectionCoversOrder"/>). An exact
    /// (variant-suffix) match is preferred over a wildcard one, so a specific "EP3174-0002" entry wins
    /// over a broad "EP31xx-xxxx" group heading on the same overview. Whitespace-normalised,
    /// case-insensitive.
    /// </summary>
    public static string? FindLinkByOrder(IDocument doc, string order)
    {
        string? wildcardHref = null;
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var text = StripText(a);
            foreach (var token in text.Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                // Exact: the token's base (variant suffix dropped) equals the order, e.g.
                // "EP3174-0002" -> "EP3174". Returns at once.
                if (token.Split('-')[0].Equals(order, StringComparison.OrdinalIgnoreCase))
                {
                    return a.GetAttribute("href");
                }

                // Wildcard / group: e.g. "EK110x-00xx" covers EK1100. Kept only if no exact is found.
                if (wildcardHref is null && InfosysNavigator.SectionCoversOrder(token, order))
                {
                    wildcardHref = a.GetAttribute("href");
                }
            }
        }

        return wildcardHref;
    }

    /// <summary>
    /// Extract a hardware "Technical data" table as property/value rows. Each row contributes its last
    /// two non-empty cells (so a 2-column EL table "property | value" and a 3-column EtherCAT Box table
    /// "category | property | value" both yield property/value). A table explicitly marked "Technical
    /// data" (first row, caption, or a preceding heading) is preferred; otherwise the richest
    /// property/value table on the page is used, which suits the dedicated technical-data page
    /// find_hardware navigates to. Returns an empty list when nothing table-like is found.
    /// </summary>
    public static IReadOnlyList<TechRow> ExtractTechnicalData(IDocument doc)
    {
        var tables = doc.QuerySelectorAll("table");

        foreach (var table in tables)
        {
            if (IsMarkedTechnicalData(table))
            {
                var marked = ParseKeyValueRows(table);
                if (marked.Count > 0)
                {
                    return marked;
                }
            }
        }

        var best = new List<TechRow>();
        foreach (var table in tables)
        {
            var rows = ParseKeyValueRows(table);
            if (rows.Count > best.Count)
            {
                best = rows;
            }
        }

        // A handful of rows guards against picking up an incidental two-cell table.
        return best.Count >= 3 ? best : [];
    }

    private static List<TechRow> ParseKeyValueRows(IElement table)
    {
        var rows = new List<TechRow>();
        foreach (var tr in table.QuerySelectorAll("tr"))
        {
            var cells = tr.QuerySelectorAll("td, th").Select(StripText).Where(c => c.Length > 0).ToList();
            if (cells.Count < 2)
            {
                continue;
            }

            var property = cells[^2];
            if (property.Equals("technical data", StringComparison.OrdinalIgnoreCase)
                || OrderNumberLike.IsMatch(property))
            {
                // The "Technical data | <order>" header row, or an EtherCAT Box comparison header
                // whose cells are the variant order numbers (e.g. EPP1008-0001 | EPP1018-0001).
                continue;
            }

            rows.Add(new TechRow(property, cells[^1]));
        }

        return rows;
    }

    private static bool IsMarkedTechnicalData(IElement table)
    {
        var firstRow = table.QuerySelector("tr");
        if (firstRow is not null
            && string.Join(" ", firstRow.QuerySelectorAll("td, th").Select(StripText))
                .Contains("technical data", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var caption = table.QuerySelector("caption");
        if (caption is not null && StripText(caption).Contains("technical data", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        for (var sib = table.PreviousElementSibling; sib is not null; sib = sib.PreviousElementSibling)
        {
            if (sib.TagName is "H1" or "H2" or "H3" or "H4" or "H5" or "H6" or "P" or "CAPTION" or "STRONG" or "B")
            {
                return StripText(sib).Contains("technical data", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
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
