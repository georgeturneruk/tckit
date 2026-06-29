using AngleSharp.Dom;

namespace TcKit.Adapters.Docs;

/// <summary>
/// Navigate Beckhoff infosys without any external search. Uses infosys's own <c>menu.php</c> tree to
/// build a {title -&gt; URL} index for a documentation section; the index is then cached by the
/// searcher so later lookups are local. Mirrors the Python <c>_infosys_navigator</c> module.
///
/// Per section: fetch <c>index.html</c> for its <c>&lt;meta primaryid&gt;</c>, fetch
/// <c>menu.php?...&amp;id=&lt;primaryid&gt;</c> for the top-level page list, then crawl those pages
/// (depth-limited) collecting every in-section child link.
/// </summary>
internal static class InfosysNavigator
{
    public const string InfosysHost = "https://infosys.beckhoff.com";
    private const string MenuUrl = $"{InfosysHost}/english/menu/menu.php";

    /// <summary>
    /// PLC-library / TF / documentation sections searched in order of likelihood for find_fb() and
    /// search_docs(). Ordered most-common first so a find_fb hit short-circuits early. The library set
    /// was sourced from the infosys PLC-libraries menu tree (2026-06) plus the motion libraries (which
    /// live under a separate menu branch).
    /// </summary>
    public static readonly IReadOnlyList<string> KnownSections =
    [
        // Standard / utility PLC libraries
        "tcplclib_tc2_standard",
        "tcplclib_tc2_utilities",
        "tcplclib_tc2_math",
        "tcplclib_tc3_math",
        "tcplclib_tc3_string",
        "tcplclib_tc2_system",
        "tcplclib_tc2_iofunctions",
        // Motion / drives
        "tcplclib_tc2_mc2",
        "tcplclib_tc2_mc2_drive",
        "tcplclib_tc2_drive",
        // Fieldbus / communication
        "tcplclib_tc2_ethercat",
        "tcplclib_tc3_ethercatdiag",
        "tcplclib_tc2_dataexchange",
        "tcplclib_tc2_coupler",
        "tcplclib_tc2_mdp",
        "tcplclib_tc2_mpbus",
        "tcplclib_tc2_mbus",
        "tcplclib_tc2_genibus",
        "tcplclib_tc2_profinetdiag",
        // IO-Link
        "tcplclib_tc3_iolink",
        // System / utility (TC3)
        "tcplclib_tc3_eventlogger",
        "tcplclib_tc3_jsonxml",
        "tcplclib_tc3_dynamicmemory",
        "tcplclib_tc3_module",
        "tcplclib_tc3_ipcdiag",
        "tcplclib_tc2_sups",
        "tcplclib_tc2_systemcx",
        "tcplclib_tc2_systemc69xx",
        // Building automation
        "tcplclib_tc3_ba_common",
        "tcplclib_tc3_ba2_common",
        "tcplclib_tc2_dali",
        "tcplclib_tc3_dali",
        "tcplclib_tc2_dmx",
        "tcplclib_tc2_eib",
        "tcplclib_tc2_enocean",
        "tcplclib_tc2_lon",
        "tcplclib_tc2_smi",
        // TwinCAT Functions
        "tf6310_tc3_tcpip",
        "tf6100_tc3_opcua",
        "tf6300_tc3_tcp",
        "tf6xxx_tc3_ads",
        // Documentation
        "tc3_automationinterface",
        "tc3_plc_intro",
        "tc3_ads_intro",
    ];

    /// <summary>
    /// EtherCAT terminal / box / measurement documentation sections, keyed by Beckhoff's series-group
    /// slugs. Sourced from the infosys EtherCAT-terminals menu tree (2026-06); the scheme is irregular
    /// (e.g. <c>el30xx</c>, <c>el10xx_el11xx</c>, <c>el125x_el2258</c>), so an order number is matched
    /// to a section by <see cref="SectionCoversOrder"/> rather than derived arithmetically. Used by
    /// find_hardware and added to the general search list.
    /// </summary>
    public static readonly IReadOnlyList<string> HardwareSections =
    [
        "ethercatsystem", "ep1xxx",
        // EtherCAT P boxes (EPP)
        "epp1xxx", "epp2xxx", "epp31xx", "epp5xxx",
        "el1052_el1054", "el10xx_el11xx", "el1202_el1252", "el125x_el2258", "el126x", "el1382",
        "el1409", "el1417", "el1429", "el1489", "el15xx", "el18xx",
        "el2044", "el2068", "el20xx_el2124", "el2202_el2252", "el2212", "el2262", "el2407", "el2409",
        "el2489", "el2502", "el252x", "el2535", "el2564", "el2574", "el2595", "el2596", "el26xx",
        "el27x2", "el27xx", "el2838", "el2869", "el28xx",
        "el30xx", "el318x", "el31xx", "el3255", "el32xx", "el331x", "el3351", "el3356", "el3403",
        "el3475", "el34x3", "el34xx", "el3632", "el3681", "el3692", "el36xx", "el3751", "el3773",
        "el3783", "el37x2",
        "el407x", "el40xx", "el41xx", "el4374", "el47xx",
        "el500x", "el5021", "el5032", "el5042", "el5072", "el5101", "el5102", "el5112", "el5122",
        "el5131", "el515x",
        "el600x_el602x", "el6070", "el6080", "el6090", "el6184", "el6201", "el6224", "el6233",
        "el6601_el6614", "el6631_el6632", "el6633", "el6652", "el6685", "el6688", "el6692", "el6695",
        "el6720", "el6731", "el6751", "el6752", "el6761", "el6821", "el6851", "el6861",
        "el7062", "el70x1", "el70x7", "el72x1", "el73x2", "el7411", "el922x",
        "el9501_el9561", "el9562", "el9576", "el95xx", "el9xxx",
        "elm2xxx", "elm3xxx", "elm72xx", "em2042", "em37xx", "em7004",
    ];

    /// <summary>
    /// True when a section slug covers an order number. Each underscore-separated slug part is turned
    /// into an anchored pattern with <c>x</c> as a single-digit wildcard (so <c>el30xx</c> matches
    /// EL3001..EL3099, <c>el5101</c> matches only EL5101, <c>el34x3</c> matches EL34_3). The order
    /// number must already be upper-cased and stripped of any suffix.
    /// </summary>
    public static bool SectionCoversOrder(string slug, string order)
    {
        foreach (var part in slug.Split('_'))
        {
            var pattern = new System.Text.StringBuilder("^");
            foreach (var ch in part)
            {
                pattern.Append(ch == 'x'
                    ? "\\d"
                    : System.Text.RegularExpressions.Regex.Escape(char.ToUpperInvariant(ch).ToString()));
            }

            pattern.Append('$');
            if (System.Text.RegularExpressions.Regex.IsMatch(order, pattern.ToString()))
            {
                return true;
            }
        }

        return false;
    }

    public static async Task<string?> GetSectionPrimaryIdAsync(
        IInfosysClient client, string section, CancellationToken cancellationToken)
    {
        var html = await client.GetAsync(
            $"{InfosysHost}/content/1033/{section}/index.html", cancellationToken).ConfigureAwait(false);
        if (html is null)
        {
            return null;
        }

        using var doc = InfosysParser.Parse(html);
        var id = doc.QuerySelector("meta[name=primaryid]")?.GetAttribute("content");
        return string.IsNullOrEmpty(id) ? null : id;
    }

    public static async Task<IReadOnlyList<(string Url, string Title)>> GetMenuLinksAsync(
        IInfosysClient client, string section, string contentFile, string primaryId, CancellationToken cancellationToken)
    {
        var content = Uri.EscapeDataString($"../content/1033/{section}/{contentFile}");
        var id = Uri.EscapeDataString(primaryId);
        var html = await client.GetAsync($"{MenuUrl}?content={content}&id={id}", cancellationToken)
            .ConfigureAwait(false);
        if (html is null)
        {
            return [];
        }

        using var doc = InfosysParser.Parse(html);
        var sectionBase = $"/content/1033/{section}/";
        var links = new List<(string Url, string Title)>();

        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href") ?? "";
            var title = NormaliseText(a.TextContent);
            if (title.Length == 0)
            {
                continue;
            }

            var clean = StripId(href);
            if (clean.Contains(sectionBase, StringComparison.Ordinal)
                && !clean.EndsWith("/index.html", StringComparison.Ordinal))
            {
                links.Add((ToAbsolute(clean, InfosysHost), title));
            }
        }

        return links;
    }

    /// <summary>
    /// Build a {title_lower -&gt; url} index for a section by crawling its tree. Depth-limited to 6
    /// levels; <paramref name="politeDelay"/> spaces out requests.
    /// </summary>
    public static async Task<Dictionary<string, string>> BuildSectionIndexAsync(
        IInfosysClient client,
        string section,
        TimeSpan politeDelay,
        CancellationToken cancellationToken)
    {
        var primaryId = await GetSectionPrimaryIdAsync(client, section, cancellationToken).ConfigureAwait(false);
        if (primaryId is null)
        {
            return [];
        }

        await DelayAsync(politeDelay, cancellationToken).ConfigureAwait(false);
        var topLinks = await GetMenuLinksAsync(client, section, "index.html", primaryId, cancellationToken)
            .ConfigureAwait(false);

        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        var sectionBase = $"{InfosysHost}/content/1033/{section}/";
        var indexUrl = sectionBase + "index.html";

        var visited = new HashSet<string>(StringComparer.Ordinal) { indexUrl };
        var queue = new Queue<(string Url, int Depth)>(topLinks.Select(l => (l.Url, 0)));

        // Seed the index with menu titles (no extra fetch).
        foreach (var (url, title) in topLinks)
        {
            index[title.ToLowerInvariant()] = url;
        }

        while (queue.Count > 0)
        {
            var (url, depth) = queue.Dequeue();
            if (!visited.Add(url) || depth > 6)
            {
                continue;
            }

            await DelayAsync(politeDelay, cancellationToken).ConfigureAwait(false);
            var html = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (html is null)
            {
                continue;
            }

            using var doc = InfosysParser.Parse(html);

            var title = CleanTitle(doc);
            if (title.Length > 0)
            {
                index[title.ToLowerInvariant()] = url;
            }

            foreach (var a in doc.QuerySelectorAll("a[href]"))
            {
                var href = a.GetAttribute("href") ?? "";
                var childTitle = NormaliseText(a.TextContent);
                if (href.Length == 0 || childTitle.Length == 0)
                {
                    continue;
                }

                if (!href.EndsWith(".html", StringComparison.Ordinal))
                {
                    continue;
                }

                if (href.StartsWith("http", StringComparison.Ordinal)
                    && !href.Contains(sectionBase, StringComparison.Ordinal))
                {
                    continue;
                }

                var absUrl = ToAbsolute(href, url);
                if (!absUrl.Contains(sectionBase, StringComparison.Ordinal) || absUrl == indexUrl)
                {
                    continue;
                }

                var cleanUrl = StripId(absUrl);
                if (!visited.Contains(cleanUrl))
                {
                    index[childTitle.ToLowerInvariant()] = cleanUrl;
                    queue.Enqueue((cleanUrl, depth + 1));
                }
            }
        }

        return index;
    }

    /// <summary>
    /// Search a section index for pages matching a query. Exact title matches sort first, then
    /// substring matches in index order. Returns (title, url) pairs.
    /// </summary>
    public static IReadOnlyList<(string Title, string Url)> SearchIndex(
        IReadOnlyDictionary<string, string> index, string query, bool exact = false)
    {
        var q = query.ToLowerInvariant();
        var results = new List<(string Title, string Url)>();

        foreach (var (title, url) in index)
        {
            if (exact)
            {
                if (title == q)
                {
                    results.Insert(0, (title, url));
                }
            }
            else if (title == q)
            {
                results.Insert(0, (title, url)); // exact match first
            }
            else if (title.Contains(q, StringComparison.Ordinal))
            {
                results.Add((title, url));
            }
        }

        return results;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string CleanTitle(IDocument doc)
    {
        var title = doc.QuerySelector("title")?.TextContent.Trim() ?? "";
        foreach (var suffix in new[] { " - Beckhoff Automation", " | Beckhoff", " - TwinCAT" })
        {
            if (title.EndsWith(suffix, StringComparison.Ordinal))
            {
                title = title[..^suffix.Length];
            }
        }

        return title.Trim();
    }

    private static string NormaliseText(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    private static string StripId(string url)
    {
        var q = url.IndexOf('?', StringComparison.Ordinal);
        return q >= 0 ? url[..q] : url;
    }

    internal static string ToAbsolute(string href, string baseUrl) =>
        href.StartsWith("http", StringComparison.Ordinal)
            ? href
            : new Uri(new Uri(baseUrl), href).ToString();

    private static Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay > TimeSpan.Zero ? Task.Delay(delay, cancellationToken) : Task.CompletedTask;
}
