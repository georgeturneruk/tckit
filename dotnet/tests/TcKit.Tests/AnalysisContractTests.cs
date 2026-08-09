using TcKit.Core.Models;
using TcKit.Core.Serialization;

namespace TcKit.Tests;

/// <summary>
/// The JSON contract for analysis results. The tool layer is a thin translation shell and is not
/// unit-tested here, in line with every other tool type, but the wire format is what the model
/// actually reads, and these DTOs introduce three new enums to it.
/// </summary>
public class AnalysisContractTests
{
    private static string Serialise() => TckitJson.Serialize(new AnalysisResult
    {
        ProjectPath = @"C:\proj\My.sln",
        Profile = "hybrid",
        ObjectsAnalysed = 3,
        Findings =
        [
            new AnalysisFinding
            {
                RuleId = "TCK2001",
                Category = "correctness",
                Severity = DiagnosticSeverity.Warning,
                Message = "message",
                PlcName = "Plc",
                ObjectName = "FB_Host",
                ItemName = "Execute",
                Part = CodePart.Declaration,
                Line = 4,
                FilePath = @"C:\proj\Plc\POUs\FB_Host.TcPOU",
                FileLine = 27,
                Symbol = "delay",
                Suggestion = "",
            },
        ],
        Skipped = ["Plc.FB_Broken: bad xml"],
        ConfigWarnings = ["unknown profile"],
        RulesNotRun = ["TCK3002: needs the whole solution"],
    });

    [Theory]
    [InlineData("project_path")]
    [InlineData("objects_analysed")]
    [InlineData("rule_id")]
    [InlineData("object_name")]
    [InlineData("item_name")]
    [InlineData("file_path")]
    [InlineData("file_line")]
    [InlineData("config_warnings")]
    [InlineData("rules_not_run")]
    public void Serialize_UsesSnakeCaseKeys(string key)
        => Assert.Contains($"\"{key}\"", Serialise(), StringComparison.Ordinal);

    [Fact]
    public void Serialize_EmitsEnumsAsStringsNotNumbers()
    {
        var json = Serialise();

        // A severity of 3 would be unreadable to the caller and silently wrong to filter on.
        Assert.Contains("\"warning\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"declaration\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"severity\": 3", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_KeepsTheDiagnosticListsEvenWhenTheyMatter()
    {
        var json = Serialise();

        Assert.Contains("bad xml", json, StringComparison.Ordinal);
        Assert.Contains("unknown profile", json, StringComparison.Ordinal);
        Assert.Contains("needs the whole solution", json, StringComparison.Ordinal);
    }
}
