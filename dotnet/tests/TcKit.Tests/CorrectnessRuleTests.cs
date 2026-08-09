using TcKit.Adapters.Analysis;
using TcKit.Core.Analysis;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>
/// One pair of tests per correctness rule: the case it must catch, and the guard that keeps it
/// from firing on legitimate code. The guards matter more than the rules, because a false positive
/// invites a "fix" that breaks working code.
/// </summary>
public class CorrectnessRuleTests
{
    private static readonly TypeClassifier Classifier = new(
        new Dictionary<string, TypeClass>(StringComparer.OrdinalIgnoreCase)
        {
            ["FB_Drive"] = TypeClass.FbInstance,
            ["FB_Consumer"] = TypeClass.FbInstance,
        });

    private static AnalysedPou Pou(
        string name, PouType type, string declaration, string body = "", params PouMember[] members)
        => AnalysedProject.Analyse(
            new PouSource
            {
                PouName = name,
                PouType = type,
                Path = $"{name}.TcPOU",
                Declaration = declaration,
                Body = body,
                Members = members,
            },
            "Plc");

    private static PouMember Method(string name, string declaration, string body) => new()
    {
        Name = name,
        Kind = PouMemberKind.Method,
        Declaration = declaration,
        Body = body,
    };

    private static AnalysedGvl Gvl(string name, string declaration) => new()
    {
        PlcName = "Plc",
        Source = new Gvl { Name = name, Path = $"{name}.TcGVL", Declaration = declaration },
        Declaration = DeclarationParser.Parse(declaration),
    };

    private static List<AnalysisFinding> Run(
        IEnumerable<AnalysedPou> pous,
        IEnumerable<AnalysedGvl>? gvls = null,
        IEnumerable<TaskInfo>? tasks = null)
        => CorrectnessRules.Run(
            new AnalysedProject
            {
                Structure = new ProjectStructure
                {
                    ProjectPath = "fixture",
                    Tasks = tasks?.ToList() ?? [],
                },
                Classifier = Classifier,
                Pous = pous.ToList(),
                Gvls = gvls?.ToList() ?? [],
            },
            new AnalysisSettings());

    private static List<AnalysisFinding> OfRule(List<AnalysisFinding> findings, string ruleId)
        => findings.Where(finding => finding.RuleId == ruleId).ToList();

    // --- TCK2001: function block instance on a call stack ---

    [Fact]
    public void StatelessInstance_FbInMethodVar_IsFlagged()
    {
        var pou = Pou(
            "FB_Host", PouType.FunctionBlock, "FUNCTION_BLOCK FB_Host\nVAR\nEND_VAR", "",
            Method("Execute", "METHOD Execute : BOOL\nVAR\n    drive : FB_Drive;\nEND_VAR", "drive();"));

        var finding = Assert.Single(OfRule(Run([pou]), CorrectnessRules.StatelessInstanceId));
        Assert.Equal("drive", finding.Symbol);
        Assert.Equal("Execute", finding.ItemName);
        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
    }

    [Fact]
    public void StatelessInstance_VarInst_IsTheCorrectConstructAndIsNotFlagged()
    {
        var pou = Pou(
            "FB_Host", PouType.FunctionBlock, "FUNCTION_BLOCK FB_Host\nVAR\nEND_VAR", "",
            Method("Execute", "METHOD Execute : BOOL\nVAR_INST\n    drive : FB_Drive;\nEND_VAR", "drive();"));

        Assert.Empty(OfRule(Run([pou]), CorrectnessRules.StatelessInstanceId));
    }

    [Fact]
    public void StatelessInstance_FbAtFunctionBlockLevel_IsNotFlagged()
    {
        var pou = Pou(
            "FB_Host", PouType.FunctionBlock,
            "FUNCTION_BLOCK FB_Host\nVAR\n    drive : FB_Drive;\nEND_VAR", "drive();");

        Assert.Empty(OfRule(Run([pou]), CorrectnessRules.StatelessInstanceId));
    }

    [Fact]
    public void StatelessInstance_FbInsideAFunction_IsFlagged()
    {
        var pou = Pou(
            "F_Compute", PouType.Function,
            "FUNCTION F_Compute : BOOL\nVAR\n    drive : FB_Drive;\nEND_VAR", "drive();");

        Assert.Single(OfRule(Run([pou]), CorrectnessRules.StatelessInstanceId));
    }

    // --- TCK2002: floating-point equality ---

    [Theory]
    [InlineData("IF speed = 1.0 THEN\n    ;\nEND_IF")]
    [InlineData("IF speed <> other THEN\n    ;\nEND_IF")]
    public void RealEquality_ExactComparison_IsFlagged(string body)
    {
        var pou = Pou(
            "FB_Host", PouType.FunctionBlock,
            "FUNCTION_BLOCK FB_Host\nVAR\n    speed : LREAL;\n    other : LREAL;\nEND_VAR", body);

        Assert.Single(OfRule(Run([pou]), CorrectnessRules.RealEqualityId));
    }

    [Theory]
    [InlineData("IF count = 5 THEN\n    ;\nEND_IF")]
    [InlineData("IF speed >= 1.0 THEN\n    ;\nEND_IF")]
    [InlineData("IF speed <= 1.0 THEN\n    ;\nEND_IF")]
    [InlineData("speed := 1.0;")]
    [InlineData("// speed = 1.0")]
    public void RealEquality_NonEqualityOrNonReal_IsNotFlagged(string body)
    {
        var pou = Pou(
            "FB_Host", PouType.FunctionBlock,
            "FUNCTION_BLOCK FB_Host\nVAR\n    speed : LREAL;\n    count : INT;\nEND_VAR", body);

        Assert.Empty(OfRule(Run([pou]), CorrectnessRules.RealEqualityId));
    }

    // --- TCK2003: retention that cannot retain ---

    [Fact]
    public void MisplacedRetain_InAMethod_IsFlagged()
    {
        var pou = Pou(
            "FB_Host", PouType.FunctionBlock, "FUNCTION_BLOCK FB_Host\nVAR\nEND_VAR", "",
            Method("Execute", "METHOD Execute : BOOL\nVAR RETAIN\n    total : UDINT;\nEND_VAR", "total := total + 1;"));

        Assert.Single(OfRule(Run([pou]), CorrectnessRules.MisplacedRetainId));
    }

    [Fact]
    public void MisplacedRetain_AtFunctionBlockLevel_IsNotFlagged()
    {
        var pou = Pou(
            "FB_Host", PouType.FunctionBlock,
            "FUNCTION_BLOCK FB_Host\nVAR RETAIN\n    total : UDINT;\nEND_VAR", "total := total + 1;");

        Assert.Empty(OfRule(Run([pou]), CorrectnessRules.MisplacedRetainId));
    }

    // --- TCK2004: unused local ---

    [Fact]
    public void UnusedLocal_NeverReferenced_IsFlagged()
    {
        var pou = Pou(
            "FB_Host", PouType.FunctionBlock, "FUNCTION_BLOCK FB_Host\nVAR\nEND_VAR", "",
            Method("Execute", "METHOD Execute : BOOL\nVAR\n    spare : INT;\n    used : INT;\nEND_VAR", "used := 1;"));

        var finding = Assert.Single(OfRule(Run([pou]), CorrectnessRules.UnusedLocalId));
        Assert.Equal("spare", finding.Symbol);
    }

    [Fact]
    public void UnusedLocal_MentionedOnlyInAComment_IsStillUnused()
    {
        var pou = Pou(
            "FB_Host", PouType.FunctionBlock, "FUNCTION_BLOCK FB_Host\nVAR\nEND_VAR", "",
            Method(
                "Execute",
                "METHOD Execute : BOOL\nVAR\n    spare : INT;\n    used : INT;\nEND_VAR",
                "// spare is for later\nused := 1;"));

        var finding = Assert.Single(OfRule(Run([pou]), CorrectnessRules.UnusedLocalId));
        Assert.Equal("spare", finding.Symbol);
    }

    [Fact]
    public void UnusedLocal_StubMethod_IsNotFlagged()
    {
        // An unimplemented method uses none of its locals by definition; saying so is noise.
        var pou = Pou(
            "FB_Host", PouType.FunctionBlock, "FUNCTION_BLOCK FB_Host\nVAR\nEND_VAR", "",
            Method("Execute", "METHOD Execute : BOOL\nVAR\n    spare : INT;\nEND_VAR", ";"));

        Assert.Empty(OfRule(Run([pou]), CorrectnessRules.UnusedLocalId));
    }

    [Fact]
    public void UnusedLocal_FunctionBlockLevelVar_IsNotFlagged()
    {
        // TwinCAT 3 leaves VAR members reachable from outside, so an unused one may be API.
        var pou = Pou(
            "FB_Host", PouType.FunctionBlock,
            "FUNCTION_BLOCK FB_Host\nVAR\n    spare : INT;\nEND_VAR", "n := 1;");

        Assert.Empty(OfRule(Run([pou]), CorrectnessRules.UnusedLocalId));
    }

    // --- TCK2005: input nothing reads ---

    [Fact]
    public void UnreadInput_NeverRead_IsFlagged()
    {
        var pou = Pou(
            "FB_Host", PouType.FunctionBlock,
            "FUNCTION_BLOCK FB_Host\nVAR_INPUT\n    Enable : BOOL;\n    Ignored : BOOL;\nEND_VAR",
            "IF Enable THEN\n    ;\nEND_IF");

        var finding = Assert.Single(OfRule(Run([pou]), CorrectnessRules.UnreadInputId));
        Assert.Equal("Ignored", finding.Symbol);
    }

    [Fact]
    public void UnreadInput_ReadInsideAMethod_IsNotFlagged()
    {
        var pou = Pou(
            "FB_Host", PouType.FunctionBlock,
            "FUNCTION_BLOCK FB_Host\nVAR_INPUT\n    Enable : BOOL;\nEND_VAR", "",
            Method("Execute", "METHOD Execute : BOOL", "Execute := Enable;"));

        Assert.Empty(OfRule(Run([pou]), CorrectnessRules.UnreadInputId));
    }

    [Fact]
    public void UnreadInput_WhenTheFunctionBlockIsExtended_IsNotFlagged()
    {
        // A child may be the reader, so the parent's input is left alone.
        var parent = Pou(
            "FB_Base", PouType.FunctionBlock,
            "FUNCTION_BLOCK FB_Base\nVAR_INPUT\n    Enable : BOOL;\nEND_VAR", "n := 1;");
        var child = Pou(
            "FB_Child", PouType.FunctionBlock, "FUNCTION_BLOCK FB_Child EXTENDS FB_Base\nVAR\nEND_VAR", "n := 2;");

        Assert.Empty(OfRule(Run([parent, child]), CorrectnessRules.UnreadInputId));
    }

    [Fact]
    public void UnreadInput_StubFunctionBlock_IsNotFlagged()
    {
        var pou = Pou(
            "FB_Host", PouType.FunctionBlock,
            "FUNCTION_BLOCK FB_Host\nVAR_INPUT\n    Enable : BOOL;\nEND_VAR", "");

        Assert.Empty(OfRule(Run([pou]), CorrectnessRules.UnreadInputId));
    }

    // --- TCK3001: global with more than one writer ---

    [Fact]
    public void MultiWriterGlobal_WrittenFromTwoPous_IsFlagged()
    {
        var gvl = Gvl("GVL_State", "VAR_GLOBAL\n    Mode : INT;\nEND_VAR");
        var first = Pou("FB_A", PouType.FunctionBlock, "FUNCTION_BLOCK FB_A\nVAR\nEND_VAR", "GVL_State.Mode := 1;");
        var second = Pou("FB_B", PouType.FunctionBlock, "FUNCTION_BLOCK FB_B\nVAR\nEND_VAR", "GVL_State.Mode := 2;");

        var finding = Assert.Single(
            OfRule(Run([first, second], [gvl]), CorrectnessRules.MultiWriterGlobalId));
        Assert.Contains("FB_A", finding.Message, StringComparison.Ordinal);
        Assert.Contains("FB_B", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiWriterGlobal_OneWriterAndManyReaders_IsNotFlagged()
    {
        var gvl = Gvl("GVL_State", "VAR_GLOBAL\n    Mode : INT;\nEND_VAR");
        var writer = Pou("FB_A", PouType.FunctionBlock, "FUNCTION_BLOCK FB_A\nVAR\nEND_VAR", "GVL_State.Mode := 1;");
        var reader = Pou(
            "FB_B", PouType.FunctionBlock, "FUNCTION_BLOCK FB_B\nVAR\nEND_VAR",
            "IF GVL_State.Mode = 1 THEN\n    ;\nEND_IF");

        Assert.Empty(OfRule(Run([writer, reader], [gvl]), CorrectnessRules.MultiWriterGlobalId));
    }

    // --- TCK3002: nothing reaches this POU ---

    [Fact]
    public void UnreachableObject_NeverReferenced_IsFlagged()
    {
        var orphan = Pou("FB_Orphan", PouType.FunctionBlock, "FUNCTION_BLOCK FB_Orphan\nVAR\nEND_VAR", "n := 1;");

        var finding = Assert.Single(OfRule(Run([orphan]), CorrectnessRules.UnreachableObjectId));
        Assert.Equal("FB_Orphan", finding.Symbol);
    }

    [Fact]
    public void UnreachableObject_InstantiatedElsewhere_IsNotFlagged()
    {
        var used = Pou("FB_Drive", PouType.FunctionBlock, "FUNCTION_BLOCK FB_Drive\nVAR\nEND_VAR", "n := 1;");
        var host = Pou(
            "FB_Consumer", PouType.FunctionBlock,
            "FUNCTION_BLOCK FB_Consumer\nVAR\n    drive : FB_Drive;\nEND_VAR", "drive();");

        Assert.DoesNotContain(
            OfRule(Run([used, host]), CorrectnessRules.UnreachableObjectId),
            finding => finding.Symbol == "FB_Drive");
    }

    [Fact]
    public void UnreachableObject_CalledThroughANamespaceQualifier_IsNotFlagged()
    {
        // A test PLC calls into its library sibling as "LibraryPlc.F_Trim". Missing this reported
        // every library POU in the T3 fixture as dead code.
        var library = Pou("F_Trim", PouType.Function, "FUNCTION F_Trim : STRING", "F_Trim := '';");
        var consumer = Pou(
            "FB_Tests", PouType.FunctionBlock, "FUNCTION_BLOCK FB_Tests\nVAR\nEND_VAR",
            "result := LibraryPlc.F_Trim(text);");

        Assert.DoesNotContain(
            OfRule(Run([library, consumer]), CorrectnessRules.UnreachableObjectId),
            finding => finding.Symbol == "F_Trim");
    }

    [Fact]
    public void UnreachableObject_DeclaredWithAQualifiedType_IsNotFlagged()
    {
        var library = Pou("FB_Pid", PouType.FunctionBlock, "FUNCTION_BLOCK FB_Pid\nVAR\nEND_VAR", "n := 1;");
        var consumer = Pou(
            "FB_PidTests", PouType.FunctionBlock,
            "FUNCTION_BLOCK FB_PidTests\nVAR\n    pid : LibraryPlc.FB_Pid;\nEND_VAR", "pid();");

        Assert.DoesNotContain(
            OfRule(Run([library, consumer]), CorrectnessRules.UnreachableObjectId),
            finding => finding.Symbol == "FB_Pid");
    }

    [Fact]
    public void UnreachableObject_BoundToATask_IsNotFlagged()
    {
        var program = Pou("PRG_Cyclic", PouType.Program, "PROGRAM PRG_Cyclic\nVAR\nEND_VAR", "n := 1;");
        var task = new TaskInfo { Name = "PlcTask", Programs = ["PRG_Cyclic"] };

        Assert.Empty(OfRule(Run([program], tasks: [task]), CorrectnessRules.UnreachableObjectId));
    }

    [Fact]
    public void UnreachableObject_Main_IsNeverFlagged()
    {
        var main = Pou("MAIN", PouType.Program, "PROGRAM MAIN\nVAR\nEND_VAR", "n := 1;");

        Assert.Empty(OfRule(Run([main]), CorrectnessRules.UnreachableObjectId));
    }

    [Fact]
    public void UnreachableObject_ExtendedByAnother_IsNotFlagged()
    {
        var parent = Pou("FB_Base", PouType.FunctionBlock, "FUNCTION_BLOCK FB_Base\nVAR\nEND_VAR", "n := 1;");
        var child = Pou(
            "FB_Child", PouType.FunctionBlock, "FUNCTION_BLOCK FB_Child EXTENDS FB_Base\nVAR\nEND_VAR", "n := 2;");

        Assert.DoesNotContain(
            OfRule(Run([parent, child]), CorrectnessRules.UnreachableObjectId),
            finding => finding.Symbol == "FB_Base");
    }

    [Fact]
    public void Run_ScopedProject_SkipsTheCrossFileRules()
    {
        var orphan = Pou("FB_Orphan", PouType.FunctionBlock, "FUNCTION_BLOCK FB_Orphan\nVAR\nEND_VAR", "n := 1;");
        var project = new AnalysedProject
        {
            Structure = new ProjectStructure { ProjectPath = "fixture" },
            Classifier = Classifier,
            Pous = [orphan],
            IsWholeProject = false,
        };

        var findings = CorrectnessRules.Run(project, new AnalysisSettings());

        Assert.DoesNotContain(findings, finding => CorrectnessRules.WholeProjectRules.Contains(finding.RuleId));
    }
}
