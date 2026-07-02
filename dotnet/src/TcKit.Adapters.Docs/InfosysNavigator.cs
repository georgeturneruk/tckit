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
    /// EtherCAT hardware documentation sections (terminals, boxes, P-boxes, plug-ins, couplers,
    /// IO-Link boxes, infrastructure), keyed by Beckhoff's per-section slugs. Enumerated from the
    /// infosys fieldbus menu tree (2026-06) by <c>oracle/regen-hardware-sections.ps1</c>; re-run that
    /// script to refresh when Beckhoff adds products. The slug scheme is irregular: family wildcards
    /// (<c>el30xx</c>, <c>epp622x</c>), exact products (<c>epp3504</c>), underscore groups
    /// (<c>el10xx_el11xx</c>) and hyphenated order-specific slugs (<c>epp7342-0002</c>), so an order
    /// number is matched to a section by <see cref="SectionCoversOrder"/> rather than derived
    /// arithmetically. Used by find_hardware and added to the general search list.
    /// </summary>
    public static readonly IReadOnlyList<string> HardwareSections =
    [
        "ethercatsystem",
        // EtherCAT Terminals (EL / EM / ELM / ED / ES)
        "ed336x-0x00", "ed407x", "el1052_el1054", "el10xx_el11xx", "el1202_el1252", "el125x_el2258",
        "el126x", "el1382", "el1409", "el1417", "el1429", "el1489", "el15xx", "el17x2-00xx", "el18xx",
        "el2044", "el2068", "el20xx_el2124", "el2202_el2252", "el2212", "el2262", "el2407", "el2409",
        "el2489", "el2502", "el2502-0005", "el2502-0010", "el252x", "el2535", "el2564", "el2574",
        "el2595", "el2596", "el26xx", "el27x2", "el27xx", "el2838", "el2869", "el28xx",
        "el3008-00xx", "el30xx", "el318x", "el31xx", "el3204-0162", "el3255", "el32xx", "el331x",
        "el3351", "el3356", "el3403", "el3475", "el34x3", "el34xx", "el3632", "el3681", "el3692",
        "el36xx", "el3751", "el3773", "el3783", "el37x2",
        "el407x", "el40xx", "el41xx", "el4374", "el47xx",
        "el500x", "el5021", "el5031-0011", "el5032", "el5042", "el5072", "el5101", "el5102", "el5112",
        "el5122", "el5131", "el515x",
        "el600x_el602x", "el6070", "el6080", "el6090", "el6184", "el6201", "el6224", "el6233",
        "el6601_el6614", "el6631-0010", "el6631_el6632", "el6633", "el6633-0010", "el6652", "el6685",
        "el6688", "el6692", "el6695", "el6711-0010", "el6720", "el6731", "el6740-0010", "el6751",
        "el6752", "el6761", "el6821", "el6851", "el6861",
        "el7031-0030", "el7062", "el70x1", "el70x7", "el72x1", "el72x1-001x", "el72x1-901x", "el73x2",
        "el7411", "el8601-8411", "el922x",
        "el9501_el9561", "el9562", "el9576", "el95xx", "el9xxx",
        "elm2xxx", "elm3xxx", "elm72xx", "em2042", "em37xx", "em7004",
        // EtherCAT couplers / infrastructure (EK / EKM)
        "ek1000", "ek110x_ek15xx", "ek1110", "ek1110-004x", "ek112x_ek15xx", "ek1300", "ek1310",
        "ek1322", "ek18xx", "ek3100", "ek9000", "ek9160", "ek9300", "ek9320", "ek9500", "ekm1101",
        // EtherCAT Box (EP) + rugged (ER) + 24 V box (EQ)
        "ep1111-0000", "ep1122", "ep1258", "ep1312-0001", "ep1518", "ep1xxx", "ep20xx_ep28xx",
        "ep23xx", "ep2534-0002", "ep2624-0002", "ep3048-0002", "ep31xx", "ep3204_ep3314", "ep3356",
        "ep3632", "ep3744", "ep3751-0160", "ep3751-0260", "ep3752", "ep4174", "ep4374", "ep4378",
        "ep43x4-1002", "ep5xxx", "ep6001", "ep6002", "ep6070-0060", "ep6080-0000", "ep6090-0000",
        "ep6224_ep6228", "ep6601-0002", "ep7041", "ep7041-4032", "ep7047-1032", "ep7211",
        "ep7342-0002", "ep7402", "ep7412-x071", "ep7414-x071", "ep8309-1022", "ep8601-0022", "ep9128",
        "ep9208-1035", "ep9224-0037", "ep9224-2037", "ep92x4-002x", "ep9300", "ep9521", "ep9576-1032",
        "erxxxx", "eqxxxx",
        // EtherCAT P Box (EPP)
        "epp1111", "epp1321-0060", "epp1322", "epp1332_epp1342", "epp1518-0002", "epp1xxx", "epp2xxx",
        "epp31xx", "epp3204", "epp3314-0002", "epp3356-0022", "epp3504", "epp3632", "epp3744",
        "epp3752-0000", "epp4174-0002", "epp4314-1002", "epp4374", "epp5xxx", "epp6001", "epp6002",
        "epp6090-0000", "epp622x", "epp7041-x002", "epp7342-0002", "epp9001", "epp9022-0060",
        "epp9022-9060", "eppxxxx-x7xx",
        // EtherCAT plug-in modules (EJ)
        "ej1008", "ej110x-00xx", "ej1122", "ej1128", "ej1254", "ej18xx", "ej2008", "ej2128", "ej2262",
        "ej2502", "ej252x", "ej2564", "ej28x9", "ej30xx", "ej3114-0010", "ej3124-0090", "ej31xx",
        "ej3255", "ej32xx", "ej3314-0090", "ej3318", "ej40xx", "ej41xx", "ej500x", "ej5021", "ej5042",
        "ej5101", "ej5112", "ej515x", "ej600x", "ej6070", "ej6080", "ej6224", "ej70x1", "ej70x7",
        "ej72x1-001x", "ej7334-0008", "ej73x2", "ej7411", "ej8906-0005", "ej8xxx", "ej9001", "ej9400",
        "ej9404", "ej9505", "ej9576",
        // IO-Link box modules (EPI / ERI)
        "epi1xxx", "epi2xxx", "epi3xxx", "epi4xxx_eri4xxx",
        // Infrastructure / switches (CU)
        "cu112x", "cu20xx_cu22xx", "cu2508", "cu2508-0022", "cu2608",
    ];

    /// <summary>
    /// True when a section slug covers an order number. Each underscore-separated slug part is turned
    /// into an anchored pattern with <c>x</c> as a single-digit wildcard (so <c>el30xx</c> matches
    /// EL3001..EL3099, <c>el5101</c> matches only EL5101, <c>el34x3</c> matches EL34_3). A variant
    /// suffix after the first hyphen is dropped from the slug part before matching (so the
    /// order-specific slug <c>epp7342-0002</c> covers the bare order EPP7342), mirroring the
    /// order-number normalisation in <see cref="BeckhoffInfosysSearcher.FindHardwareAsync"/> which
    /// strips the same suffix. The order number must already be upper-cased and stripped of any suffix.
    /// </summary>
    public static bool SectionCoversOrder(string slug, string order)
    {
        foreach (var rawPart in slug.Split('_'))
        {
            // Drop the variant suffix (e.g. "ep3751-0160" -> "ep3751"); the bare order has the same
            // suffix stripped, so matching on the base resolves both family and order-specific slugs.
            var part = rawPart.Split('-')[0];
            if (part.Length == 0)
            {
                continue;
            }

            var pattern = new System.Text.StringBuilder("^");
            foreach (var ch in part)
            {
                pattern.Append(ch is 'x' or 'X'
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
    /// In-section menu nodes as (Url, Title, PrimaryId), keeping the <c>?id=</c> each menu href carries
    /// so the tree can be walked node-by-node. Like <see cref="GetMenuLinksAsync"/> but retains the id
    /// and skips the section's own <c>index.html</c> (the group landing).
    /// </summary>
    public static async Task<IReadOnlyList<(string Url, string Title, string Id)>> GetMenuNodesAsync(
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
        var nodes = new List<(string Url, string Title, string Id)>();

        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href") ?? "";
            var title = NormaliseText(a.TextContent);
            var nodeId = ExtractIdParam(href);
            var clean = StripId(href);
            if (title.Length == 0 || nodeId is null
                || !clean.Contains(sectionBase, StringComparison.Ordinal)
                || clean.EndsWith("/index.html", StringComparison.Ordinal))
            {
                continue;
            }

            nodes.Add((ToAbsolute(clean, InfosysHost), title, nodeId));
        }

        return nodes;
    }

    /// <summary>
    /// Walk a section's menu.php tree (bounded by fetch count and depth) to find the product node whose
    /// title names the order number AND that owns a "Technical data" child, returning its (url,
    /// primaryId). Many sections nest the product several levels below the overview (couplers, boxes:
    /// overview -&gt; product overview -&gt; connection type -&gt; "&lt;order&gt;" -&gt; "Technical data"),
    /// which a single overview scan misses. Requiring the Technical-data child avoids matching an
    /// order-named aspect page (e.g. a "Diagnostic LEDs &gt; EK1100" node) that carries no table. The
    /// product-overview branch is searched first so the right node is reached in a few fetches; an
    /// order-named node without a Technical-data child is kept only as a fallback.
    /// </summary>
    public static async Task<(string Url, string PrimaryId)?> FindOrderNodeAsync(
        IInfosysClient client,
        string section,
        string indexPrimaryId,
        string order,
        TimeSpan politeDelay,
        CancellationToken cancellationToken)
    {
        // The deepest legitimate nesting seen (couplers: overview -> product overview -> connection
        // type -> order -> technical data) resolves in ~10 fetches; the cap bounds the wasted walk for
        // pure catch-all sections (erxxxx, eqxxxx) that have no per-order page at all.
        const int maxFetches = 40;
        const int maxDepth = 8;

        var visited = new HashSet<string>(StringComparer.Ordinal) { "index.html" };
        // Two-tier queue: nodes inside the product-listing branch are searched before reference
        // material (Foreword, Basics, Mounting, Diagnostics, Appendix), so the product node is found
        // quickly and within budget.
        var priority = new Queue<(string Url, string File, string Id, int Depth, bool OrderNode, bool InProduct)>();
        var normal = new Queue<(string Url, string File, string Id, int Depth, bool OrderNode, bool InProduct)>();
        normal.Enqueue(("", "index.html", indexPrimaryId, 0, false, false));
        (string Url, string PrimaryId)? fallback = null;
        var fetches = 0;

        while ((priority.Count > 0 || normal.Count > 0) && fetches < maxFetches)
        {
            var (url, file, id, depth, orderNode, inProduct) =
                priority.Count > 0 ? priority.Dequeue() : normal.Dequeue();
            if (depth > maxDepth)
            {
                continue;
            }

            fetches++;
            var nodes = await GetMenuNodesAsync(client, section, file, id, cancellationToken).ConfigureAwait(false);
            await DelayAsync(politeDelay, cancellationToken).ConfigureAwait(false);

            // The node being expanded is the order's product node when its title named the order and it
            // owns a "Technical data" child: that is the page whose Technical data we can resolve.
            if (orderNode && nodes.Any(n => n.Title.Contains("technical data", StringComparison.OrdinalIgnoreCase)))
            {
                return (url, id);
            }

            foreach (var (childUrl, title, childId) in nodes)
            {
                var token = title.Split(',', ' ', '|', '/')[0].Trim();
                var isOrder = token.Split('-')[0].Equals(order, StringComparison.OrdinalIgnoreCase);
                if (isOrder)
                {
                    fallback ??= (childUrl, childId);
                }

                var childFile = FileName(childUrl);
                if (!visited.Add(childFile))
                {
                    continue;
                }

                // Stay in (or enter) the product-listing branch: "Product overview" and everything
                // under it, plus order/family-named nodes, are the producty path to the table.
                var childInProduct = inProduct
                    || title.Contains("overview", StringComparison.OrdinalIgnoreCase)
                    || isOrder
                    || SectionCoversOrder(token, order);
                var item = (childUrl, childFile, childId, depth + 1, isOrder, childInProduct);
                if (childInProduct)
                {
                    priority.Enqueue(item);
                }
                else
                {
                    normal.Enqueue(item);
                }
            }
        }

        return fallback;
    }

    /// <summary>A pending page to visit during a section crawl.</summary>
    public sealed record CrawlNode(string Url, int Depth);

    /// <summary>
    /// Resumable state of a section crawl: the {title_lower -&gt; url} index built so far, the pending
    /// frontier, the set of visited URLs, and whether the crawl ran to completion. Persisting this lets
    /// a crawl that is interrupted (budget hit, process killed) resume instead of restarting.
    /// </summary>
    public sealed record CrawlState(
        Dictionary<string, string> Pages,
        List<CrawlNode> Queue,
        HashSet<string> Visited,
        bool Complete);

    private const int CrawlMaxDepth = 6;
    private const int CrawlPersistEvery = 20;

    /// <summary>
    /// Build a {title_lower -&gt; url} index for a section by crawling its tree (depth-limited). Runs to
    /// completion; used by tests and the non-budgeted path. Prefer <see cref="CrawlSectionAsync"/>.
    /// </summary>
    public static async Task<Dictionary<string, string>> BuildSectionIndexAsync(
        IInfosysClient client,
        string section,
        TimeSpan politeDelay,
        CancellationToken cancellationToken)
    {
        var state = await CrawlSectionAsync(
            client, section, resume: null, politeDelay, shouldStop: static () => false, onProgress: null,
            cancellationToken).ConfigureAwait(false);
        return state?.Pages ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Crawl a section's tree into a {title_lower -&gt; url} index, resumably and cooperatively bounded.
    /// Starts fresh when <paramref name="resume"/> is null, otherwise continues from the saved frontier.
    /// Stops early (returning <see cref="CrawlState.Complete"/> = false) when <paramref name="shouldStop"/>
    /// returns true; <paramref name="onProgress"/> is invoked every <see cref="CrawlPersistEvery"/> pages
    /// so the caller can persist partial progress. Returns null only when a fresh crawl finds no section
    /// primaryid (section unreachable / does not exist).
    /// </summary>
    public static async Task<CrawlState?> CrawlSectionAsync(
        IInfosysClient client,
        string section,
        CrawlState? resume,
        TimeSpan politeDelay,
        Func<bool> shouldStop,
        Action<CrawlState>? onProgress,
        CancellationToken cancellationToken)
    {
        var sectionBase = $"{InfosysHost}/content/1033/{section}/";
        var indexUrl = sectionBase + "index.html";

        Dictionary<string, string> index;
        Queue<CrawlNode> queue;
        HashSet<string> visited;

        if (resume is { Complete: false })
        {
            index = resume.Pages;
            queue = new Queue<CrawlNode>(resume.Queue);
            visited = resume.Visited;
        }
        else
        {
            var primaryId = await GetSectionPrimaryIdAsync(client, section, cancellationToken).ConfigureAwait(false);
            if (primaryId is null)
            {
                return null;
            }

            await DelayAsync(politeDelay, cancellationToken).ConfigureAwait(false);
            var topLinks = await GetMenuLinksAsync(client, section, "index.html", primaryId, cancellationToken)
                .ConfigureAwait(false);

            index = new Dictionary<string, string>(StringComparer.Ordinal);
            visited = new HashSet<string>(StringComparer.Ordinal) { indexUrl };
            queue = new Queue<CrawlNode>(topLinks.Select(l => new CrawlNode(l.Url, 0)));

            // Seed the index with menu titles (no extra fetch).
            foreach (var (url, title) in topLinks)
            {
                index[title.ToLowerInvariant()] = url;
            }
        }

        var sincePersist = 0;
        while (queue.Count > 0)
        {
            if (shouldStop())
            {
                // Budget exhausted: hand back the partial, resumable state.
                return new CrawlState(index, [.. queue], visited, Complete: false);
            }

            var (url, depth) = queue.Dequeue();
            if (!visited.Add(url) || depth > CrawlMaxDepth)
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
                if (href.Length == 0 || childTitle.Length == 0
                    || !href.EndsWith(".html", StringComparison.Ordinal)
                    || (href.StartsWith("http", StringComparison.Ordinal)
                        && !href.Contains(sectionBase, StringComparison.Ordinal)))
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
                    queue.Enqueue(new CrawlNode(cleanUrl, depth + 1));
                }
            }

            if (++sincePersist >= CrawlPersistEvery && onProgress is not null)
            {
                sincePersist = 0;
                onProgress(new CrawlState(index, [.. queue], visited, Complete: false));
            }
        }

        return new CrawlState(index, [], visited, Complete: true);
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

    /// <summary>The value of the <c>id=</c> query parameter of a menu href, or null if absent.</summary>
    private static string? ExtractIdParam(string href)
    {
        var match = System.Text.RegularExpressions.Regex.Match(href, @"[?&]id=(\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>The file name (no query) of a URL, e.g. ".../7635633163.html?id=1" -&gt; "7635633163.html".</summary>
    private static string FileName(string url) => StripId(url[(url.LastIndexOf('/') + 1)..]);

    internal static string ToAbsolute(string href, string baseUrl) =>
        href.StartsWith("http", StringComparison.Ordinal)
            ? href
            : new Uri(new Uri(baseUrl), href).ToString();

    private static Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay > TimeSpan.Zero ? Task.Delay(delay, cancellationToken) : Task.CompletedTask;
}
