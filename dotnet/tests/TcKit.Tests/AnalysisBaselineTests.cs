using TcKit.Adapters.Analysis;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>
/// Tests the baseline, which is what makes the check adoptable on an existing codebase: record
/// today's findings, then fail the build only on new ones.
/// </summary>
public class AnalysisBaselineTests
{
    private static AnalysisFinding Finding(
        string objectName, string symbol, int line = 1, string ruleId = "TCK2004") => new()
    {
        RuleId = ruleId,
        Category = "correctness",
        Severity = DiagnosticSeverity.Warning,
        Message = "test",
        PlcName = "Plc",
        ObjectName = objectName,
        ItemName = "Execute",
        Line = line,
        Symbol = symbol,
    };

    [Fact]
    public void Fingerprint_IgnoresTheLineNumber()
    {
        // Inserting a variable higher up a declaration must not invalidate every entry below it
        // and fail a build that changed nothing relevant.
        Assert.Equal(
            AnalysisBaseline.Fingerprint(Finding("FB_A", "spare", line: 4)),
            AnalysisBaseline.Fingerprint(Finding("FB_A", "spare", line: 99)));
    }

    [Theory]
    [InlineData("FB_B", "spare", "TCK2004")]
    [InlineData("FB_A", "other", "TCK2004")]
    [InlineData("FB_A", "spare", "TCK2005")]
    public void Fingerprint_DistinguishesObjectSymbolAndRule(string obj, string symbol, string rule)
        => Assert.NotEqual(
            AnalysisBaseline.Fingerprint(Finding("FB_A", "spare")),
            AnalysisBaseline.Fingerprint(Finding(obj, symbol, ruleId: rule)));

    [Fact]
    public void Filter_RemovesBaselinedFindingsAndKeepsNewOnes()
    {
        var baselined = Finding("FB_A", "spare");
        var fresh = Finding("FB_B", "newProblem");
        var baseline = new HashSet<string>(StringComparer.Ordinal)
        {
            AnalysisBaseline.Fingerprint(baselined),
        };

        var remaining = AnalysisBaseline.Filter([baselined, fresh], baseline);

        Assert.Equal("newProblem", Assert.Single(remaining).Symbol);
    }

    [Fact]
    public void Filter_EmptyBaseline_KeepsEverything()
        => Assert.Equal(2, AnalysisBaseline.Filter(
            [Finding("FB_A", "a"), Finding("FB_B", "b")],
            new HashSet<string>(StringComparer.Ordinal)).Count);

    [Fact]
    public void Load_MissingFile_IsAnEmptyBaselineNotAnError()
    {
        // The first run on a branch that has no baseline yet must not fail.
        Assert.Empty(AnalysisBaseline.Load(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}")));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAndSuppressesTheSameFindings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tckit-baseline-{Guid.NewGuid():N}.txt");
        var findings = new[] { Finding("FB_A", "spare"), Finding("FB_B", "other") };

        try
        {
            AnalysisBaseline.Save(path, findings);

            Assert.Empty(AnalysisBaseline.Filter(findings, AnalysisBaseline.Load(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_WritesCommentsAndSortsSoTheFileDiffsCleanly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tckit-baseline-{Guid.NewGuid():N}.txt");

        try
        {
            AnalysisBaseline.Save(path, [Finding("FB_Z", "z"), Finding("FB_A", "a")]);
            var lines = File.ReadAllLines(path);
            var entries = lines.Where(line => !line.StartsWith('#')).ToList();

            Assert.Contains(lines, line => line.StartsWith('#'));
            Assert.Equal(entries.OrderBy(e => e, StringComparer.Ordinal), entries);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_IgnoresCommentsAndBlankLines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tckit-baseline-{Guid.NewGuid():N}.txt");

        try
        {
            var fingerprint = AnalysisBaseline.Fingerprint(Finding("FB_A", "spare"));
            File.WriteAllLines(path, ["# a comment", "", "   ", fingerprint]);

            Assert.Single(AnalysisBaseline.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
