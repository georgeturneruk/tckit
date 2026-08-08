using TcKit.Adapters.Analysis;
using TcKit.Adapters.Reader;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>
/// End-to-end analysis over a real multi-PLC TwinCAT fixture, through the same reader the MCP
/// server uses. Unit tests cover the pieces; this proves the whole lane parses genuine
/// XAE-written XML rather than only the hand-built source in the other tests.
/// </summary>
public class ProjectAnalyserTests
{
    private static ProjectAnalyser Analyser() => new(new XmlProjectReader());

    private static AnalysisRequest Request(string? objectName = null) => new()
    {
        // The .sln path also exercises the reader's "a .sln stands for its directory" shorthand.
        ProjectPath = Fixtures.T3Solution,
        ObjectName = objectName,
        MinimumSeverity = DiagnosticSeverity.Suggestion,
    };

    [Fact]
    public async Task AnalyseAsync_RealProject_ReadsEveryObjectWithoutSkipping()
    {
        var result = await Analyser().AnalyseAsync(Request(), CancellationToken.None);

        Assert.Empty(result.Skipped);
        Assert.Empty(result.ConfigWarnings);
        Assert.True(result.ObjectsAnalysed > 10, $"Analysed only {result.ObjectsAnalysed} objects.");
        Assert.Equal(NamingProfiles.Hybrid, result.Profile);
    }

    [Fact]
    public async Task AnalyseAsync_HungarianFixture_FlagsVariablesUnderTheHybridDefault()
    {
        // The fixture is written in the Beckhoff house style, so the hybrid default should have
        // plenty to say about its variables and nothing to say about its object names.
        var result = await Analyser().AnalyseAsync(Request(), CancellationToken.None);

        Assert.Contains(result.Findings, finding => finding.RuleId == NamingRuleEngine.VariableRuleId);
        Assert.DoesNotContain(
            result.Findings,
            finding => finding.RuleId == NamingRuleEngine.ObjectRuleId && finding.ObjectName.StartsWith("FB_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyseAsync_EveryFinding_CarriesALocationAndASuggestion()
    {
        var result = await Analyser().AnalyseAsync(Request(), CancellationToken.None);

        Assert.NotEmpty(result.Findings);
        Assert.All(result.Findings, finding =>
        {
            Assert.NotEmpty(finding.ObjectName);
            Assert.NotEmpty(finding.PlcName);
            Assert.NotEmpty(finding.Symbol);
            Assert.True(finding.Line >= 1);
            Assert.NotEqual(finding.Symbol, finding.Suggestion);
        });
    }

    [Fact]
    public async Task AnalyseAsync_ObjectNameScope_RestrictsToThatObject()
    {
        var result = await Analyser().AnalyseAsync(Request("FB_RingBuffer"), CancellationToken.None);

        Assert.Equal(1, result.ObjectsAnalysed);
        Assert.All(result.Findings, finding => Assert.Equal("FB_RingBuffer", finding.ObjectName));
    }

    [Fact]
    public async Task AnalyseAsync_RuleIdFilter_ReturnsOnlyThatRule()
    {
        var request = Request() with { RuleIds = [NamingRuleEngine.VariableRuleId] };

        var result = await Analyser().AnalyseAsync(request, CancellationToken.None);

        Assert.NotEmpty(result.Findings);
        Assert.All(result.Findings, finding => Assert.Equal(NamingRuleEngine.VariableRuleId, finding.RuleId));
    }

    [Fact]
    public async Task AnalyseAsync_MinimumSeverityAboveSuggestion_FiltersNamingOut()
    {
        // Naming ships at suggestion, so asking for warnings and above should come back clean
        // without the analyser having skipped anything.
        var request = Request() with { MinimumSeverity = DiagnosticSeverity.Warning };

        var result = await Analyser().AnalyseAsync(request, CancellationToken.None);

        Assert.Empty(result.Findings);
        Assert.Empty(result.Skipped);
    }
}
