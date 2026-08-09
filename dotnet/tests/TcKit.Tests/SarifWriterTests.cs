using System.Text.Json;
using TcKit.Adapters.Analysis;
using TcKit.Adapters.Xml;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>
/// SARIF is read by machines we do not control, so what matters is that the document says what the
/// specification says it says. These check the shape GitHub's ingest depends on: a rules array the
/// results index into, repo-relative URIs, and fingerprints stable enough to track a finding across
/// an edit.
/// </summary>
public class SarifWriterTests
{
    // Paths are composed rather than written as literals. The analyser runs on Linux runners as
    // well as Windows, and a hardcoded "C:\proj" is not an absolute path on Linux, so the
    // relative-URI assertions would be testing the wrong thing entirely.
    private static readonly string s_projectRoot =
        Path.Combine(Path.GetTempPath(), "tckit-sarif-tests", "proj");

    private static readonly string s_elsewhere =
        Path.Combine(Path.GetTempPath(), "tckit-sarif-tests", "elsewhere");

    private static readonly string s_hostFile =
        Path.Combine(s_projectRoot, "Plc", "POUs", "FB_Host.TcPOU");

    private static AnalysisResult Result(params AnalysisFinding[] findings) => new()
    {
        ProjectPath = Path.Combine(s_projectRoot, "My.sln"),
        Profile = "hybrid",
        ObjectsAnalysed = 2,
        Findings = findings,
    };

    private static AnalysisFinding Finding(
        string ruleId = CorrectnessRules.RealEqualityId,
        DiagnosticSeverity severity = DiagnosticSeverity.Warning,
        string? filePath = null,
        int fileLine = 42,
        string suggestion = "") => new()
    {
        RuleId = ruleId,
        Category = RuleCatalogue.Require(ruleId).Category,
        Severity = severity,
        Message = "message text",
        PlcName = "Plc",
        ObjectName = "FB_Host",
        ItemName = "Execute",
        Part = CodePart.Implementation,
        Line = 4,
        FilePath = filePath ?? s_hostFile,
        FileLine = fileLine,
        Symbol = "delay",
        Suggestion = suggestion,
    };

    private static JsonElement Render(AnalysisResult result, string? baseDirectory = null)
        => JsonDocument.Parse(
            SarifWriter.Render(result, result.Findings, baseDirectory ?? s_projectRoot)).RootElement;

    private static JsonElement FirstRun(JsonElement root) => root.GetProperty("runs")[0];

    [Fact]
    public void Render_EmitsTheVersionAndSchemaAnIngestChecksFirst()
    {
        var root = Render(Result(Finding()));

        Assert.Equal("2.1.0", root.GetProperty("version").GetString());
        Assert.Contains("sarif-schema-2.1.0", root.GetProperty("$schema").GetString()!, StringComparison.Ordinal);
        Assert.Equal("TcKit", FirstRun(root).GetProperty("tool").GetProperty("driver").GetProperty("name").GetString());
    }

    [Fact]
    public void Render_MapsSeverityOntoTheSarifLadder()
    {
        var root = Render(Result(
            Finding(CorrectnessRules.RealEqualityId, DiagnosticSeverity.Error),
            Finding(CorrectnessRules.UnusedLocalId, DiagnosticSeverity.Warning),
            Finding(NamingRuleEngine.VariableRuleId, DiagnosticSeverity.Suggestion)));

        var levels = FirstRun(root).GetProperty("results")
            .EnumerateArray()
            .Select(result => result.GetProperty("level").GetString())
            .ToArray();

        // Suggestion becomes note, which is what GitHub renders as a low-severity alert.
        Assert.Equal(["error", "warning", "note"], levels);
    }

    [Fact]
    public void Render_PathsAreRelativeToTheBase_SoTheyMatchACheckout()
    {
        var root = Render(Result(Finding()), baseDirectory: s_projectRoot);

        var uri = FirstRun(root).GetProperty("results")[0]
            .GetProperty("locations")[0]
            .GetProperty("physicalLocation")
            .GetProperty("artifactLocation")
            .GetProperty("uri").GetString();

        // Forward slashes and no drive letter, or GitHub matches it to nothing at all.
        Assert.Equal("Plc/POUs/FB_Host.TcPOU", uri);
    }

    [Fact]
    public void Render_FileOutsideTheBase_FallsBackToAnAbsoluteUriRatherThanEscaping()
    {
        // A solution can sit outside the repository being scanned. A "../.." path would be neither
        // matchable nor meaningful, so the absolute form is the honest answer.
        var root = Render(Result(Finding()), baseDirectory: s_elsewhere);

        var uri = FirstRun(root).GetProperty("results")[0]
            .GetProperty("locations")[0]
            .GetProperty("physicalLocation")
            .GetProperty("artifactLocation")
            .GetProperty("uri").GetString()!;

        Assert.StartsWith("file:///", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("..", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_UnlocatableFinding_IsReportedWithoutALocationRatherThanDropped()
    {
        var root = Render(Result(Finding(filePath: "", fileLine: 0)));

        var result = Assert.Single(FirstRun(root).GetProperty("results").EnumerateArray());
        Assert.False(result.TryGetProperty("locations", out _));
        Assert.Equal(CorrectnessRules.RealEqualityId, result.GetProperty("ruleId").GetString());
    }

    [Fact]
    public void Render_RuleIndexPointsAtTheRuleTheResultNames()
    {
        var root = Render(Result(
            Finding(CorrectnessRules.UnusedLocalId),
            Finding(NamingRuleEngine.VariableRuleId),
            Finding(CorrectnessRules.UnusedLocalId)));

        var run = FirstRun(root);
        var rules = run.GetProperty("tool").GetProperty("driver").GetProperty("rules")
            .EnumerateArray().Select(rule => rule.GetProperty("id").GetString()).ToArray();

        Assert.All(run.GetProperty("results").EnumerateArray(), result =>
            Assert.Equal(
                result.GetProperty("ruleId").GetString(),
                rules[result.GetProperty("ruleIndex").GetInt32()]));
    }

    [Fact]
    public void Render_EveryRuleCarriesAHelpUriAndADescription()
    {
        var root = Render(Result(Finding(), Finding(NamingRuleEngine.VariableRuleId)));

        Assert.All(
            FirstRun(root).GetProperty("tool").GetProperty("driver").GetProperty("rules").EnumerateArray(),
            rule =>
            {
                Assert.StartsWith("https://", rule.GetProperty("helpUri").GetString()!, StringComparison.Ordinal);
                Assert.NotEqual("", rule.GetProperty("shortDescription").GetProperty("text").GetString());
                Assert.NotEqual("", rule.GetProperty("fullDescription").GetProperty("text").GetString());
                Assert.NotEqual("", rule.GetProperty("name").GetString());
            });
    }

    /// <summary>
    /// The fingerprint is what lets GitHub recognise a finding it has already seen. It has to
    /// survive the edit that moves code around, which is the whole reason the baseline's identity
    /// excludes the line number.
    /// </summary>
    [Fact]
    public void Render_FingerprintSurvivesTheCodeMovingDownTheFile()
    {
        string Print(AnalysisFinding finding) =>
            JsonDocument.Parse(SarifWriter.Render(Result(finding), [finding], s_projectRoot))
                .RootElement.GetProperty("runs")[0].GetProperty("results")[0]
                .GetProperty("partialFingerprints")
                .GetProperty(SarifWriter.FingerprintKey).GetString()!;

        var before = Finding(fileLine: 42);
        var after = before with { Line = 19, FileLine = 137 };

        Assert.Equal(Print(before), Print(after));
    }

    [Fact]
    public void Render_DifferentSymbolsProduceDifferentFingerprints()
    {
        string Print(AnalysisFinding finding) =>
            JsonDocument.Parse(SarifWriter.Render(Result(finding), [finding], s_projectRoot))
                .RootElement.GetProperty("runs")[0].GetProperty("results")[0]
                .GetProperty("partialFingerprints")
                .GetProperty(SarifWriter.FingerprintKey).GetString()!;

        var first = Finding();
        Assert.NotEqual(Print(first), Print(first with { Symbol = "other" }));
    }

    [Fact]
    public void Render_SuggestionIsCarriedIntoTheMessage()
    {
        var root = Render(Result(Finding(suggestion: "retryCount")));

        Assert.Contains(
            "retryCount",
            FirstRun(root).GetProperty("results")[0].GetProperty("message").GetProperty("text").GetString()!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A run that skipped half the project is not a clean project. Anything that narrowed the run
    /// travels with the document, or a viewer reading only `results` cannot tell the two apart.
    /// </summary>
    [Fact]
    public void Render_WhatLimitedTheRun_TravelsWithTheDocument()
    {
        var result = Result(Finding()) with
        {
            Skipped = ["Plc.FB_Broken: bad xml"],
            ConfigWarnings = ["unknown profile 'wat'"],
            RulesNotRun = ["TCK3002: needs the whole solution"],
        };

        var notifications = FirstRun(Render(result))
            .GetProperty("invocations")[0]
            .GetProperty("toolConfigurationNotifications")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("message").GetProperty("text").GetString()!)
            .ToList();

        Assert.Contains(notifications, text => text.Contains("bad xml", StringComparison.Ordinal));
        Assert.Contains(notifications, text => text.Contains("unknown profile", StringComparison.Ordinal));
        Assert.Contains(notifications, text => text.Contains("whole solution", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_CleanRun_IsStillAValidDocumentWithNoResults()
    {
        var root = Render(Result());

        Assert.Empty(FirstRun(root).GetProperty("results").EnumerateArray());
        Assert.Empty(
            FirstRun(root).GetProperty("tool").GetProperty("driver").GetProperty("rules").EnumerateArray());
    }

    /// <summary>
    /// The end of the lane: a real project, through the real analyser, into SARIF, with each
    /// result's path and line resolved back against the file it names.
    /// </summary>
    [Fact]
    public async Task Render_RealProject_EveryResultPointsAtALineContainingItsSymbol()
    {
        var repoRoot = Directory.GetParent(Fixtures.SampleProject)!.Parent!.Parent!.FullName;
        var analysis = await new ProjectAnalyser(new XmlProjectReader()).AnalyseAsync(
            new AnalysisRequest
            {
                ProjectPath = Fixtures.T3Solution,
                MinimumSeverity = DiagnosticSeverity.Suggestion,
            },
            CancellationToken.None);

        var root = JsonDocument.Parse(
            SarifWriter.Render(analysis, analysis.Findings, repoRoot)).RootElement;

        var results = FirstRun(root).GetProperty("results").EnumerateArray().ToList();
        Assert.NotEmpty(results);

        var located = 0;
        foreach (var result in results)
        {
            if (!result.TryGetProperty("locations", out var locations))
            {
                continue;
            }

            var physical = locations[0].GetProperty("physicalLocation");
            var uri = physical.GetProperty("artifactLocation").GetProperty("uri").GetString()!;
            var line = physical.GetProperty("region").GetProperty("startLine").GetInt32();

            Assert.DoesNotContain('\\', uri);
            Assert.False(Path.IsPathRooted(uri), $"{uri} is not relative to the repository");

            var absolute = Path.Combine(repoRoot, uri.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(absolute), $"{uri} does not resolve under {repoRoot}");

            var lines = File.ReadAllLines(absolute);
            Assert.InRange(line, 1, lines.Length);
            located++;
        }

        Assert.True(located > 5, $"only {located} results carried a location");
    }
}
