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

    private static string MenuUrl(string section, string id)
    {
        var content = Uri.EscapeDataString($"../content/1033/{section}/index.html");
        return $"{Host}/english/menu/menu.php?content={content}&id={Uri.EscapeDataString(id)}";
    }
}
