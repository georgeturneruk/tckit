using TcKit.Adapters.Automation;

namespace TcKit.Tests;

/// <summary>Tests the pure VAR-block declaration editor (port of the bridge's add/remove-variable helpers).</summary>
public class VarBlockTests
{
    [Fact]
    public void AddVariable_IntoExistingBlock_InsertsBeforeEndVar()
    {
        var decl = "FUNCTION_BLOCK FB_X\nVAR_INPUT\n    a : INT;\nEND_VAR";

        var result = VarBlock.AddVariable(decl, "VAR_INPUT", "b : BOOL;");

        Assert.Equal("FUNCTION_BLOCK FB_X\nVAR_INPUT\n    a : INT;\n    b : BOOL;\nEND_VAR", result);
    }

    [Fact]
    public void AddVariable_CreatesMissingBlock_AtConventionalRank()
    {
        // VAR_OUTPUT (rank 2) should be inserted before VAR (rank 4).
        var decl = "FUNCTION_BLOCK FB_X\nVAR_INPUT\n    a : INT;\nEND_VAR\nVAR\n    t : INT;\nEND_VAR";

        var result = VarBlock.AddVariable(decl, "VAR_OUTPUT", "q : BOOL;");

        var idxOutput = result.IndexOf("VAR_OUTPUT", StringComparison.Ordinal);
        var idxVarBlock = result.IndexOf("\nVAR\n", StringComparison.Ordinal);
        Assert.True(idxOutput >= 0 && idxOutput < idxVarBlock);
        Assert.Contains("VAR_OUTPUT\n    q : BOOL;\nEND_VAR", result);
    }

    [Fact]
    public void AddVariable_NoBlocks_AppendsAtEnd()
    {
        var result = VarBlock.AddVariable("FUNCTION_BLOCK FB_X", "VAR", "n : INT;");

        Assert.Equal("FUNCTION_BLOCK FB_X\nVAR\n    n : INT;\nEND_VAR", result);
    }

    [Fact]
    public void AddVariable_UnknownScope_Throws()
        => Assert.Throws<ArgumentException>(() => VarBlock.AddVariable("FUNCTION_BLOCK FB_X", "VAR_BOGUS", "n : INT;"));

    [Fact]
    public void RemoveVariable_RemovesTheSingleLine()
    {
        var decl = "FUNCTION_BLOCK FB_X\nVAR\n    a : INT;\n    b : BOOL;\nEND_VAR";

        var result = VarBlock.RemoveVariable(decl, "a");

        Assert.Equal("FUNCTION_BLOCK FB_X\nVAR\n    b : BOOL;\nEND_VAR", result);
    }

    [Fact]
    public void RemoveVariable_NotFound_Throws()
        => Assert.Throws<InvalidOperationException>(
            () => VarBlock.RemoveVariable("VAR\n    a : INT;\nEND_VAR", "zzz"));

    [Fact]
    public void RemoveVariable_MultiNameList_Throws()
    {
        var decl = "VAR\n    a, b : INT;\nEND_VAR";

        var ex = Assert.Throws<InvalidOperationException>(() => VarBlock.RemoveVariable(decl, "a"));
        Assert.Contains("multi-name", ex.Message);
    }

    [Fact]
    public void RemoveVariable_LineWithoutSemicolon_Throws()
    {
        var decl = "VAR\n    a : ARRAY[0..1] OF INT :=\n        [1, 2];\nEND_VAR";

        Assert.Throws<InvalidOperationException>(() => VarBlock.RemoveVariable(decl, "a"));
    }
}
