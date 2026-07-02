using TcKit.Adapters.DocGen;

namespace TcKit.Tests;

/// <summary>Project doc-model building. Ports the Python <c>test_doc_model.py</c>.</summary>
public sealed class DocModelTests
{
    private static IEnumerable<ObjectDoc> Objects(ProjectDoc project)
        => project.Plcs.Values.SelectMany(plc => plc.Objects);

    // -- Variable parser ------------------------------------------------------

    [Fact]
    public void ParseVariables_VarInputExtracted()
    {
        var result = DocModel.ParseVariables("FUNCTION_BLOCK FB_Test\nVAR_INPUT\n    bEnable : BOOL;\n    nCount  : INT;\nEND_VAR");
        var names = result.Input.Select(v => v.Name).ToList();
        Assert.Contains("bEnable", names);
        Assert.Contains("nCount", names);
    }

    [Fact]
    public void ParseVariables_VarOutputExtracted()
    {
        var result = DocModel.ParseVariables("FUNCTION_BLOCK FB_Test\nVAR_OUTPUT\n    bDone : BOOL;\nEND_VAR");
        Assert.Equal("bDone", result.Output[0].Name);
        Assert.Equal("BOOL", result.Output[0].VarType);
    }

    [Fact]
    public void ParseVariables_InlineCommentCaptured()
    {
        var result = DocModel.ParseVariables("FUNCTION_BLOCK FB_Test\nVAR_INPUT\n    bEnable : BOOL; // Trigger input\nEND_VAR");
        Assert.Equal("Trigger input", result.Input[0].Comment);
    }

    [Fact]
    public void ParseVariables_ArrayTypeCaptured()
    {
        var result = DocModel.ParseVariables("FUNCTION_BLOCK FB_Test\nVAR\n    aData : ARRAY[0..9] OF BOOL;\nEND_VAR");
        Assert.Contains("ARRAY", result.Variable[0].VarType, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseVariables_InitialValueNotInType()
    {
        var result = DocModel.ParseVariables("FUNCTION_BLOCK FB_Test\nVAR\n    nCount : INT := 0;\nEND_VAR");
        Assert.Equal("INT", result.Variable[0].VarType);
    }

    [Fact]
    public void ParseVariables_MultipleVarBlocks()
    {
        var result = DocModel.ParseVariables(
            "FUNCTION_BLOCK FB_Test\nVAR_INPUT\n    x : BOOL;\nEND_VAR\nVAR_OUTPUT\n    y : BOOL;\nEND_VAR\nVAR\n    z : INT;\nEND_VAR");
        Assert.Single(result.Input);
        Assert.Single(result.Output);
        Assert.Single(result.Variable);
    }

    [Fact]
    public void ParseVariables_UnderscorePrefixedPrivateVar()
        => Assert.Equal("_state", DocModel.ParseVariables("FUNCTION_BLOCK FB_Test\nVAR\n    _state : INT;\nEND_VAR").Variable[0].Name);

    [Fact]
    public void ParseVariables_NoVars_ReturnsEmptyLists()
    {
        var result = DocModel.ParseVariables("FUNCTION_BLOCK FB_Empty");
        Assert.Empty(result.Input);
        Assert.Empty(result.Output);
        Assert.Empty(result.Variable);
    }

    [Fact]
    public void ParseVariables_VarGlobalExtracted()
    {
        var result = DocModel.ParseVariables(
            "VAR_GLOBAL CONSTANT\n    nMaxRetries : INT := 3;\n    sName : STRING := 'TcKit';\nEND_VAR");
        var names = result.Variable.Select(v => v.Name).ToList();
        Assert.Contains("nMaxRetries", names);
        Assert.Contains("sName", names);
    }

    [Fact]
    public void ParseVariables_DefaultValueCaptured()
        => Assert.Equal("42", DocModel.ParseVariables("VAR\n    nCount : INT := 42;\nEND_VAR").Variable[0].DefaultValue);

    [Fact]
    public void ParseVariables_InlineBlockCommentCaptured()
    {
        var result = DocModel.ParseVariables("VAR\n    fTimeout : LREAL := 5.0; (* seconds *)\nEND_VAR");
        Assert.Equal("seconds", result.Variable[0].Comment);
        Assert.Equal("5.0", result.Variable[0].DefaultValue);
    }

    [Fact]
    public void ParseVariables_BlockCommentOnNextLine_NotAttributedToVarAbove()
    {
        var result = DocModel.ParseVariables(
            "VAR_GLOBAL\n    a : INT := 1;\n    (* doc for b, NOT for a *)\n    b : INT := 2;\nEND_VAR");
        var byName = result.Variable.ToDictionary(v => v.Name);
        Assert.Equal("", byName["a"].Comment);
        Assert.Equal("", byName["b"].Comment);
    }

    // -- Struct field parser --------------------------------------------------

    [Fact]
    public void ParseStructFields_FieldsExtracted()
    {
        const string decl =
            "TYPE ST_Config :\nSTRUCT\n    nMaxRetries : INT := 3;\n    fTimeout    : LREAL := 5.0; (* seconds *)\n    bEnabled    : BOOL := TRUE;\nEND_STRUCT\nEND_TYPE";
        Assert.Equal(new[] { "nMaxRetries", "fTimeout", "bEnabled" }, DocModel.ParseStructFields(decl).Select(f => f.Name));
    }

    [Fact]
    public void ParseStructFields_DefaultsCaptured()
        => Assert.Equal("3", DocModel.ParseStructFields(
            "TYPE ST_Config :\nSTRUCT\n    nMaxRetries : INT := 3;\nEND_STRUCT\nEND_TYPE")[0].DefaultValue);

    [Fact]
    public void ParseStructFields_InlineBlockComment()
        => Assert.Equal("seconds", DocModel.ParseStructFields(
            "TYPE ST_Config :\nSTRUCT\n    fTimeout : LREAL := 5.0; (* seconds *)\nEND_STRUCT\nEND_TYPE")[0].Comment);

    [Fact]
    public void ParseStructFields_UnionFieldsExtracted()
    {
        const string decl =
            "TYPE U_Data :\nUNION\n    asBytes : ARRAY[0..3] OF BYTE;\n    nWord   : DWORD;\nEND_UNION\nEND_TYPE";
        var names = DocModel.ParseStructFields(decl).Select(f => f.Name).ToList();
        Assert.Contains("asBytes", names);
        Assert.Contains("nWord", names);
    }

    [Fact]
    public void ParseStructFields_EmptyStruct_ReturnsEmpty()
        => Assert.Empty(DocModel.ParseStructFields("TYPE ST_Empty :\nSTRUCT\nEND_STRUCT\nEND_TYPE"));

    // -- Enum member parser ---------------------------------------------------

    [Fact]
    public void ParseEnumMembers_Extracted()
    {
        const string decl = "TYPE E_State :\n(\n    Idle := 0,\n    Running := 1,\n    Error := 2\n);\nEND_TYPE";
        Assert.Equal(new[] { "Idle", "Running", "Error" }, DocModel.ParseEnumMembers(decl).Select(m => m.Name));
    }

    [Fact]
    public void ParseEnumMembers_ValuesCaptured()
    {
        var members = DocModel.ParseEnumMembers("TYPE E_State :\n(\n    Idle := 0,\n    Running := 1\n);\nEND_TYPE");
        Assert.Equal("0", members[0].VarType);
        Assert.Equal("1", members[1].VarType);
    }

    [Fact]
    public void ParseEnumMembers_WithoutExplicitValues()
    {
        var members = DocModel.ParseEnumMembers("TYPE E_X :\n(\n    A,\n    B,\n    C\n);\nEND_TYPE");
        Assert.Equal(new[] { "A", "B", "C" }, members.Select(m => m.Name));
        Assert.All(members, m => Assert.Equal("", m.VarType));
    }

    [Fact]
    public void ParseEnumMembers_NonEnum_ReturnsEmpty()
        => Assert.Empty(DocModel.ParseEnumMembers("TYPE ST_X :\nSTRUCT\n    x : INT;\nEND_STRUCT\nEND_TYPE"));

    // -- Declaration meta -----------------------------------------------------

    [Fact]
    public void Meta_NoModifiers()
    {
        var meta = DocModel.ExtractDeclarationMeta("FUNCTION_BLOCK FB_Test");
        Assert.Equal("", meta.Visibility);
        Assert.False(meta.IsAbstract);
        Assert.Equal("", meta.Extends);
        Assert.Empty(meta.Implements);
    }

    [Fact]
    public void Meta_PublicVisibility()
        => Assert.Equal("PUBLIC", DocModel.ExtractDeclarationMeta("FUNCTION_BLOCK PUBLIC TcoTask").Visibility);

    [Fact]
    public void Meta_PrivateVisibility()
        => Assert.Equal("PRIVATE", DocModel.ExtractDeclarationMeta("METHOD PRIVATE AutoRestore").Visibility);

    [Fact]
    public void Meta_ProtectedVisibility()
        => Assert.Equal("PROTECTED", DocModel.ExtractDeclarationMeta("METHOD PROTECTED Step : BOOL").Visibility);

    [Fact]
    public void Meta_AbstractModifier()
        => Assert.True(DocModel.ExtractDeclarationMeta("FUNCTION_BLOCK PUBLIC ABSTRACT TcoTask").IsAbstract);

    [Fact]
    public void Meta_FinalModifier()
        => Assert.True(DocModel.ExtractDeclarationMeta("METHOD PROTECTED FINAL CompleteStep").IsFinal);

    [Fact]
    public void Meta_Extends()
        => Assert.Equal("TcoObject", DocModel.ExtractDeclarationMeta("FUNCTION_BLOCK TcoTask EXTENDS TcoObject").Extends);

    [Fact]
    public void Meta_ImplementsSingle()
        => Assert.Contains("ITcoTask", DocModel.ExtractDeclarationMeta("FUNCTION_BLOCK TcoTask IMPLEMENTS ITcoTask").Implements);

    [Fact]
    public void Meta_ImplementsMultiple()
    {
        var meta = DocModel.ExtractDeclarationMeta("FUNCTION_BLOCK TcoTask EXTENDS TcoObject IMPLEMENTS ITcoTask, ITcoTaskStatus");
        Assert.Equal("TcoObject", meta.Extends);
        Assert.Equal(new HashSet<string> { "ITcoTask", "ITcoTaskStatus" }, meta.Implements.ToHashSet());
    }

    [Fact]
    public void Meta_ExtendsAndImplements()
    {
        var meta = DocModel.ExtractDeclarationMeta(
            "FUNCTION_BLOCK PUBLIC ABSTRACT TcoObject EXTENDS TcoParent IMPLEMENTS IBase, IExtra");
        Assert.Equal("PUBLIC", meta.Visibility);
        Assert.True(meta.IsAbstract);
        Assert.Equal("TcoParent", meta.Extends);
        Assert.Equal(2, meta.Implements.Count);
    }

    [Fact]
    public void Meta_CommentLineIgnored()
        => Assert.Equal("", DocModel.ExtractDeclarationMeta("// FUNCTION_BLOCK in a comment\nFUNCTION_BLOCK FB_Real").Visibility);

    [Fact]
    public void Meta_MethodWithReturnType()
        => Assert.Equal("PUBLIC", DocModel.ExtractDeclarationMeta("METHOD PUBLIC Execute : BOOL").Visibility);

    // -- Full project doc build (against fixtures) ----------------------------

    [Fact]
    public void Build_FindsAllObjects()
    {
        var names = Objects(DocModel.BuildProjectDoc(Fixtures.SampleProject)).Select(o => o.Name).ToList();
        Assert.Contains("FB_Example", names);
        Assert.Contains("GVL_Params", names);
        Assert.Contains("ST_ExampleConfig", names);
        Assert.Contains("E_ExampleState", names);
    }

    [Fact]
    public void Build_FbHasDescription()
    {
        var fb = Objects(DocModel.BuildProjectDoc(Fixtures.SampleProject)).First(o => o.Name == "FB_Example");
        Assert.Contains("TcKit", fb.Comment.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_FbInputsExtracted()
    {
        var fb = Objects(DocModel.BuildProjectDoc(Fixtures.SampleProject)).First(o => o.Name == "FB_Example");
        var names = fb.Inputs.Select(v => v.Name).ToList();
        Assert.Contains("bEnable", names);
        Assert.Contains("nSetpoint", names);
    }

    [Fact]
    public void Build_FbInputDescriptionsFromParams()
    {
        var fb = Objects(DocModel.BuildProjectDoc(Fixtures.SampleProject)).First(o => o.Name == "FB_Example");
        Assert.NotEqual("", fb.Inputs.First(v => v.Name == "bEnable").Comment);
    }

    [Fact]
    public void Build_FbMethodsExtracted()
    {
        var fb = Objects(DocModel.BuildProjectDoc(Fixtures.SampleProject)).First(o => o.Name == "FB_Example");
        var names = fb.Methods.Select(m => m.Name).ToList();
        Assert.Contains("Execute", names);
        Assert.Contains("Reset", names);
    }

    [Fact]
    public void Build_MethodHasReturnType()
    {
        var fb = Objects(DocModel.BuildProjectDoc(Fixtures.SampleProject)).First(o => o.Name == "FB_Example");
        Assert.Equal("BOOL", fb.Methods.First(m => m.Name == "Execute").ReturnType);
    }

    [Fact]
    public void Build_MethodHasDescription()
    {
        var fb = Objects(DocModel.BuildProjectDoc(Fixtures.SampleProject)).First(o => o.Name == "FB_Example");
        Assert.NotEqual("", fb.Methods.First(m => m.Name == "Execute").Comment.Description);
    }

    [Fact]
    public void Build_FbPropertyExtracted()
    {
        var fb = Objects(DocModel.BuildProjectDoc(Fixtures.SampleProject)).First(o => o.Name == "FB_Example");
        Assert.Contains("ErrorId", fb.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Build_PropertyHasGetSet()
    {
        var fb = Objects(DocModel.BuildProjectDoc(Fixtures.SampleProject)).First(o => o.Name == "FB_Example");
        var errorId = fb.Properties.First(p => p.Name == "ErrorId");
        Assert.True(errorId.HasGet);
        Assert.True(errorId.HasSet);
    }

    [Fact]
    public void Build_GvlType()
        => Assert.Equal("gvl", Objects(DocModel.BuildProjectDoc(Fixtures.SampleProject)).First(o => o.Name == "GVL_Params").ObjType);

    [Fact]
    public void Build_StructType()
        => Assert.Equal("struct", Objects(DocModel.BuildProjectDoc(Fixtures.SampleProject)).First(o => o.Name == "ST_ExampleConfig").ObjType);

    [Fact]
    public void Build_EnumType()
        => Assert.Equal("enum", Objects(DocModel.BuildProjectDoc(Fixtures.SampleProject)).First(o => o.Name == "E_ExampleState").ObjType);

    [Fact]
    public void Build_ProjectNameFromDirectory()
        => Assert.Equal("sample_project", DocModel.BuildProjectDoc(Fixtures.SampleProject).Name);

    [Fact]
    public void Build_EmptyProject_Raises()
        => Assert.Throws<NoSourceFilesException>(
            () => DocModel.BuildProjectDoc(Path.Combine(Path.GetTempPath(), "tckit_nonexistent_doc_project")));

    [Fact]
    public void Build_GvlVariablesExtracted()
    {
        var gvl = Objects(DocModel.BuildProjectDoc(Fixtures.SampleProject)).First(o => o.Name == "GVL_Params");
        var names = gvl.Variables.Select(v => v.Name).ToList();
        Assert.Contains("nMaxRetries", names);
        Assert.Contains("fTimeout", names);
        Assert.Contains("sProjectName", names);
    }

    [Fact]
    public void Build_GvlDefaultValue()
    {
        var gvl = Objects(DocModel.BuildProjectDoc(Fixtures.SampleProject)).First(o => o.Name == "GVL_Params");
        Assert.Equal("3", gvl.Variables.First(v => v.Name == "nMaxRetries").DefaultValue);
    }

    [Fact]
    public void Build_GvlInlineBlockComment()
    {
        var gvl = Objects(DocModel.BuildProjectDoc(Fixtures.SampleProject)).First(o => o.Name == "GVL_Params");
        Assert.Equal("seconds", gvl.Variables.First(v => v.Name == "fTimeout").Comment);
    }

    [Fact]
    public void Build_StructFieldsExtracted()
    {
        var st = Objects(DocModel.BuildProjectDoc(Fixtures.SampleProject)).First(o => o.Name == "ST_ExampleConfig");
        var names = st.Variables.Select(v => v.Name).ToList();
        Assert.Contains("nMaxRetries", names);
        Assert.Contains("fTimeout", names);
        Assert.Contains("sDescription", names);
        Assert.Contains("bEnabled", names);
    }

    [Fact]
    public void Build_EnumMembersExtracted()
    {
        var e = Objects(DocModel.BuildProjectDoc(Fixtures.SampleProject)).First(o => o.Name == "E_ExampleState");
        Assert.Equal(new[] { "Idle", "Running", "Error" }, e.Variables.Select(v => v.Name));
        Assert.Equal(new[] { "0", "1", "2" }, e.Variables.Select(v => v.VarType));
    }

    [Fact]
    public void Build_TcioInterfaceIsPickedUp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tckit_tcio_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "I_Sample.TcIO"),
                "<?xml version=\"1.0\"?><TcPlcObject><Itf Name=\"I_Sample\">"
                + "<Declaration><![CDATA[INTERFACE I_Sample\n]]></Declaration>"
                + "<Method Name=\"DoStuff\"><Declaration><![CDATA[METHOD DoStuff : BOOL\n]]></Declaration></Method>"
                + "</Itf></TcPlcObject>");
            var project = DocModel.BuildProjectDoc(dir);
            var i = Objects(project).First(o => o.Name == "I_Sample");
            Assert.Equal("interface", i.ObjType);
            Assert.Equal(new[] { "DoStuff" }, i.Methods.Select(m => m.Name));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
