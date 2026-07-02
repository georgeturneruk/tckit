using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TcKit.Core.Models;
using TcKit.Core.Ports;

namespace TcKit.Adapters.Docs;

/// <summary>
/// <see cref="IDocsSearcher"/> against infosys.beckhoff.com. Builds section indexes by walking
/// infosys's own menu.php tree (no external search, no API keys, no rate limiting) and caches both
/// the section indexes and fetched pages to disk; later lookups are local reads. Port of the Python
/// <c>BeckhoffInfosysSearcher</c>.
/// </summary>
public sealed class BeckhoffInfosysSearcher : IDocsSearcher
{
    private static readonly JsonSerializerOptions CacheJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly string[] FbPrefixes = ["FB_", "FC_", "FUN_", "STLB_", "ST_", "E_", "F_"];

    private readonly IInfosysClient _client;
    private readonly string _cacheDir;
    private readonly TimeSpan _politeDelay;
    private readonly TimeSpan _findFbBudget;

    /// <summary>
    /// Production constructor: live HTTP client. The per-page crawl delay (default 100 ms) and the
    /// overall FindFb time budget (default 45 s, kept under a typical MCP tool timeout) are overridable
    /// via the TCKIT_INFOSYS_DELAY_MS and TCKIT_FINDFB_BUDGET_SECONDS environment variables.
    /// </summary>
    public BeckhoffInfosysSearcher(string? cachePath = null)
        : this(new HttpInfosysClient(), cachePath ?? DefaultCachePath(),
            TimeSpan.FromMilliseconds(EnvInt("TCKIT_INFOSYS_DELAY_MS", 100)))
    {
    }

    /// <summary>Seam constructor for tests: injected client, crawl delay, and optional FindFb budget.</summary>
    internal BeckhoffInfosysSearcher(
        IInfosysClient client, string cachePath, TimeSpan politeDelay, TimeSpan? findFbBudget = null)
    {
        _client = client;
        _cacheDir = cachePath;
        _politeDelay = politeDelay;
        _findFbBudget = findFbBudget ?? TimeSpan.FromSeconds(EnvInt("TCKIT_FINDFB_BUDGET_SECONDS", 45));
    }

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    // -----------------------------------------------------------------------
    // IDocsSearcher
    // -----------------------------------------------------------------------

    public async Task<DocPage> GetPageAsync(string url, CancellationToken cancellationToken)
    {
        url = NormaliseUrl(url);

        var cached = LoadPageCache(url);
        if (cached is not null)
        {
            return new DocPage(cached.Url, cached.Title, cached.Content, Cached: true);
        }

        var html = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException($"Failed to fetch infosys page: {url}");

        using var doc = InfosysParser.Parse(html);
        var title = InfosysParser.ExtractTitle(doc);
        var content = InfosysParser.ExtractMainContent(doc);

        SavePageCache(url, title, content);
        return new DocPage(url, title, content, Cached: false);
    }

    public async Task<SearchResults> SearchAsync(string query, string? section, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> sections = string.IsNullOrEmpty(section)
            ? [.. InfosysNavigator.KnownSections, .. InfosysNavigator.HardwareSections]
            : [section];
        var results = new List<SearchResult>();

        foreach (var sec in sections)
        {
            var index = LoadSectionIndex(sec);
            if (index is null)
            {
                continue;
            }

            foreach (var (_, url) in InfosysNavigator.SearchIndex(index, query).Take(3))
            {
                try
                {
                    var page = await GetPageAsync(url, cancellationToken).ConfigureAwait(false);
                    if (page.Title.Length > 0 && !page.Title.Contains("Information System", StringComparison.Ordinal))
                    {
                        var snippet = Truncate(page.Content, 200).Replace("\n", " ", StringComparison.Ordinal);
                        results.Add(new SearchResult(page.Title, url, snippet));
                    }
                }
#pragma warning disable CA1031 // A single page failing must not abort the whole search.
                catch (HttpRequestException)
                {
                    // Skip unreachable pages, mirroring the Python adapter.
                }
#pragma warning restore CA1031
            }

            if (results.Count >= 5)
            {
                break;
            }
        }

        return new SearchResults { Query = query, Results = results };
    }

    public async Task<FbDoc> FindFbAsync(string fbName, CancellationToken cancellationToken)
    {
        // Bound the whole search so it returns an informative result before the MCP client's tool
        // timeout kills it. Each section persists its crawl progress, so a retry resumes rather than
        // restarting.
        var budget = System.Diagnostics.Stopwatch.StartNew();
        foreach (var section in InfosysNavigator.KnownSections)
        {
            var url = await FindInSectionAsync(section, fbName, budget, _findFbBudget, cancellationToken)
                .ConfigureAwait(false);
            if (url is null)
            {
                if (budget.Elapsed >= _findFbBudget)
                {
                    break;
                }

                continue;
            }

            var html = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (html is null)
            {
                continue;
            }

            using var doc = InfosysParser.Parse(html);
            var title = InfosysParser.ExtractTitle(doc);
            if (title.Length == 0 || title.Contains("Information System", StringComparison.Ordinal))
            {
                continue;
            }

            var description = InfosysParser.ExtractDescription(doc);
            var content = InfosysParser.ExtractMainContent(doc);
            SavePageCache(url, title, content);

            var rows = InfosysParser.ExtractParameterTable(doc);
            var inputs = rows
                .Where(r => r.Direction.ToLowerInvariant() is "input" or "in" or "var_input" or "")
                .Select(ToParameterDoc)
                .ToList();
            var outputs = rows
                .Where(r => r.Direction.ToLowerInvariant() is "output" or "out" or "var_output")
                .Select(ToParameterDoc)
                .ToList();

            return new FbDoc
            {
                Name = fbName,
                Description = description,
                Url = url,
                Inputs = inputs,
                Outputs = outputs,
            };
        }

        if (budget.Elapsed >= _findFbBudget)
        {
            throw new TimeoutException(
                $"'{fbName}' not found yet: infosys indexing hit the {_findFbBudget.TotalSeconds:N0}s budget before "
                + "finishing. The partial index was saved to disk, so running FindFb again with the same name resumes "
                + "where it left off (each retry indexes more and caches it). To index more per call, raise "
                + "TCKIT_FINDFB_BUDGET_SECONDS.");
        }

        throw new FileNotFoundException(
            $"Could not find infosys page for '{fbName}'. Searched all {InfosysNavigator.KnownSections.Count} known "
            + "sections (now fully indexed, so this was fast). Check the exact spelling — Beckhoff names can differ "
            + "from expectation (e.g. 'FB_IolRead', not 'FB_IoLinkRead'). Or fetch a known URL with GetDocPage.");
    }

    public async Task<LibraryDoc> FindLibraryAsync(string libraryName, CancellationToken cancellationToken)
    {
        var budget = System.Diagnostics.Stopwatch.StartNew();
        foreach (var section in InfosysNavigator.KnownSections)
        {
            var url = await FindInSectionAsync(section, libraryName, budget, _findFbBudget, cancellationToken)
                .ConfigureAwait(false);
            if (url is null)
            {
                if (budget.Elapsed >= _findFbBudget)
                {
                    break;
                }

                continue;
            }

            try
            {
                var page = await GetPageAsync(url, cancellationToken).ConfigureAwait(false);
                if (page.Title.Length > 0 && !page.Title.Contains("Information System", StringComparison.Ordinal))
                {
                    return new LibraryDoc
                    {
                        Name = libraryName,
                        Description = Truncate(page.Content, 300),
                        Url = url,
                    };
                }
            }
#pragma warning disable CA1031 // Skip unreachable pages and try the next section.
            catch (HttpRequestException)
            {
            }
#pragma warning restore CA1031
        }

        throw new FileNotFoundException($"Could not find infosys page for library '{libraryName}'.");
    }

    public async Task<HardwareDoc> FindHardwareAsync(string orderNumber, CancellationToken cancellationToken)
    {
        // Normalise like the hardware catalogue: drop any suffix after the first space or hyphen.
        var order = orderNumber.Trim().ToUpperInvariant().Split(' ', '-')[0];

        // Try the most specific section first: a section with fewer 'x' wildcards (e.g. epp3504,
        // epp622x) is preferred over a catch-all (e.g. eppxxxx-x7xx, erxxxx) so the order's own
        // terminal page is found before a broad family page that merely also matches the pattern.
        var candidates = InfosysNavigator.HardwareSections
            .Where(s => InfosysNavigator.SectionCoversOrder(s, order))
            .OrderBy(s => s.Count(c => c == 'x'));

        HardwareDoc? best = null;
        foreach (var section in candidates)
        {
            var primaryId = await InfosysNavigator.GetSectionPrimaryIdAsync(_client, section, cancellationToken)
                .ConfigureAwait(false);
            if (primaryId is null)
            {
                continue;
            }

            // Fast path: the terminal's page is linked (by its order number) from the section's
            // overview page. Works for sections whose overview lists each order inline (most EL/EP/EPP).
            var topLinks = await InfosysNavigator
                .GetMenuLinksAsync(_client, section, "index.html", primaryId, cancellationToken).ConfigureAwait(false);
            var termUrl = await ResolveTerminalPageAsync(order, topLinks, cancellationToken).ConfigureAwait(false);
            if (termUrl is not null)
            {
                var doc = await BuildHardwareDocAsync(section, order, termUrl, cancellationToken).ConfigureAwait(false);
                if (doc is { TechnicalData.Count: > 0 })
                {
                    return doc;
                }

                best ??= doc;
            }

            // Fallback: the product may be nested several menu levels below the overview (couplers,
            // boxes, short-doc pages have frame-shell overviews with no inline order anchors). Walk the
            // menu tree for the order-named node and resolve its Technical data from there.
            var node = await InfosysNavigator
                .FindOrderNodeAsync(_client, section, primaryId, order, _politeDelay, cancellationToken)
                .ConfigureAwait(false);
            if (node is not null && node.Value.Url != termUrl)
            {
                var deep = await BuildHardwareDocAsync(section, order, node.Value.Url, cancellationToken)
                    .ConfigureAwait(false);
                if (deep is { TechnicalData.Count: > 0 })
                {
                    return deep;
                }

                best ??= deep;
            }
        }

        return best ?? throw new FileNotFoundException(
            $"Could not find infosys hardware page for '{orderNumber}'. "
            + "No documented EtherCAT terminal/box section covers that order number.");
    }

    /// <summary>Fetch a resolved terminal/product page, cache it, and resolve its Technical data table
    /// into a <see cref="HardwareDoc"/>. Returns null only when the page itself cannot be fetched.</summary>
    private async Task<HardwareDoc?> BuildHardwareDocAsync(
        string section, string order, string termUrl, CancellationToken cancellationToken)
    {
        var termHtml = await _client.GetAsync(termUrl, cancellationToken).ConfigureAwait(false);
        if (termHtml is null)
        {
            return null;
        }

        using var termDoc = InfosysParser.Parse(termHtml);
        var title = InfosysParser.ExtractTitle(termDoc);
        var description = InfosysParser.ExtractDescription(termDoc);
        SavePageCache(termUrl, title, InfosysParser.ExtractMainContent(termDoc));

        var techData = await ResolveTechnicalDataAsync(
            section, order, termUrl, InfosysParser.ExtractMeta(termDoc, "primaryid"), cancellationToken)
            .ConfigureAwait(false);

        return new HardwareDoc
        {
            Name = order,
            Title = title,
            Description = description,
            Url = termUrl,
            TechnicalData = techData,
        };
    }

    /// <summary>Find the terminal page URL by scanning the section's top pages (overview first) for an
    /// anchor whose text is the order number.</summary>
    private async Task<string?> ResolveTerminalPageAsync(
        string order, IReadOnlyList<(string Url, string Title)> topLinks, CancellationToken cancellationToken)
    {
        foreach (var (url, _) in topLinks.OrderByDescending(
            l => l.Title.Contains("overview", StringComparison.OrdinalIgnoreCase)))
        {
            await DelayAsync(cancellationToken).ConfigureAwait(false);
            var html = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (html is null)
            {
                continue;
            }

            using var doc = InfosysParser.Parse(html);
            var href = InfosysParser.FindLinkByOrder(doc, order);
            if (href is not null)
            {
                return InfosysNavigator.ToAbsolute(href, url);
            }
        }

        return null;
    }

    /// <summary>Expand the terminal page's menu to find its "&lt;order&gt; - Technical data" page and
    /// parse the table. Returns an empty list when the page or table is not found.</summary>
    private async Task<IReadOnlyList<TechnicalDataItem>> ResolveTechnicalDataAsync(
        string section, string order, string termUrl, string? termPrimaryId, CancellationToken cancellationToken)
    {
        if (termPrimaryId is null)
        {
            return [];
        }

        var contentFile = termUrl[(termUrl.LastIndexOf('/') + 1)..].Split('?')[0];
        var siblings = await InfosysNavigator
            .GetMenuLinksAsync(_client, section, contentFile, termPrimaryId, cancellationToken).ConfigureAwait(false);

        var techUrl = ResolveTechnicalDataUrl(siblings, order, termUrl);
        if (techUrl is null)
        {
            return [];
        }

        await DelayAsync(cancellationToken).ConfigureAwait(false);
        var html = await _client.GetAsync(techUrl, cancellationToken).ConfigureAwait(false);
        if (html is null)
        {
            return [];
        }

        using var doc = InfosysParser.Parse(html);
        SavePageCache(techUrl, InfosysParser.ExtractTitle(doc), InfosysParser.ExtractMainContent(doc));
        return InfosysParser.ExtractTechnicalData(doc)
            .Select(t => new TechnicalDataItem(t.Property, t.Value))
            .ToList();
    }

    /// <summary>
    /// Pick the terminal's "Technical data" page from the expanded menu siblings. EL terminals name
    /// the node "&lt;order&gt; - Technical data"; EtherCAT Box modules title it bare ("Technical
    /// data"), so when no node names the order we take the first technical-data node following the
    /// terminal page in tree order (its own subsection), falling back to the first one found.
    /// </summary>
    private static string? ResolveTechnicalDataUrl(
        IReadOnlyList<(string Url, string Title)> siblings, string order, string termUrl)
    {
        bool IsTech((string Url, string Title) s) =>
            s.Title.Contains("technical data", StringComparison.OrdinalIgnoreCase);

        var named = siblings.FirstOrDefault(s => IsTech(s) && s.Title.Contains(order, StringComparison.OrdinalIgnoreCase));
        if (named.Url is not null)
        {
            return named.Url;
        }

        var termIndex = -1;
        for (var i = 0; i < siblings.Count; i++)
        {
            if (siblings[i].Url == termUrl)
            {
                termIndex = i;
                break;
            }
        }

        for (var i = termIndex + 1; i >= 0 && i < siblings.Count; i++)
        {
            if (IsTech(siblings[i]))
            {
                return siblings[i].Url;
            }
        }

        return siblings.FirstOrDefault(IsTech).Url;
    }

    private Task DelayAsync(CancellationToken cancellationToken) =>
        _politeDelay > TimeSpan.Zero ? Task.Delay(_politeDelay, cancellationToken) : Task.CompletedTask;

    // -----------------------------------------------------------------------
    // Section index management
    // -----------------------------------------------------------------------

    private async Task<string?> FindInSectionAsync(
        string section, string name, System.Diagnostics.Stopwatch budget, TimeSpan overall,
        CancellationToken cancellationToken)
    {
        var cache = LoadSectionCache(section);
        if (cache is not null)
        {
            var hit = SearchWithAliases(cache.Pages, name);
            if (hit is not null)
            {
                return hit;
            }

            // A fully-indexed section that does not contain the name: genuinely absent, no re-crawl.
            if (cache.Complete != false)
            {
                return null;
            }
        }

        // Start or resume the crawl, but only while budget remains.
        if (budget.Elapsed >= overall)
        {
            return null;
        }

        var resume = cache is { Complete: false, Queue: not null, Visited: not null }
            ? new InfosysNavigator.CrawlState(
                cache.Pages, cache.Queue, new HashSet<string>(cache.Visited, StringComparer.Ordinal), Complete: false)
            : null;

        var state = await InfosysNavigator.CrawlSectionAsync(
            _client, section, resume, _politeDelay,
            shouldStop: () => budget.Elapsed >= overall,
            onProgress: s => SaveSectionCache(section, s),
            cancellationToken).ConfigureAwait(false);

        if (state is null)
        {
            return null; // section unreachable / no primaryid
        }

        SaveSectionCache(section, state);
        return SearchWithAliases(state.Pages, name);
    }

    private static string? SearchWithAliases(IReadOnlyDictionary<string, string> index, string name)
    {
        var candidates = new List<string> { name };
        foreach (var prefix in FbPrefixes)
        {
            if (name.ToUpperInvariant().StartsWith(prefix, StringComparison.Ordinal))
            {
                candidates.Add(name[prefix.Length..]);
                break;
            }
        }

        foreach (var candidate in candidates)
        {
            var matches = InfosysNavigator.SearchIndex(index, candidate);
            if (matches.Count > 0)
            {
                return matches[0].Url;
            }
        }

        return null;
    }

    private string SectionCachePath(string section) =>
        Path.Combine(_cacheDir, $"section_{Hash(section)}.json");

    private Dictionary<string, string>? LoadSectionIndex(string section)
    {
        var path = SectionCachePath(section);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var file = JsonSerializer.Deserialize<SectionCacheFile>(File.ReadAllText(path), CacheJson);
            return file?.Pages ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception exc) when (exc is JsonException or IOException)
        {
            return null;
        }
    }

    private SectionCacheFile? LoadSectionCache(string section)
    {
        var path = SectionCachePath(section);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SectionCacheFile>(File.ReadAllText(path), CacheJson);
        }
        catch (Exception exc) when (exc is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Persist a section's crawl state. A completed crawl drops the (now-useless) frontier to
    /// keep the file small; an interrupted crawl keeps the frontier + visited set so the next call
    /// resumes instead of restarting.</summary>
    private void SaveSectionCache(string section, InfosysNavigator.CrawlState state)
    {
        Directory.CreateDirectory(_cacheDir);
        var file = new SectionCacheFile(
            section,
            DateTimeOffset.UtcNow.ToString("o"),
            state.Pages,
            state.Complete,
            state.Complete ? null : state.Queue,
            state.Complete ? null : [.. state.Visited]);
        File.WriteAllText(SectionCachePath(section), JsonSerializer.Serialize(file, CacheJson));
    }

    // -----------------------------------------------------------------------
    // Page content cache
    // -----------------------------------------------------------------------

    private string PageCachePath(string url) => Path.Combine(_cacheDir, $"{Hash(url)}.json");

    private PageCacheFile? LoadPageCache(string url)
    {
        var path = PageCachePath(url);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PageCacheFile>(File.ReadAllText(path), CacheJson);
        }
        catch (Exception exc) when (exc is JsonException or IOException)
        {
            return null;
        }
    }

    private void SavePageCache(string url, string title, string content)
    {
        Directory.CreateDirectory(_cacheDir);
        var entry = new PageCacheFile(url, title, content, DateTimeOffset.UtcNow.ToString("o"));
        File.WriteAllText(PageCachePath(url), JsonSerializer.Serialize(entry, CacheJson));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>Convert english.php wrapper URLs to direct content URLs; pass others through.</summary>
    internal static string NormaliseUrl(string url)
    {
        if (!url.Contains("english.php", StringComparison.Ordinal)
            || !url.Contains("content=", StringComparison.Ordinal))
        {
            return url;
        }

        var query = url.Contains('?', StringComparison.Ordinal) ? url[(url.IndexOf('?', StringComparison.Ordinal) + 1)..] : url;
        foreach (var pair in query.Split('&'))
        {
            if (!pair.StartsWith("content=", StringComparison.Ordinal))
            {
                continue;
            }

            var value = Uri.UnescapeDataString(pair["content=".Length..]);
            var contentPath = value.TrimStart('.', '/');
            return $"{InfosysNavigator.InfosysHost}/{contentPath}";
        }

        return url;
    }

    private static ParameterDoc ToParameterDoc(ParamRow r) =>
        new(r.Name, r.Type, r.Direction, r.Description);

    private static string Truncate(string text, int length) =>
        text.Length <= length ? text : text[..length];

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    private static string DefaultCachePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "tckit", "cache", "infosys");

    // Disk-cache shapes. Pages keeps the original {title -> url} contract; Complete/Queue/Visited are
    // added for resumable crawls. Legacy cache files (and any written by the old Python adapter) omit
    // those three, so Complete deserialises to null, which is treated as "complete" — a full crawl.
    private sealed record SectionCacheFile(
        string Section,
        string BuiltAt,
        Dictionary<string, string> Pages,
        bool? Complete = null,
        List<InfosysNavigator.CrawlNode>? Queue = null,
        List<string>? Visited = null);

    private sealed record PageCacheFile(string Url, string Title, string Content, string FetchedAt);
}
