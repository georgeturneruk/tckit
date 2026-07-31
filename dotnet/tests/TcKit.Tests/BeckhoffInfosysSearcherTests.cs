using TcKit.Adapters.Docs;

namespace TcKit.Tests;

/// <summary>
/// The searcher's orchestration over the seam: URL normalisation, page-cache hits, end-to-end
/// FindFb (build index -&gt; fetch -&gt; parse parameters), the not-found contract, and search over a
/// cached index.
/// </summary>
public sealed class BeckhoffInfosysSearcherTests
{
    private const string Host = "https://infosys.beckhoff.com";
    private const string Section = "tcplclib_tc2_standard"; // first of KnownSections
    private static readonly string FbUrl = $"{Host}/content/1033/{Section}/12.html";

    [Fact]
    public void NormaliseUrl_StripsEnglishPhpWrapper()
    {
        var wrapper = $"{Host}/english.php?content=../content/1033/{Section}/index.html&id=";

        var direct = BeckhoffInfosysSearcher.NormaliseUrl(wrapper);

        Assert.Equal($"{Host}/content/1033/{Section}/index.html", direct);
    }

    [Fact]
    public void NormaliseUrl_PassesThroughDirectUrl()
    {
        Assert.Equal(FbUrl, BeckhoffInfosysSearcher.NormaliseUrl(FbUrl));
    }

    [Fact]
    public async Task GetPage_FetchesThenServesFromCache()
    {
        var client = new FakeInfosysClient();
        client.Add(FbUrl, "<html><head><title>FB_MemSet - Beckhoff Infosys</title></head>"
            + "<body><div id=\"content\"><p>Memory set function block.</p></div></body></html>");
        var searcher = NewSearcher(client);

        var first = await searcher.GetPageAsync(FbUrl, default);
        var second = await searcher.GetPageAsync(FbUrl, default);

        Assert.False(first.Cached);
        Assert.Equal("FB_MemSet", first.Title);
        Assert.True(second.Cached);
        Assert.Equal(first.Content, second.Content);
        Assert.Single(client.Requested); // the cache hit did not touch the network
    }

    [Fact]
    public async Task FindFb_BuildsIndexAndParsesParameters()
    {
        var searcher = NewSearcher(FbScenarioClient());

        var doc = await searcher.FindFbAsync("FB_MemSet", default);

        Assert.Equal("FB_MemSet", doc.Name);
        Assert.Equal(FbUrl, doc.Url);
        Assert.Contains("fills a memory area", doc.Description, StringComparison.Ordinal);
        Assert.Equal("pDest", Assert.Single(doc.Inputs).Name);
        Assert.Equal("bOk", Assert.Single(doc.Outputs).Name);
    }

    [Fact]
    public async Task FindFb_NotFound_Throws()
    {
        var searcher = NewSearcher(new FakeInfosysClient());

        await Assert.ThrowsAsync<FileNotFoundException>(() => searcher.FindFbAsync("FB_DoesNotExist", default));
    }

    [Fact]
    public async Task FindFb_BudgetExhausted_ThrowsInformativeTimeout()
    {
        // Zero budget: the crawl never runs, so FindFb reports "still indexing, retry" rather than a
        // definitive not-found — the contract that keeps a real slow crawl under the MCP tool timeout.
        var searcher = new BeckhoffInfosysSearcher(
            FbScenarioClient(), TempCache(), TimeSpan.Zero, findFbBudget: TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => searcher.FindFbAsync("FB_MemSet", default));
        Assert.Contains("resume", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_UsesCachedIndexAfterFindFb()
    {
        var cache = TempCache();
        var searcher = new BeckhoffInfosysSearcher(FbScenarioClient(), cache, TimeSpan.Zero);
        await searcher.FindFbAsync("FB_MemSet", default); // populates section + page caches

        var results = await searcher.SearchAsync("MemSet", null, default);

        Assert.NotEmpty(results.Results);
        Assert.Equal(FbUrl, results.Results[0].Url);
        Assert.Equal("FB_MemSet", results.Results[0].Title);
    }

    [Fact]
    public async Task Search_UnreachableSections_ReturnsEmpty()
    {
        // Every section is unreachable (the fake serves nothing), so the on-demand crawl yields no
        // index and the search comes back empty rather than throwing.
        var searcher = NewSearcher(new FakeInfosysClient());

        var results = await searcher.SearchAsync("MemSet", null, default);

        Assert.Empty(results.Results);
    }

    [Fact]
    public async Task Search_CrawlsUncachedSectionOnDemand()
    {
        // No FindFb primes the cache first: SearchDocs must crawl the section itself and still find the
        // page. This is the cold-cache path that previously returned nothing.
        var searcher = NewSearcher(FbScenarioClient());

        var results = await searcher.SearchAsync("MemSet", Section, default);

        Assert.NotEmpty(results.Results);
        Assert.Equal(FbUrl, results.Results[0].Url);
        Assert.Equal("FB_MemSet", results.Results[0].Title);
    }

    [Fact]
    public async Task FindHardware_NavigatesToTerminalAndParsesTechnicalData()
    {
        var searcher = NewSearcher(HardwareScenarioClient());

        var doc = await searcher.FindHardwareAsync("EL3004", default);

        Assert.Equal("EL3004", doc.Name);
        Assert.Equal("EL3004 - Introduction", doc.Title);
        Assert.Equal($"{HwHost}/content/1033/el30xx/t.html", doc.Url);
        Assert.Contains("analog input terminal", doc.Description, StringComparison.Ordinal);
        Assert.Equal("Number of inputs", doc.TechnicalData[0].Property);
        Assert.Equal("4", doc.TechnicalData[0].Value);
        Assert.Contains(doc.TechnicalData, t => t.Property == "Signal voltage");
    }

    [Fact]
    public async Task FindHardware_NormalisesSuffixedOrderNumber()
    {
        var searcher = NewSearcher(HardwareScenarioClient());

        var doc = await searcher.FindHardwareAsync("EL3004-0000", default);

        Assert.Equal("EL3004", doc.Name);
    }

    [Fact]
    public async Task FindHardware_UncoveredOrderNumber_Throws()
    {
        var searcher = NewSearcher(new FakeInfosysClient());

        await Assert.ThrowsAsync<FileNotFoundException>(() => searcher.FindHardwareAsync("ZZ9999", default));
    }

    // -----------------------------------------------------------------------

    private const string HwHost = "https://infosys.beckhoff.com";

    private static FakeInfosysClient HardwareScenarioClient()
    {
        const string section = "el30xx";
        var client = new FakeInfosysClient();
        client.Add($"{HwHost}/content/1033/{section}/index.html",
            "<html><head><meta name=\"primaryid\" content=\"100\"></head><body></body></html>");
        client.Add(MenuUrl(section, "100"),
            $"<html><body><a href=\"/content/1033/{section}/ov.html?id=1\">Product overview</a></body></html>");
        client.Add($"{HwHost}/content/1033/{section}/ov.html",
            $"<html><body><a href=\"x.html\">EL3001</a><a href=\"t.html\">EL3004</a></body></html>");
        client.Add($"{HwHost}/content/1033/{section}/t.html",
            "<html><head><title>EL3004 - Introduction</title><meta name=\"primaryid\" content=\"200\"></head>"
            + "<body><div id=\"content\"><p>The EL3004 analog input terminal handles four channels.</p></div></body></html>");
        client.Add(MenuUrl(section, "200", "t.html"),
            $"<html><body>"
            + $"<a href=\"/content/1033/{section}/t.html?id=2\">EL3004 - Introduction</a>"
            + $"<a href=\"/content/1033/{section}/td.html?id=9\">EL3004 - Technical data</a>"
            + $"<a href=\"/content/1033/{section}/td1.html?id=8\">EL3001 - Technical data</a>"  // wrong terminal
            + "</body></html>");
        client.Add($"{HwHost}/content/1033/{section}/td.html", """
            <html><head><title>EL3004 - Technical data</title></head><body><table>
              <tr><td>Technical data</td><td>EL3004</td></tr>
              <tr><td>Number of inputs</td><td>4</td></tr>
              <tr><td>Signal voltage</td><td>-10 V ... +10 V</td></tr>
            </table></body></html>
            """);
        return client;
    }

    private static FakeInfosysClient FbScenarioClient()
    {
        var client = new FakeInfosysClient();
        client.Add($"{Host}/content/1033/{Section}/index.html",
            "<html><head><meta name=\"primaryid\" content=\"999\"></head><body></body></html>");
        client.Add(MenuUrl(Section, "999"),
            $"<html><body><a href=\"/content/1033/{Section}/12.html\">FB_MemSet</a></body></html>");
        client.Add(FbUrl, """
            <html><head><title>FB_MemSet - Beckhoff Infosys</title></head><body>
            <div id="content">
              <p>The function block FB_MemSet fills a memory area with a value.</p>
              <h3>VAR_INPUT</h3>
              <table>
                <tr><th>Name</th><th>Type</th><th>Description</th></tr>
                <tr><td>pDest</td><td>PVOID</td><td>destination pointer</td></tr>
              </table>
              <h3>VAR_OUTPUT</h3>
              <table>
                <tr><th>Name</th><th>Type</th><th>Description</th></tr>
                <tr><td>bOk</td><td>BOOL</td><td>success flag</td></tr>
              </table>
            </div></body></html>
            """);
        return client;
    }

    private static BeckhoffInfosysSearcher NewSearcher(FakeInfosysClient client) =>
        new(client, TempCache(), TimeSpan.Zero);

    private static string TempCache() =>
        Path.Combine(Path.GetTempPath(), "tckit-tests", Guid.NewGuid().ToString("N"));

    private static string MenuUrl(string section, string id, string contentFile = "index.html")
    {
        var content = Uri.EscapeDataString($"../content/1033/{section}/{contentFile}");
        return $"{Host}/english/menu/menu.php?content={content}&id={Uri.EscapeDataString(id)}";
    }
}
