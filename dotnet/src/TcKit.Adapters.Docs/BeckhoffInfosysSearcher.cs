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

    /// <summary>Production constructor: live HTTP client, polite 300 ms crawl delay.</summary>
    public BeckhoffInfosysSearcher(string? cachePath = null)
        : this(new HttpInfosysClient(), cachePath ?? DefaultCachePath(), TimeSpan.FromMilliseconds(300))
    {
    }

    /// <summary>Seam constructor for tests: injected client and (typically zero) crawl delay.</summary>
    internal BeckhoffInfosysSearcher(IInfosysClient client, string cachePath, TimeSpan politeDelay)
    {
        _client = client;
        _cacheDir = cachePath;
        _politeDelay = politeDelay;
    }

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
        foreach (var section in InfosysNavigator.KnownSections)
        {
            var url = await FindInSectionAsync(section, fbName, cancellationToken).ConfigureAwait(false);
            if (url is null)
            {
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

        throw new FileNotFoundException(
            $"Could not find infosys page for '{fbName}'. "
            + $"Searched {InfosysNavigator.KnownSections.Count} section(s). "
            + "Try GetPage() with a known URL, or add the section to the known-sections list.");
    }

    public async Task<LibraryDoc> FindLibraryAsync(string libraryName, CancellationToken cancellationToken)
    {
        foreach (var section in InfosysNavigator.KnownSections)
        {
            var url = await FindInSectionAsync(section, libraryName, cancellationToken).ConfigureAwait(false);
            if (url is null)
            {
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

        foreach (var section in InfosysNavigator.HardwareSections.Where(s => InfosysNavigator.SectionCoversOrder(s, order)))
        {
            var primaryId = await InfosysNavigator.GetSectionPrimaryIdAsync(_client, section, cancellationToken)
                .ConfigureAwait(false);
            if (primaryId is null)
            {
                continue;
            }

            // The terminal's page is linked (by its order number) from the section's overview page.
            var topLinks = await InfosysNavigator
                .GetMenuLinksAsync(_client, section, "index.html", primaryId, cancellationToken).ConfigureAwait(false);
            var termUrl = await ResolveTerminalPageAsync(order, topLinks, cancellationToken).ConfigureAwait(false);
            if (termUrl is null)
            {
                continue;
            }

            var termHtml = await _client.GetAsync(termUrl, cancellationToken).ConfigureAwait(false);
            if (termHtml is null)
            {
                continue;
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

        throw new FileNotFoundException(
            $"Could not find infosys hardware page for '{orderNumber}'. "
            + "No documented EtherCAT terminal/box section covers that order number.");
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
            var href = InfosysParser.FindLinkByText(doc, order);
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

        var techLink = siblings.FirstOrDefault(s =>
            s.Title.Contains("technical data", StringComparison.OrdinalIgnoreCase)
            && s.Title.Contains(order, StringComparison.OrdinalIgnoreCase));
        if (techLink.Url is null)
        {
            return [];
        }

        await DelayAsync(cancellationToken).ConfigureAwait(false);
        var html = await _client.GetAsync(techLink.Url, cancellationToken).ConfigureAwait(false);
        if (html is null)
        {
            return [];
        }

        using var doc = InfosysParser.Parse(html);
        SavePageCache(techLink.Url, InfosysParser.ExtractTitle(doc), InfosysParser.ExtractMainContent(doc));
        return InfosysParser.ExtractTechnicalData(doc)
            .Select(t => new TechnicalDataItem(t.Property, t.Value))
            .ToList();
    }

    private Task DelayAsync(CancellationToken cancellationToken) =>
        _politeDelay > TimeSpan.Zero ? Task.Delay(_politeDelay, cancellationToken) : Task.CompletedTask;

    // -----------------------------------------------------------------------
    // Section index management
    // -----------------------------------------------------------------------

    private async Task<string?> FindInSectionAsync(string section, string name, CancellationToken cancellationToken)
    {
        var index = LoadSectionIndex(section);
        if (index is not null)
        {
            // Cache exists: search it, but never rebuild on a miss.
            return SearchWithAliases(index, name);
        }

        index = await InfosysNavigator
            .BuildSectionIndexAsync(_client, section, _politeDelay, cancellationToken).ConfigureAwait(false);
        if (index.Count > 0)
        {
            SaveSectionIndex(section, index);
            return SearchWithAliases(index, name);
        }

        return null;
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

    private void SaveSectionIndex(string section, Dictionary<string, string> index)
    {
        Directory.CreateDirectory(_cacheDir);
        var file = new SectionCacheFile(section, DateTimeOffset.UtcNow.ToString("o"), index);
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

    // Disk-cache shapes; keys match the Python adapter's JSON so caches are interchangeable.
    private sealed record SectionCacheFile(string Section, string BuiltAt, Dictionary<string, string> Pages);

    private sealed record PageCacheFile(string Url, string Title, string Content, string FetchedAt);
}
