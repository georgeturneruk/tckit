using TcKit.Core.Analysis;

namespace TcKit.Tests;

/// <summary>Tests the VAR-block declaration parser that feeds the naming rules.</summary>
public class DeclarationParserTests
{
    [Fact]
    public void Parse_FunctionBlockHeader_ReadsNameAccessAndInheritance()
    {
        var declaration = "FUNCTION_BLOCK PUBLIC FB_Motor EXTENDS FB_Base IMPLEMENTS I_Drive, I_Reset\n"
            + "VAR_INPUT\n    Enable : BOOL;\nEND_VAR";

        var result = DeclarationParser.Parse(declaration);

        Assert.Equal("FUNCTION_BLOCK", result.Header.Keyword);
        Assert.Equal("FB_Motor", result.Header.Name);
        Assert.Equal("FB_Base", result.Header.Extends);
        Assert.Equal(["I_Drive", "I_Reset"], result.Header.Implements);
        Assert.Equal(StAccessibility.Public, result.Header.Accessibility);
    }

    [Fact]
    public void Parse_MethodHeader_ReadsReturnTypeAndPrivateAccess()
    {
        var result = DeclarationParser.Parse("METHOD PRIVATE Execute : BOOL\nVAR\n    i : INT;\nEND_VAR");

        Assert.Equal("METHOD", result.Header.Keyword);
        Assert.Equal("Execute", result.Header.Name);
        Assert.Equal("BOOL", result.Header.ReturnType);
        Assert.Equal(StAccessibility.Private, result.Header.Accessibility);
    }

    [Fact]
    public void Parse_VariableNamedProgram_IsNotReadAsAHeader()
    {
        // A GVL has no header at all, so a variable that shares a keyword must not become one.
        var result = DeclarationParser.Parse("VAR_GLOBAL\n    Program : BOOL;\nEND_VAR");

        Assert.Equal("", result.Header.Keyword);
        Assert.Equal("Program", Assert.Single(result.Variables).Name);
    }

    [Fact]
    public void Parse_EachSection_IsAttributedToItsBlock()
    {
        var declaration = "FUNCTION_BLOCK FB_X\n"
            + "VAR_INPUT\n    a : INT;\nEND_VAR\n"
            + "VAR_OUTPUT\n    b : BOOL;\nEND_VAR\n"
            + "VAR_IN_OUT\n    c : REAL;\nEND_VAR\n"
            + "VAR\n    d : INT;\nEND_VAR\n"
            + "VAR_STAT\n    e : INT;\nEND_VAR\n"
            + "VAR_TEMP\n    f : INT;\nEND_VAR";

        var sections = DeclarationParser.Parse(declaration)
            .Variables.ToDictionary(v => v.Name, v => v.Section, StringComparer.Ordinal);

        Assert.Equal(VarSection.VarInput, sections["a"]);
        Assert.Equal(VarSection.VarOutput, sections["b"]);
        Assert.Equal(VarSection.VarInOut, sections["c"]);
        Assert.Equal(VarSection.Var, sections["d"]);
        Assert.Equal(VarSection.VarStat, sections["e"]);
        Assert.Equal(VarSection.VarTemp, sections["f"]);
    }

    [Fact]
    public void Parse_VarConstant_CarriesTheConstantQualifier()
    {
        var result = DeclarationParser.Parse("VAR CONSTANT\n    Limit : UINT := 3;\nEND_VAR");

        var variable = Assert.Single(result.Variables);
        Assert.Equal(VarSection.Var, variable.Section);
        Assert.True(variable.Qualifiers.HasFlag(VarQualifiers.Constant));
    }

    [Fact]
    public void Parse_LegacyVarPersistent_MapsToVarPlusPersistent()
    {
        var result = DeclarationParser.Parse("VAR_PERSISTENT\n    Total : UDINT;\nEND_VAR");

        var variable = Assert.Single(result.Variables);
        Assert.Equal(VarSection.Var, variable.Section);
        Assert.True(variable.Qualifiers.HasFlag(VarQualifiers.Persistent));
    }

    [Fact]
    public void Parse_MultiNameLine_YieldsOneVariablePerName()
    {
        var result = DeclarationParser.Parse("VAR\n    a, b, c : INT;\nEND_VAR");

        Assert.Equal(["a", "b", "c"], result.Variables.Select(v => v.Name));
        Assert.All(result.Variables, v => Assert.Equal("INT", v.TypeExpression));
    }

    [Fact]
    public void Parse_AtDeclaration_CapturesAddressAndName()
    {
        var result = DeclarationParser.Parse("VAR\n    Sensor AT %I* : BOOL;\nEND_VAR");

        var variable = Assert.Single(result.Variables);
        Assert.Equal("Sensor", variable.Name);
        Assert.Equal("%I*", variable.Address);
        Assert.Equal("BOOL", variable.TypeExpression);
    }

    [Theory]
    [InlineData("Buffer : ARRAY [0..9] OF BYTE;", "ARRAY [0..9] OF BYTE")]
    [InlineData("Handle : POINTER TO ST_Item;", "POINTER TO ST_Item")]
    [InlineData("Text : STRING(80);", "STRING(80)")]
    [InlineData("Count : UINT := 5;", "UINT")]
    [InlineData("Motor : FB_Drive := (Max := 10);", "FB_Drive")]
    public void Parse_TypeExpression_StopsAtTheInitialiser(string line, string expected)
    {
        var result = DeclarationParser.Parse($"VAR\n    {line}\nEND_VAR");

        Assert.Equal(expected, Assert.Single(result.Variables).TypeExpression);
    }

    [Fact]
    public void Parse_DeclarationSpanningLines_IsStillOneVariable()
    {
        var result = DeclarationParser.Parse(
            "VAR\n    Buffer : ARRAY [0..9]\n        OF BYTE;\nEND_VAR");

        var variable = Assert.Single(result.Variables);
        Assert.Equal("Buffer", variable.Name);
        Assert.Equal("ARRAY [0..9] OF BYTE", variable.TypeExpression);
    }

    [Fact]
    public void Parse_CommentedOutDeclaration_IsIgnored()
    {
        var result = DeclarationParser.Parse("VAR\n    // Ghost : INT;\n    Real : INT;\nEND_VAR");

        Assert.Equal("Real", Assert.Single(result.Variables).Name);
    }

    [Fact]
    public void Parse_ReportsTheLineTheVariableIsOn()
    {
        var result = DeclarationParser.Parse("FUNCTION_BLOCK FB_X\nVAR\n    a : INT;\n    b : INT;\nEND_VAR");

        Assert.Equal(3, result.Variables[0].Line);
        Assert.Equal(4, result.Variables[1].Line);
    }

    [Fact]
    public void Parse_CrLfLineEndings_AreHandled()
    {
        var result = DeclarationParser.Parse("FUNCTION_BLOCK FB_X\r\nVAR_INPUT\r\n    Enable : BOOL;\r\nEND_VAR");

        var variable = Assert.Single(result.Variables);
        Assert.Equal("Enable", variable.Name);
        Assert.Equal(VarSection.VarInput, variable.Section);
    }

    [Fact]
    public void ParseType_Struct_ReadsNameAndMembers()
    {
        var result = DeclarationParser.ParseType(
            "TYPE ST_Config :\nSTRUCT\n    Timeout : TIME;\n    Retries : UINT;\nEND_STRUCT\nEND_TYPE");

        Assert.Equal("ST_Config", result.Name);
        Assert.Equal(["Timeout", "Retries"], result.Members.Select(m => m.Name));
        Assert.Equal("TIME", result.Members[0].TypeExpression);
    }

    [Fact]
    public void ParseType_Enum_ReadsConstants()
    {
        var result = DeclarationParser.ParseType("TYPE E_State :\n(\n    Idle := 0,\n    Running,\n    Faulted\n);\nEND_TYPE");

        Assert.Equal("E_State", result.Name);
        Assert.Equal(["Idle", "Running", "Faulted"], result.Members.Select(m => m.Name));
    }

    [Fact]
    public void ParseType_Alias_HasNoMembers()
    {
        var result = DeclarationParser.ParseType("TYPE T_Speed : LREAL;\nEND_TYPE");

        Assert.Equal("T_Speed", result.Name);
        Assert.Empty(result.Members);
    }
}
