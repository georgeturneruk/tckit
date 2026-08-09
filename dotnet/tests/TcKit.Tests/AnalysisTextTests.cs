using TcKit.Adapters.Analysis;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>Tests the build-log rendering, which is what a CI run actually shows a human.</summary>
public class AnalysisTextTests
{
    private static AnalysisResult Result(
        IReadOnlyList<AnalysisFinding>? findings = null,
        IReadOnlyList<string>? skipped = null,
        IReadOnlyList<string>? rulesNotRun = null) => new()
    {
        ProjectPath = "x.sln",
        Profile = "hybrid",
        ObjectsAnalysed = 12,
        Findings = findings ?? [],
        Skipped = skipped ?? [],
        RulesNotRun = rulesNotRun ?? [],
    };

    private static AnalysisFinding Finding(DiagnosticSeverity severity, string suggestion = "") => new()
    {
        RuleId = "TCK2004",
        Category = "correctness",
        Severity = severity,
        Message = "Local 'spare' is never used.",
        PlcName = "Plc",
        ObjectName = "FB_Host",
        ItemName = "Execute",
        Line = 7,
        Symbol = "spare",
        Suggestion = suggestion,
    };

    [Fact]
    public void Render_UsesTheCompilerLocationFormat()
    {
        var text = AnalysisText.Render(Result([Finding(DiagnosticSeverity.Warning)]), [Finding(DiagnosticSeverity.Warning)]);

        Assert.Contains("Plc/FB_Host.Execute(7): warning TCK2004:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ObjectLevelFinding_OmitsTheItemSegment()
    {
        var finding = Finding(DiagnosticSeverity.Warning) with { ItemName = "" };

        var text = AnalysisText.Render(Result([finding]), [finding]);

        Assert.Contains("Plc/FB_Host(7):", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_IncludesTheSuggestionWhenThereIsOne()
    {
        var finding = Finding(DiagnosticSeverity.Suggestion, "Spare");

        var text = AnalysisText.Render(Result([finding]), [finding]);

        Assert.Contains("Suggested: 'Spare'.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_SummarisesBySeverity()
    {
        var findings = new[] { Finding(DiagnosticSeverity.Warning), Finding(DiagnosticSeverity.Suggestion) };

        var text = AnalysisText.Render(Result(findings), findings);

        Assert.Contains("12 objects analysed, profile 'hybrid', 1 warning, 1 suggestion.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_NoFindings_SaysSoPlainly()
        => Assert.Contains("no findings.", AnalysisText.Render(Result(), []), StringComparison.Ordinal);

    [Fact]
    public void Render_SurfacesWhatLimitedTheRunBeforeTheFindings()
    {
        // A short finding list next to a long skipped list means partial coverage, not a clean
        // project, and that must not be easy to miss in a build log.
        var result = Result(skipped: ["Plc.FB_Broken: bad xml"], rulesNotRun: ["TCK3002: scoped run"]);

        var text = AnalysisText.Render(result, []);

        Assert.Contains("tckit: skipped: Plc.FB_Broken", text, StringComparison.Ordinal);
        Assert.Contains("tckit: rule not run: TCK3002", text, StringComparison.Ordinal);
        Assert.Contains("1 skipped", text, StringComparison.Ordinal);
    }
}
