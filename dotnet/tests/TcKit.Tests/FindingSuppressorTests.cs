using TcKit.Adapters.Analysis;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>
/// Tests in-source suppression. Every rule has legitimate exceptions, and without a way to record
/// one in the code the only choices are silencing a whole rule or living with the noise.
/// </summary>
public class FindingSuppressorTests
{
    private static AnalysedProject Project(string declaration, string body) => new()
    {
        Structure = new ProjectStructure { ProjectPath = "fixture" },
        Classifier = new TypeClassifier(new Dictionary<string, TypeClass>()),
        Pous =
        [
            AnalysedProject.Analyse(
                new PouSource
                {
                    PouName = "FB_Host",
                    PouType = PouType.FunctionBlock,
                    Path = "FB_Host.TcPOU",
                    Declaration = declaration,
                    Body = body,
                },
                "Plc"),
        ],
    };

    private static AnalysisFinding Finding(int line, string ruleId, CodePart part) => new()
    {
        RuleId = ruleId,
        Category = "correctness",
        Severity = DiagnosticSeverity.Warning,
        Message = "test",
        PlcName = "Plc",
        ObjectName = "FB_Host",
        Part = part,
        Line = line,
        Symbol = "x",
    };

    private static List<AnalysisFinding> Apply(
        AnalysedProject project, params AnalysisFinding[] findings)
        => FindingSuppressor.Apply(findings, project);

    [Fact]
    public void Apply_DisableNextLine_SuppressesTheLineBelow()
    {
        var project = Project("FUNCTION_BLOCK FB_Host", "// tckit-disable-next-line TCK2002\nIF a = b THEN\n;\nEND_IF");

        Assert.Empty(Apply(project, Finding(2, "TCK2002", CodePart.Implementation)));
    }

    [Fact]
    public void Apply_DisableNextLine_DoesNotSuppressOtherLines()
    {
        var project = Project("FUNCTION_BLOCK FB_Host", "// tckit-disable-next-line TCK2002\nIF a = b THEN\nIF c = d THEN");

        Assert.Single(Apply(project, Finding(3, "TCK2002", CodePart.Implementation)));
    }

    [Fact]
    public void Apply_DisableLine_SuppressesItsOwnLine()
    {
        var project = Project("FUNCTION_BLOCK FB_Host", "IF a = b THEN // tckit-disable-line TCK2002");

        Assert.Empty(Apply(project, Finding(1, "TCK2002", CodePart.Implementation)));
    }

    [Fact]
    public void Apply_OnlySuppressesTheNamedRule()
    {
        var project = Project("FUNCTION_BLOCK FB_Host", "// tckit-disable-next-line TCK2002\nIF a = b THEN");

        Assert.Single(Apply(project, Finding(2, "TCK2004", CodePart.Implementation)));
    }

    [Fact]
    public void Apply_CommaSeparatedRules_AreAllSuppressed()
    {
        var project = Project("FUNCTION_BLOCK FB_Host", "// tckit-disable-next-line TCK2002, TCK2004\nIF a = b THEN");

        Assert.Empty(Apply(
            project,
            Finding(2, "TCK2002", CodePart.Implementation),
            Finding(2, "TCK2004", CodePart.Implementation)));
    }

    [Fact]
    public void Apply_BareSuppression_CoversEveryRuleOnTheLine()
    {
        var project = Project("FUNCTION_BLOCK FB_Host", "// tckit-disable-next-line\nIF a = b THEN");

        Assert.Empty(Apply(
            project,
            Finding(2, "TCK2002", CodePart.Implementation),
            Finding(2, "TCK1002", CodePart.Implementation)));
    }

    [Fact]
    public void Apply_InADeclaration_SuppressesADeclarationFinding()
    {
        var project = Project(
            "FUNCTION_BLOCK FB_Host\nVAR\n    // tckit-disable-next-line TCK2004\n    spare : INT;\nEND_VAR", "");

        Assert.Empty(Apply(project, Finding(4, "TCK2004", CodePart.Declaration)));
    }

    [Fact]
    public void Apply_DeclarationSuppression_DoesNotLeakIntoTheBody()
    {
        // Lines are counted within each half separately, so a comment in one must not silence the
        // same line number in the other.
        var project = Project("FUNCTION_BLOCK FB_Host\n// tckit-disable-next-line TCK2002", "IF a = b THEN\nIF c = d THEN");

        Assert.Single(Apply(project, Finding(2, "TCK2002", CodePart.Implementation)));
    }

    [Fact]
    public void Apply_NoSuppressionComments_KeepsEverything()
    {
        var project = Project("FUNCTION_BLOCK FB_Host", "IF a = b THEN");

        Assert.Single(Apply(project, Finding(1, "TCK2002", CodePart.Implementation)));
    }
}
