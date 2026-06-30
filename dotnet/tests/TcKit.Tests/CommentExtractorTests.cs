using TcKit.Adapters.DocGen;

namespace TcKit.Tests;

/// <summary>Comment-style detection and parsing. Ports the Python <c>test_comment_extractor.py</c>.</summary>
public sealed class CommentExtractorTests
{
    // -- Preamble extraction --------------------------------------------------

    [Fact]
    public void ExtractPreamble_StopsAtFunctionBlock()
    {
        const string decl = "// :Description: Some FB\nFUNCTION_BLOCK FB_Test\nVAR_INPUT\n    x : BOOL;\nEND_VAR";
        Assert.DoesNotContain("FUNCTION_BLOCK", CommentExtractor.ExtractPreamble(decl), StringComparison.Ordinal);
        Assert.Contains(":Description:", CommentExtractor.ExtractPreamble(decl), StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractPreamble_StopsAtMethod()
    {
        const string decl = "// :Description: A method\nMETHOD Execute : BOOL\nVAR_INPUT\n    x : BOOL;\nEND_VAR";
        Assert.DoesNotContain("METHOD", CommentExtractor.ExtractPreamble(decl), StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractPreamble_KeywordInsideComment_NotBoundary()
    {
        const string decl = "// :Description: Wraps a FUNCTION_BLOCK pattern\nFUNCTION_BLOCK FB_Test";
        var preamble = CommentExtractor.ExtractPreamble(decl);
        Assert.Contains("Wraps a FUNCTION_BLOCK pattern", preamble, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(preamble, "FUNCTION_BLOCK"));
    }

    [Fact]
    public void ExtractPreamble_KeywordInsideXmlComment_NotBoundary()
    {
        const string decl = "(*~\n<docu><summary>Contains a METHOD call</summary></docu>\n~*)\nFUNCTION_BLOCK FB_Test";
        Assert.Contains("METHOD call", CommentExtractor.ExtractPreamble(decl), StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractPreamble_EmptyDeclaration_ReturnsEmpty()
        => Assert.Equal("", CommentExtractor.ExtractPreamble(""));

    [Fact]
    public void ExtractPreamble_StopsAtVarGlobal()
    {
        const string decl = "// :Description: Some globals\nVAR_GLOBAL\n    x : BOOL;\nEND_VAR";
        var preamble = CommentExtractor.ExtractPreamble(decl);
        Assert.Contains(":Description:", preamble, StringComparison.Ordinal);
        Assert.DoesNotContain("VAR_GLOBAL", preamble, StringComparison.Ordinal);
        Assert.DoesNotContain("x : BOOL", preamble, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractPreamble_GvlWithOnlyPragma_YieldsShortPreamble()
    {
        const string decl =
            "{attribute 'qualified_only'}\nVAR_GLOBAL CONSTANT\n    n : INT := 1;\n    (* explains m *)\n    m : INT := 2;\nEND_VAR";
        var preamble = CommentExtractor.ExtractPreamble(decl);
        Assert.Contains("{attribute", preamble, StringComparison.Ordinal);
        Assert.DoesNotContain("VAR_GLOBAL", preamble, StringComparison.Ordinal);
        Assert.DoesNotContain("explains m", preamble, StringComparison.Ordinal);
    }

    // -- Style detection ------------------------------------------------------

    [Fact]
    public void DetectStyle_XmlDocu()
        => Assert.Equal("xml_docu", CommentExtractor.DetectStyle("(*~\n<docu><summary>Test</summary></docu>\n~*)"));

    [Fact]
    public void DetectStyle_BlockRst()
        => Assert.Equal("block_rst", CommentExtractor.DetectStyle("(* :Description: Some FB *)"));

    [Fact]
    public void DetectStyle_LineRst()
        => Assert.Equal("line_rst", CommentExtractor.DetectStyle("// :Description: Some FB\n// :param x: Input"));

    [Fact]
    public void DetectStyle_PlainComment_IsLineRst()
        => Assert.Equal("line_rst", CommentExtractor.DetectStyle("// just a plain comment"));

    [Fact]
    public void DetectStyle_Empty_IsPlain()
        => Assert.Equal("plain", CommentExtractor.DetectStyle(""));

    [Fact]
    public void DetectStyle_AttributePragmaOnly_IsPlain()
        => Assert.Equal("plain", CommentExtractor.DetectStyle("{attribute 'hide'}"));

    [Fact]
    public void DetectStyle_InlineBlockComment_DoesNotTriggerBlockRst()
    {
        const string preamble = "// :Description: Some GVL\nVAR_GLOBAL\n    x : REAL := 1.0; (* seconds *)\nEND_VAR";
        Assert.Equal("line_rst", CommentExtractor.DetectStyle(preamble));
    }

    // -- RST line style -------------------------------------------------------

    [Fact]
    public void LineRst_BasicDescription()
        => Assert.Equal("Example function block.",
            CommentExtractor.Extract("// :Description: Example function block.\nFUNCTION_BLOCK FB_Test").Description);

    [Fact]
    public void LineRst_DescriptionWithFunctionWord_NotTruncated()
        => Assert.Equal("Example function block for TcKit parser validation.",
            CommentExtractor.Extract(
                "// :Description: Example function block for TcKit parser validation.\nFUNCTION_BLOCK FB_Test").Description);

    [Fact]
    public void LineRst_ParamsExtracted()
    {
        var result = CommentExtractor.Extract(
            "// :param bEnable: Rising edge starts the operation\n// :param nSetpoint: Target value\nFUNCTION_BLOCK FB_Test");
        Assert.Equal("Rising edge starts the operation", result.Params["bEnable"]);
        Assert.Equal("Target value", result.Params["nSetpoint"]);
    }

    [Fact]
    public void LineRst_ReturnsExtracted()
        => Assert.Equal("TRUE when operation completes",
            CommentExtractor.Extract("// :returns: TRUE when operation completes\nMETHOD Execute : BOOL").Returns);

    [Fact]
    public void LineRst_RemarksExtracted()
        => Assert.Equal("Only call once per cycle",
            CommentExtractor.Extract("// :remarks: Only call once per cycle\nMETHOD Execute : BOOL").Remarks);

    [Fact]
    public void LineRst_PlainLineComment_BecomesDescription()
        => Assert.Equal("This is a plain comment",
            CommentExtractor.Extract("// This is a plain comment\nFUNCTION_BLOCK FB_Test").Description);

    [Fact]
    public void LineRst_AttributePragma_NotInDescription()
    {
        var result = CommentExtractor.Extract("{attribute 'hide'}\n// :Description: A hidden FB\nFUNCTION_BLOCK FB_Test");
        Assert.DoesNotContain("{attribute", result.Description, StringComparison.Ordinal);
        Assert.Equal("A hidden FB", result.Description);
    }

    [Fact]
    public void LineRst_MultipleAttributePragmas()
        => Assert.Equal("Read-only value", CommentExtractor.Extract(
            "{attribute clr [ReadOnly()]}\n{attribute 'monitoring' := 'variable'}\n// :Description: Read-only value\nPROPERTY MyProp : BOOL").Description);

    [Fact]
    public void LineRst_NoComment_ReturnsEmpty()
    {
        var result = CommentExtractor.Extract("FUNCTION_BLOCK FB_Test\nVAR_INPUT\n    x : BOOL;\nEND_VAR");
        Assert.Equal("", result.Description);
        Assert.Empty(result.Params);
        Assert.Equal("", result.Returns);
    }

    // -- XML <docu> style -----------------------------------------------------

    [Fact]
    public void XmlDocu_SummaryExtracted()
        => Assert.Contains("basic task execution", CommentExtractor.Extract(
            "(*~\n<docu><summary>Provides basic task execution.</summary></docu>\n~*)\nFUNCTION_BLOCK TcoTask").Description,
            StringComparison.Ordinal);

    [Fact]
    public void XmlDocu_ParamWithNameAttribute()
        => Assert.Equal("Enables the task", CommentExtractor.Extract(
            "(*~\n<docu><param name=\"bEnable\">Enables the task</param></docu>\n~*)\nMETHOD Execute").Params["bEnable"]);

    [Fact]
    public void XmlDocu_ReturnsExtracted()
        => Assert.Equal("TRUE when done", CommentExtractor.Extract(
            "(*~\n<docu><returns>TRUE when done</returns></docu>\n~*)\nMETHOD Execute : BOOL").Returns);

    [Fact]
    public void XmlDocu_NestedParaTagsStripped()
    {
        var result = CommentExtractor.Extract(
            "(*~\n<docu><summary><para>Task execution via <see cref=\"ITcoTask\"/>.</para></summary></docu>\n~*)\nFUNCTION_BLOCK TcoTask");
        Assert.DoesNotContain("<para>", result.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("<see", result.Description, StringComparison.Ordinal);
        Assert.Contains("Task execution", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void XmlDocu_RemarksExtracted()
        => Assert.Equal("Only call cyclically.", CommentExtractor.Extract(
            "(*~\n<docu><summary>Brief.</summary><remarks>Only call cyclically.</remarks></docu>\n~*)\nFUNCTION_BLOCK FB_Test").Remarks);

    // -- Block RST style ------------------------------------------------------

    [Fact]
    public void BlockRst_BasicDescription()
        => Assert.Contains("block-commented", CommentExtractor.Extract(
            "(* :Description: A block-commented FB\n:param x: Input value\n*)\nFUNCTION_BLOCK FB_Test").Description,
            StringComparison.Ordinal);

    [Fact]
    public void BlockRst_ParamExtracted()
        => Assert.Equal("Trigger input",
            CommentExtractor.Extract("(* :param bEnable: Trigger input\n*)\nMETHOD Execute : BOOL").Params["bEnable"]);

    // -- Edge cases -----------------------------------------------------------

    [Fact]
    public void Edge_MethodWithPublicModifier()
        => Assert.Equal("Reset state", CommentExtractor.Extract("// :Description: Reset state\nMETHOD PUBLIC Reset").Description);

    [Fact]
    public void Edge_FunctionBlockPublicAbstract()
        => Assert.Equal("Abstract base",
            CommentExtractor.Extract("// :Description: Abstract base\nFUNCTION_BLOCK PUBLIC ABSTRACT TcoObject").Description);

    [Fact]
    public void Edge_DescriptionWithInterfaceKeyword_NotTruncated()
        => Assert.Equal("Implements the INTERFACE pattern",
            CommentExtractor.Extract("// :Description: Implements the INTERFACE pattern\nFUNCTION_BLOCK FB_Test").Description);

    [Fact]
    public void Edge_SummaryAlias()
        => Assert.Equal("Brief summary here",
            CommentExtractor.Extract("// :Summary: Brief summary here\nFUNCTION_BLOCK FB_Test").Description);

    [Fact]
    public void Edge_ReturnAlias()
        => Assert.Equal("The result value",
            CommentExtractor.Extract("// :return: The result value\nMETHOD Execute : BOOL").Returns);

    [Fact]
    public void Edge_GvlWithoutDocComment_YieldsEmptyDescription()
    {
        const string decl =
            "{attribute 'qualified_only'}\nVAR_GLOBAL CONSTANT\n    n : INT := 1;\n    (* explains m *)\n    m : INT := 2;\nEND_VAR";
        var result = CommentExtractor.Extract(decl);
        Assert.Equal("", result.Description);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }

        return count;
    }
}
