using TcKit.Adapters.Analysis;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>
/// End-to-end behaviour of the shipped naming profiles, from parsed source through to findings.
/// These are the executable statement of what hybrid, dotnet and hungarian actually mean.
/// </summary>
public class NamingProfileTests
{
    private static readonly TypeClassifier Classifier = new(
        new Dictionary<string, TypeClass>(StringComparer.OrdinalIgnoreCase)
        {
            ["FB_Drive"] = TypeClass.FbInstance,
            ["ST_Config"] = TypeClass.Struct,
            ["E_State"] = TypeClass.Enum,
        });

    private static AnalysisSettings Settings(string profile)
        => new() { Profile = profile, Rules = NamingProfiles.For(profile) };

    private static PouSource Fb(string name, string declaration, params PouMember[] members) => new()
    {
        PouName = name,
        PouType = PouType.FunctionBlock,
        Path = $"{name}.TcPOU",
        Declaration = declaration,
        Body = "",
        Members = members,
    };

    private static PouMember Method(string name, string declaration) => new()
    {
        Name = name,
        Kind = PouMemberKind.Method,
        Declaration = declaration,
        Body = "",
    };

    private static List<AnalysisFinding> Run(string profile, PouSource pou)
        => NamingRuleEngine.Run(SymbolCollector.FromPou(pou, "Plc", Classifier), Settings(profile));

    [Fact]
    public void Hybrid_ConventionalProject_ProducesNoFindings()
    {
        var pou = Fb(
            "FB_Motor",
            "FUNCTION_BLOCK FB_Motor\nVAR_INPUT\n    Enable : BOOL;\nEND_VAR\n"
            + "VAR\n    _state : E_State;\n    _drive : FB_Drive;\nEND_VAR",
            Method("Execute", "METHOD Execute : BOOL\nVAR\n    retries : UINT;\nEND_VAR"));

        Assert.Empty(Run(NamingProfiles.Hybrid, pou));
    }

    [Fact]
    public void Hybrid_FunctionBlockWithoutKindPrefix_IsFlagged()
    {
        var findings = Run(NamingProfiles.Hybrid, Fb("Motor", "FUNCTION_BLOCK Motor\nVAR\nEND_VAR"));

        var finding = Assert.Single(findings);
        Assert.Equal(NamingRuleEngine.ObjectRuleId, finding.RuleId);
        Assert.Equal("FB_Motor", finding.Suggestion);
    }

    [Fact]
    public void Hybrid_HungarianInputVariable_IsFlaggedWithStrippedSuggestion()
    {
        var findings = Run(
            NamingProfiles.Hybrid,
            Fb("FB_Motor", "FUNCTION_BLOCK FB_Motor\nVAR_INPUT\n    bEnable : BOOL;\nEND_VAR"));

        var finding = Assert.Single(findings);
        Assert.Equal(NamingRuleEngine.VariableRuleId, finding.RuleId);
        Assert.Equal("bEnable", finding.Symbol);
        Assert.Equal("Enable", finding.Suggestion);
        Assert.Equal(3, finding.Line);
    }

    [Fact]
    public void Hybrid_InstanceFieldWithoutUnderscore_IsFlagged()
    {
        var findings = Run(
            NamingProfiles.Hybrid,
            Fb("FB_Motor", "FUNCTION_BLOCK FB_Motor\nVAR\n    state : INT;\nEND_VAR"));

        Assert.Equal("_state", Assert.Single(findings).Suggestion);
    }

    [Fact]
    public void Hybrid_SameSectionDiffersByScope_FbSurfaceIsPascalAndParameterIsCamel()
    {
        // An FB's VAR_INPUT is public surface; a method's is a parameter list. Same keyword,
        // different convention, which is why the rules select on scope.
        var pou = Fb(
            "FB_Motor",
            "FUNCTION_BLOCK FB_Motor\nVAR_INPUT\n    Setpoint : LREAL;\nEND_VAR",
            Method("Execute", "METHOD Execute : BOOL\nVAR_INPUT\n    Value : INT;\nEND_VAR"));

        var finding = Assert.Single(Run(NamingProfiles.Hybrid, pou));
        Assert.Equal("Value", finding.Symbol);
        Assert.Equal("Execute", finding.ItemName);
        Assert.Equal("value", finding.Suggestion);
    }

    [Fact]
    public void Hybrid_Constant_IsPascalCaseNotScreamingSnake()
    {
        // The modifier constraint outranks the section rule, so a constant is PascalCase wherever
        // it is declared: .NET convention, not the PLC habit of SCREAMING_SNAKE.
        var findings = Run(
            NamingProfiles.Hybrid,
            Fb("FB_Motor", "FUNCTION_BLOCK FB_Motor\nVAR CONSTANT\n    maxRetries : UINT := 3;\nEND_VAR"));

        Assert.Equal("MaxRetries", Assert.Single(findings).Suggestion);
    }

    [Fact]
    public void Dotnet_ObjectKindPrefix_IsFlagged()
    {
        var findings = Run(NamingProfiles.Dotnet, Fb("FB_Motor", "FUNCTION_BLOCK FB_Motor\nVAR\nEND_VAR"));

        Assert.Equal("Motor", Assert.Single(findings).Suggestion);
    }

    [Fact]
    public void Dotnet_PlainObjectName_IsAccepted()
        => Assert.Empty(Run(NamingProfiles.Dotnet, Fb("Motor", "FUNCTION_BLOCK Motor\nVAR\nEND_VAR")));

    [Fact]
    public void Hungarian_TypedPrefix_IsRequired()
    {
        var findings = Run(
            NamingProfiles.Hungarian,
            Fb("FB_Motor", "FUNCTION_BLOCK FB_Motor\nVAR_INPUT\n    Enable : BOOL;\nEND_VAR"));

        Assert.Equal("bEnable", Assert.Single(findings).Suggestion);
    }

    [Fact]
    public void Hungarian_CorrectlyPrefixedNames_AreAccepted()
    {
        var pou = Fb(
            "FB_Motor",
            "FUNCTION_BLOCK FB_Motor\nVAR_INPUT\n    bEnable : BOOL;\n    nCount : UINT;\nEND_VAR\n"
            + "VAR\n    fbDrive : FB_Drive;\n    stConfig : ST_Config;\n    eState : E_State;\nEND_VAR");

        Assert.Empty(Run(NamingProfiles.Hungarian, pou));
    }

    [Fact]
    public void Hungarian_UnresolvableType_IsNotFlagged()
    {
        // A library type we cannot classify gets no rule at all: precision over recall.
        var findings = Run(
            NamingProfiles.Hungarian,
            Fb("FB_Motor", "FUNCTION_BLOCK FB_Motor\nVAR\n    Whatever : FB_FromSomeLibrary;\nEND_VAR"));

        Assert.Empty(findings);
    }

    [Fact]
    public void Hybrid_Gvl_ChecksTheListAndItsGlobals()
    {
        var gvl = new Gvl
        {
            Name = "Parameters",
            Path = "Parameters.TcGVL",
            Declaration = "VAR_GLOBAL\n    nMaxRetries : UINT := 3;\nEND_VAR",
        };

        var findings = NamingRuleEngine.Run(
            SymbolCollector.FromGvl(gvl, "Plc", Classifier), Settings(NamingProfiles.Hybrid));

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.Symbol == "Parameters" && f.Suggestion == "GVL_Parameters");
        Assert.Contains(findings, f => f.Symbol == "nMaxRetries" && f.Suggestion == "MaxRetries");
    }

    [Fact]
    public void Hybrid_StructMembers_AreChecked()
    {
        var dut = new Dut
        {
            Name = "ST_Config",
            Path = "ST_Config.TcDUT",
            DutKind = DutKind.Struct,
            Declaration = "TYPE ST_Config :\nSTRUCT\n    tTimeout : TIME;\n    Retries : UINT;\nEND_STRUCT\nEND_TYPE",
        };

        var findings = NamingRuleEngine.Run(
            SymbolCollector.FromDut(dut, "Plc", Classifier), Settings(NamingProfiles.Hybrid));

        var finding = Assert.Single(findings);
        Assert.Equal(NamingRuleEngine.TypeMemberRuleId, finding.RuleId);
        Assert.Equal("tTimeout", finding.Symbol);
        Assert.Equal("Timeout", finding.Suggestion);
    }

    [Fact]
    public void Hybrid_FunctionParameters_AreCamelCaseNotPublicSurface()
    {
        // A FUNCTION has no instance surface, so its VAR_INPUT is a parameter list like a method's.
        var function = new PouSource
        {
            PouName = "F_Contains",
            PouType = PouType.Function,
            Path = "F_Contains.TcPOU",
            Declaration = "FUNCTION F_Contains : BOOL\nVAR_INPUT\n    text : STRING;\n    needle : STRING;\nEND_VAR",
            Body = "",
        };

        Assert.Empty(Run(NamingProfiles.Hybrid, function));
    }

    [Fact]
    public void Hybrid_MainProgram_IsExemptFromTheProgramPrefix()
    {
        // TwinCAT mandates the name MAIN; suggesting PRG_MAIN would break the project.
        var main = new PouSource
        {
            PouName = "MAIN",
            PouType = PouType.Program,
            Path = "MAIN.TcPOU",
            Declaration = "PROGRAM MAIN\nVAR\nEND_VAR",
            Body = "",
        };

        Assert.Empty(Run(NamingProfiles.Hybrid, main));
    }

    [Fact]
    public void Hybrid_HungarianPrefixOnAConformingLocal_IsFlaggedAsRedundant()
    {
        // "nCount" is already valid camelCase, so the casing rule cannot see the prefix. This is
        // the gap TCK1005 exists to close.
        var pou = Fb(
            "FB_Motor", "FUNCTION_BLOCK FB_Motor\nVAR\nEND_VAR",
            Method("Execute", "METHOD Execute : BOOL\nVAR\n    nCount : INT;\nEND_VAR"));

        var finding = Assert.Single(Run(NamingProfiles.Hybrid, pou));
        Assert.Equal(NamingRuleEngine.RedundantTypePrefixId, finding.RuleId);
        Assert.Equal("count", finding.Suggestion);
    }

    [Fact]
    public void Hybrid_WordThatMerelyStartsLikeAPrefix_IsNotFlagged()
    {
        // "nextValue" starts with n on an INT, but "ne" is not a prefix and the agreement test
        // only matches a prefix followed by an upper-case boundary.
        var pou = Fb(
            "FB_Motor", "FUNCTION_BLOCK FB_Motor\nVAR\nEND_VAR",
            Method("Execute", "METHOD Execute : BOOL\nVAR\n    nextValue : INT;\nEND_VAR"));

        Assert.Empty(Run(NamingProfiles.Hybrid, pou));
    }

    [Theory]
    [InlineData("bBOOL : BOOL")]
    [InlineData("bBool : BOOL")]
    [InlineData("aDINT : ARRAY [0..3] OF DINT")]
    [InlineData("aLREAL2d : ARRAY [0..3] OF LREAL")]
    public void Hybrid_VariableNamedAfterItsOwnType_IsNotFlagged(string declaration)
    {
        // TcUnit declares its test subjects as "aDINT : ARRAY OF DINT". The leading letter reads as
        // a type prefix, but the name is the type, and there is nothing useful to suggest:
        // stripping "aDINT" leaves "DINT", which recases to "dINT". Worse than silence.
        var pou = Fb(
            "FB_Motor", "FUNCTION_BLOCK FB_Motor\nVAR\nEND_VAR",
            Method("Execute", $"METHOD Execute : BOOL\nVAR\n    {declaration};\nEND_VAR"));

        Assert.Empty(Run(NamingProfiles.Hybrid, pou));
    }

    [Fact]
    public void Hybrid_TypeNameThatIsOnlyTheStartOfAWord_IsStillFlagged()
    {
        // "IntervalMs" begins with the type name INT, but the next character is lower case, so it
        // is the start of a word rather than the whole of one. The prefix is still redundant.
        var pou = Fb(
            "FB_Motor", "FUNCTION_BLOCK FB_Motor\nVAR\nEND_VAR",
            Method("Execute", "METHOD Execute : BOOL\nVAR\n    nIntervalMs : INT;\nEND_VAR"));

        var finding = Assert.Single(Run(NamingProfiles.Hybrid, pou));
        Assert.Equal(NamingRuleEngine.RedundantTypePrefixId, finding.RuleId);
        Assert.Equal("intervalMs", finding.Suggestion);
    }

    [Fact]
    public void Hybrid_PrefixDisagreeingWithTheType_IsNotFlagged()
    {
        // "bufferSize" on an INT starts with "b", which tags a BOOL, so it is a word not a tag.
        var pou = Fb(
            "FB_Motor", "FUNCTION_BLOCK FB_Motor\nVAR\nEND_VAR",
            Method("Execute", "METHOD Execute : BOOL\nVAR\n    bufferSize : INT;\nEND_VAR"));

        Assert.Empty(Run(NamingProfiles.Hybrid, pou));
    }

    [Fact]
    public void Hybrid_InstanceFieldKeepsItsUnderscoreButNotItsTypePrefix()
    {
        var pou = Fb("FB_Motor", "FUNCTION_BLOCK FB_Motor\nVAR\n    _nCount : INT;\nEND_VAR");

        var finding = Assert.Single(Run(NamingProfiles.Hybrid, pou));
        Assert.Equal(NamingRuleEngine.RedundantTypePrefixId, finding.RuleId);
        Assert.Equal("_count", finding.Suggestion);
    }

    [Fact]
    public void Hungarian_TypePrefixIsTheConvention_SoItIsNotFlaggedAsRedundant()
    {
        var pou = Fb(
            "FB_Motor", "FUNCTION_BLOCK FB_Motor\nVAR_INPUT\n    nCount : INT;\nEND_VAR");

        Assert.Empty(Run(NamingProfiles.Hungarian, pou));
    }

    [Fact]
    public void Hybrid_NonConformingName_IsReportedOnceNotTwice()
    {
        // "bEnable" fails the casing rule and carries a type prefix. Both describe the same defect
        // with the same fix, so only the casing finding is emitted.
        var pou = Fb("FB_Motor", "FUNCTION_BLOCK FB_Motor\nVAR_INPUT\n    bEnable : BOOL;\nEND_VAR");

        var finding = Assert.Single(Run(NamingProfiles.Hybrid, pou));
        Assert.Equal(NamingRuleEngine.VariableRuleId, finding.RuleId);
    }

    [Fact]
    public void None_Profile_DisablesEveryRule()
        => Assert.Empty(Run(NamingProfiles.None, Fb("motor", "FUNCTION_BLOCK motor\nVAR\n    X : INT;\nEND_VAR")));
}
