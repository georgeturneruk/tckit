using TcKit.Adapters.Docs;

namespace TcKit.Tests;

/// <summary>
/// HTML parsing of infosys pages: title-suffix stripping, main-content extraction (boilerplate
/// removed), the first descriptive paragraph, and both parameter-table layouts.
/// </summary>
public sealed class InfosysParserTests
{
    [Fact]
    public void ExtractTitle_StripsBrandingSuffix()
    {
        using var doc = InfosysParser.Parse("<html><head><title>FB_MemSet - Beckhoff Infosys</title></head><body></body></html>");
        Assert.Equal("FB_MemSet", InfosysParser.ExtractTitle(doc));
    }

    [Fact]
    public void ExtractMainContent_PrefersContentDivAndStripsBoilerplate()
    {
        const string html = """
            <html><body>
              <nav>menu noise</nav>
              <script>var x = 1;</script>
              <div id="content"><p>Real documentation text.</p></div>
              <footer>footer noise</footer>
            </body></html>
            """;
        using var doc = InfosysParser.Parse(html);

        var content = InfosysParser.ExtractMainContent(doc);

        Assert.Contains("Real documentation text.", content, StringComparison.Ordinal);
        Assert.DoesNotContain("menu noise", content, StringComparison.Ordinal);
        Assert.DoesNotContain("footer noise", content, StringComparison.Ordinal);
        Assert.DoesNotContain("var x", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractDescription_ReturnsFirstMeaningfulParagraph()
    {
        const string html = """
            <html><body><div id="content">
              <p>The function block fills a memory area with a value.</p>
              <p>A second paragraph that should be ignored.</p>
            </div></body></html>
            """;
        using var doc = InfosysParser.Parse(html);

        // Mirrors the Python adapter: the first <p> in the content container is taken (when long
        // enough), not a scan for the "best" paragraph.
        Assert.Equal(
            "The function block fills a memory area with a value.",
            InfosysParser.ExtractDescription(doc));
    }

    [Fact]
    public void ExtractParameterTable_DirectionColumn()
    {
        const string html = """
            <html><body><table>
              <tr><th>Name</th><th>Type</th><th>Direction</th><th>Description</th></tr>
              <tr><td>bExecute</td><td>BOOL</td><td>INPUT</td><td>Start the call.</td></tr>
              <tr><td>bDone</td><td>BOOL</td><td>OUTPUT</td><td>Finished.</td></tr>
            </table></body></html>
            """;
        using var doc = InfosysParser.Parse(html);

        var rows = InfosysParser.ExtractParameterTable(doc);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new ParamRow("bExecute", "BOOL", "INPUT", "Start the call."), rows[0]);
        Assert.Equal(new ParamRow("bDone", "BOOL", "OUTPUT", "Finished."), rows[1]);
    }

    [Fact]
    public void ExtractParameterTable_InfersDirectionFromHeading()
    {
        const string html = """
            <html><body><div id="content">
              <h3>VAR_INPUT</h3>
              <table>
                <tr><th>Name</th><th>Type</th><th>Description</th></tr>
                <tr><td>pDest</td><td>PVOID</td><td>destination</td></tr>
              </table>
              <h3>VAR_OUTPUT</h3>
              <table>
                <tr><th>Name</th><th>Type</th><th>Description</th></tr>
                <tr><td>bOk</td><td>BOOL</td><td>success</td></tr>
              </table>
            </div></body></html>
            """;
        using var doc = InfosysParser.Parse(html);

        var rows = InfosysParser.ExtractParameterTable(doc);

        Assert.Equal(2, rows.Count);
        Assert.Equal("input", rows[0].Direction);
        Assert.Equal("pDest", rows[0].Name);
        Assert.Equal("output", rows[1].Direction);
        Assert.Equal("bOk", rows[1].Name);
    }

    [Fact]
    public void ExtractTechnicalData_ParsesPropertyValueRowsBelowHeader()
    {
        const string html = """
            <html><body><table>
              <tr><td>Technical data</td><td>EL3004</td></tr>
              <tr><td>Number of inputs</td><td>4</td></tr>
              <tr><td>Signal voltage</td><td>-10 V ... +10 V</td></tr>
              <tr><td>Resolution</td><td>12 bits</td></tr>
            </table></body></html>
            """;
        using var doc = InfosysParser.Parse(html);

        var rows = InfosysParser.ExtractTechnicalData(doc);

        Assert.Equal(3, rows.Count); // the "Technical data | EL3004" header row is dropped
        Assert.Equal(new TechRow("Number of inputs", "4"), rows[0]);
        Assert.Equal(new TechRow("Signal voltage", "-10 V ... +10 V"), rows[1]);
    }

    [Fact]
    public void ExtractTechnicalData_IgnoresTablesWithoutHeader()
    {
        const string html = "<html><body><table>"
            + "<tr><th>Name</th><th>Value</th></tr><tr><td>foo</td><td>bar</td></tr>"
            + "</table></body></html>";
        using var doc = InfosysParser.Parse(html);

        Assert.Empty(InfosysParser.ExtractTechnicalData(doc));
    }

    [Fact]
    public void ExtractTechnicalData_HeadingMarkedThreeColumnTable()
    {
        // EtherCAT Box layout: a preceding "Technical data" heading and a category|property|value
        // table (category cells span rows). Property/value come from the last two non-empty cells.
        const string html = """
            <html><body>
              <h2>Technical data</h2>
              <table>
                <tr><td>EtherCAT P</td><td>Connection</td><td>2 x M8 socket</td></tr>
                <tr><td></td><td>Supply voltage</td><td>24 V</td></tr>
                <tr><td>Inputs</td><td>Number</td><td>8</td></tr>
              </table>
            </body></html>
            """;
        using var doc = InfosysParser.Parse(html);

        var rows = InfosysParser.ExtractTechnicalData(doc);

        Assert.Equal(3, rows.Count);
        Assert.Equal(new TechRow("Connection", "2 x M8 socket"), rows[0]);
        Assert.Equal(new TechRow("Supply voltage", "24 V"), rows[1]); // spanned category dropped
        Assert.Equal(new TechRow("Number", "8"), rows[2]);
    }

    [Fact]
    public void ExtractTechnicalData_DropsComparisonHeaderOfOrderNumbers()
    {
        // EtherCAT Box comparison table: the header row's cells are the variant order numbers.
        const string html = """
            <html><body><h2>Technical data</h2><table>
              <tr><td>EPP1008-0001</td><td>EPP1018-0001</td></tr>
              <tr><td>Number of inputs</td><td>8</td></tr>
              <tr><td>Connection</td><td>8 x M8 socket</td></tr>
            </table></body></html>
            """;
        using var doc = InfosysParser.Parse(html);

        var rows = InfosysParser.ExtractTechnicalData(doc);

        Assert.Equal(2, rows.Count); // the order-number header row is dropped
        Assert.DoesNotContain(rows, r => r.Property.StartsWith("EPP", StringComparison.Ordinal));
        Assert.Equal(new TechRow("Number of inputs", "8"), rows[0]);
    }

    [Fact]
    public void FindLinkByOrder_MatchesExactAndVariantSuffix()
    {
        const string html = "<html><body>"
            + "<a href=\"x.html\">EL3001</a>"
            + "<a href=\"t.html\">EL3004</a>"
            + "<a href=\"p.html\">EPP1008-0001</a></body></html>";
        using var doc = InfosysParser.Parse(html);

        Assert.Equal("t.html", InfosysParser.FindLinkByOrder(doc, "EL3004"));    // exact
        Assert.Equal("p.html", InfosysParser.FindLinkByOrder(doc, "EPP1008"));   // variant suffix
        Assert.Null(InfosysParser.FindLinkByOrder(doc, "EL9999"));
    }
}
