using TcKit.Core.Authoring;

namespace TcKit.Tests;

/// <summary>
/// Tests for the ST declaration/implementation splitter (port of the bridge's Split-TcCode). This is
/// the highest translation-bug-risk piece in the writer lane, and is fully testable without COM.
/// </summary>
public class StCodeTests
{
    [Fact]
    public void Split_WithVarBlock_SplitsAfterLastEndVar()
    {
        var (declaration, implementation) = StCode.Split("FUNCTION_BLOCK FB_X\nVAR\n  n : INT;\nEND_VAR\nn := n + 1;");

        Assert.EndsWith("END_VAR", declaration);
        Assert.Equal("n := n + 1;", implementation);
    }

    [Fact]
    public void Split_WithMultipleVarBlocks_SplitsAfterTheLastEndVar()
    {
        var code = "METHOD M : BOOL\nVAR_INPUT\n  a : INT;\nEND_VAR\nVAR\n  t : INT;\nEND_VAR\nM := TRUE;";

        var (declaration, implementation) = StCode.Split(code);

        Assert.EndsWith("END_VAR", declaration);
        Assert.Contains("VAR_INPUT", declaration);
        Assert.Equal("M := TRUE;", implementation);
    }

    [Fact]
    public void Split_NoVarButHasHeader_SplitsAfterHeaderLine()
    {
        var (declaration, implementation) = StCode.Split("METHOD DoThing : BOOL\nDoThing := TRUE;");

        Assert.Equal("METHOD DoThing : BOOL", declaration);
        Assert.Equal("DoThing := TRUE;", implementation);
    }

    [Fact]
    public void Split_NoVarNoHeader_TreatsWholeInputAsImplementation()
    {
        var (declaration, implementation) = StCode.Split("nCounter := nCounter + 1;");

        Assert.Equal("", declaration);
        Assert.Equal("nCounter := nCounter + 1;", implementation);
    }

    [Fact]
    public void Split_NormalisesCrlfAndTrimsImplementationLead()
    {
        var (declaration, implementation) = StCode.Split("FUNCTION_BLOCK FB_X\r\nVAR\r\nEND_VAR\r\n\r\n  body();");

        Assert.DoesNotContain('\r', declaration);
        Assert.Equal("body();", implementation);
    }
}
