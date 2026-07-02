using TcKit.Adapters.Docs;

namespace TcKit.Tests;

/// <summary>
/// Section-index search ordering and the menu.php tree crawl (index.html primaryid -&gt; menu links
/// -&gt; child-page collection), driven against the canned-HTML fake.
/// </summary>
public sealed class InfosysNavigatorTests
{
    private const string Host = "https://infosys.beckhoff.com";
    private const string Section = "tf6310_tc3_tcpip";

    [Fact]
    public void SearchIndex_ExactMatchSortsFirst()
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["abc"] = "u-abc",
            ["ab"] = "u-ab",
        };

        var results = InfosysNavigator.SearchIndex(index, "ab");

        Assert.Equal(2, results.Count);
        Assert.Equal(("ab", "u-ab"), results[0]); // exact first even though it is later in the dict
        Assert.Equal(("abc", "u-abc"), results[1]);
    }

    [Fact]
    public void SearchIndex_SubstringMatchesOnly()
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fb_socketsend"] = "u1",
            ["fb_clientconnect"] = "u2",
        };

        var results = InfosysNavigator.SearchIndex(index, "socket");

        Assert.Single(results);
        Assert.Equal(("fb_socketsend", "u1"), results[0]);
    }

    [Fact]
    public async Task BuildSectionIndex_CrawlsMenuTreeAndChildPages()
    {
        var client = new FakeInfosysClient();
        client.Add($"{Host}/content/1033/{Section}/index.html",
            "<html><head><meta name=\"primaryid\" content=\"555\"></head><body></body></html>");
        client.Add(MenuUrl(Section, "555"),
            $"<html><body>"
            + $"<a href=\"/content/1033/{Section}/100.html\">FB_SocketSend</a>"
            + $"<a href=\"/content/1033/{Section}/index.html\">Overview</a>"   // index.html -> filtered
            + "<a href=\"/content/1033/other/200.html\">Other</a>"             // foreign -> filtered
            + "</body></html>");
        client.Add($"{Host}/content/1033/{Section}/100.html",
            "<html><head><title>FB_SocketSend</title></head><body>"
            + "<a href=\"200.html\">FB_SocketClose</a></body></html>");
        client.Add($"{Host}/content/1033/{Section}/200.html",
            "<html><head><title>FB_SocketClose</title></head><body></body></html>");

        var index = await InfosysNavigator.BuildSectionIndexAsync(client, Section, TimeSpan.Zero, default);

        Assert.Equal($"{Host}/content/1033/{Section}/100.html", index["fb_socketsend"]);
        Assert.Equal($"{Host}/content/1033/{Section}/200.html", index["fb_socketclose"]);
        Assert.DoesNotContain(index.Values, v => v.Contains("/other/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildSectionIndex_NoPrimaryId_ReturnsEmpty()
    {
        var index = await InfosysNavigator.BuildSectionIndexAsync(
            new FakeInfosysClient(), Section, TimeSpan.Zero, default);

        Assert.Empty(index);
    }

    [Fact]
    public async Task CrawlSection_StopsEarlyThenResumesFromFrontier()
    {
        var client = new FakeInfosysClient();
        client.Add($"{Host}/content/1033/{Section}/index.html",
            "<html><head><meta name=\"primaryid\" content=\"555\"></head><body></body></html>");
        client.Add(MenuUrl(Section, "555"),
            $"<html><body><a href=\"/content/1033/{Section}/100.html\">FB_SocketSend</a></body></html>");
        client.Add($"{Host}/content/1033/{Section}/100.html",
            $"<html><head><title>FB_SocketSend</title></head><body>"
            + $"<a href=\"200.html\">FB_SocketClose</a></body></html>");
        client.Add($"{Host}/content/1033/{Section}/200.html",
            "<html><head><title>FB_SocketClose</title></head><body></body></html>");

        // First pass: stop after the first page, leaving 200.html on the frontier.
        var calls = 0;
        var partial = await InfosysNavigator.CrawlSectionAsync(
            client, Section, resume: null, TimeSpan.Zero, shouldStop: () => calls++ >= 1, onProgress: null, default);

        Assert.NotNull(partial);
        Assert.False(partial!.Complete);
        Assert.Contains(partial.Queue, n => n.Url.EndsWith("200.html", StringComparison.Ordinal));
        Assert.DoesNotContain($"{Host}/content/1033/{Section}/200.html", partial.Visited);

        // Resume: no stop -> drains the persisted frontier to completion.
        var full = await InfosysNavigator.CrawlSectionAsync(
            client, Section, resume: partial, TimeSpan.Zero, shouldStop: static () => false, onProgress: null, default);

        Assert.NotNull(full);
        Assert.True(full!.Complete);
        Assert.Empty(full.Queue);
        Assert.Contains($"{Host}/content/1033/{Section}/200.html", full.Visited);
    }

    [Fact]
    public void KnownSections_IncludeMotionAndIoLinkLibraries()
    {
        Assert.Contains("tcplclib_tc2_mc2", InfosysNavigator.KnownSections);
        Assert.Contains("tcplclib_tc2_mc2_drive", InfosysNavigator.KnownSections);
        Assert.Contains("tcplclib_tc3_iolink", InfosysNavigator.KnownSections);
    }

    [Fact]
    public void HardwareSections_IncludeEtherCatPBoxes()
    {
        Assert.Contains("epp1xxx", InfosysNavigator.HardwareSections);
        Assert.Contains("epp31xx", InfosysNavigator.HardwareSections);
        Assert.Contains("epp622x", InfosysNavigator.HardwareSections);
    }

    [Theory]
    [InlineData("epp1xxx", "EPP1008", true)]
    [InlineData("epp31xx", "EPP3174", true)]
    [InlineData("epp31xx", "EPP3204", false)]
    [InlineData("epp622x", "EPP6228", true)]    // IO-Link master, EtherCAT P
    [InlineData("epp622x", "EPP6224", true)]
    [InlineData("epp622x", "EPP6328", false)]
    public void SectionCoversOrder_HandlesEtherCatP(string slug, string order, bool expected)
        => Assert.Equal(expected, InfosysNavigator.SectionCoversOrder(slug, order));

    [Theory]
    [InlineData("epp7342-0002", "EPP7342", true)]   // hyphenated order-specific slug, suffix dropped
    [InlineData("ep3751-0160", "EP3751", true)]
    [InlineData("cu112x", "CU1128", true)]          // infrastructure switch
    [InlineData("ej110x-00xx", "EJ1100", true)]     // plug-in module
    [InlineData("epi1xxx", "EPI1008", true)]        // IO-Link box
    [InlineData("erxxxx", "ER2008", true)]          // rugged EtherCAT box catch-all
    [InlineData("eqxxxx", "EQ2339", true)]
    [InlineData("ep3751-0160", "EP3752", false)]    // base still anchored, no false positives
    public void SectionCoversOrder_StripsVariantSuffix(string slug, string order, bool expected)
        => Assert.Equal(expected, InfosysNavigator.SectionCoversOrder(slug, order));

    // One representative order per hardware family must be covered by at least one section, so
    // find_hardware reaches a section to navigate. Orders are pre-normalised (bare, upper-cased).
    [Theory]
    [InlineData("EL3004")]    // EtherCAT Terminal
    [InlineData("ELM3504")]   // measurement terminal
    [InlineData("EK1100")]    // EtherCAT coupler
    [InlineData("CU1128")]    // infrastructure switch
    [InlineData("EP3174")]    // EtherCAT Box
    [InlineData("ER2008")]    // rugged EtherCAT Box
    [InlineData("EPP6228")]   // EtherCAT P Box
    [InlineData("EPP3504")]   // EtherCAT P Box analog
    [InlineData("EJ1100")]    // EtherCAT plug-in module
    [InlineData("EPI1008")]   // IO-Link box
    public void HardwareSections_CoverEveryFamily(string order)
        => Assert.Contains(InfosysNavigator.HardwareSections, s => InfosysNavigator.SectionCoversOrder(s, order));

    [Theory]
    [InlineData("el30xx", "EL3004", true)]
    [InlineData("el30xx", "EL3104", false)]            // EL31xx is a different section
    [InlineData("el10xx_el11xx", "EL1008", true)]      // first underscore part
    [InlineData("el10xx_el11xx", "EL1108", true)]      // second underscore part
    [InlineData("el5101", "EL5101", true)]             // exact slug
    [InlineData("el5101", "EL5102", false)]
    [InlineData("el34x3", "EL3403", true)]             // mid-string x wildcard
    [InlineData("el34x3", "EL3404", false)]
    [InlineData("ep1xxx", "EP1008", true)]
    [InlineData("elm3xxx", "ELM3504", true)]
    public void SectionCoversOrder_MatchesByWildcard(string slug, string order, bool expected)
        => Assert.Equal(expected, InfosysNavigator.SectionCoversOrder(slug, order));

    [Fact]
    public async Task FindOrderNode_DescendsProductBranchToTechnicalData()
    {
        // Couplers nest the product several levels below the overview and reuse the order number on an
        // aspect page (Diagnostic LEDs -> EK1100) that carries no table. The walk must descend the
        // product branch and return the order node that owns a "Technical data" child, not the aspect.
        const string sec = "couplertest";
        var client = new FakeInfosysClient();
        client.Add($"{Host}/content/1033/{sec}/index.html",
            "<html><head><meta name=\"primaryid\" content=\"1\"></head><body></body></html>");
        client.Add(MenuFor(sec, "index.html", "1"),
            $"<html><body>"
            + $"<a href=\"/content/1033/{sec}/diag.html?id=5\">Diagnostic LEDs</a>"
            + $"<a href=\"/content/1033/{sec}/po.html?id=2\">Product overview</a></body></html>");
        // Aspect branch: an order-named node with no Technical data child.
        client.Add(MenuFor(sec, "diag.html", "5"),
            $"<html><body><a href=\"/content/1033/{sec}/diagek.html?id=6\">EK1100</a></body></html>");
        client.Add(MenuFor(sec, "diagek.html", "6"),
            $"<html><body><a href=\"/content/1033/{sec}/status.html?id=7\">Status</a></body></html>");
        // Product branch: order node that owns a Technical data child.
        client.Add(MenuFor(sec, "po.html", "2"),
            $"<html><body><a href=\"/content/1033/{sec}/prod.html?id=3\">EK1100</a></body></html>");
        client.Add(MenuFor(sec, "prod.html", "3"),
            $"<html><body><a href=\"/content/1033/{sec}/tech.html?id=4\">Technical data</a></body></html>");

        var node = await InfosysNavigator.FindOrderNodeAsync(client, sec, "1", "EK1100", TimeSpan.Zero, default);

        Assert.NotNull(node);
        Assert.Equal($"{Host}/content/1033/{sec}/prod.html", node!.Value.Url);
        Assert.Equal("3", node.Value.PrimaryId);
    }

    private static string MenuUrl(string section, string id) => MenuFor(section, "index.html", id);

    private static string MenuFor(string section, string file, string id)
    {
        var content = Uri.EscapeDataString($"../content/1033/{section}/{file}");
        return $"{Host}/english/menu/menu.php?content={content}&id={Uri.EscapeDataString(id)}";
    }
}
