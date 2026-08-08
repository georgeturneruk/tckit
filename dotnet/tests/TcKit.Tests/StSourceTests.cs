using TcKit.Core.Analysis;

namespace TcKit.Tests;

/// <summary>Tests the position-preserving mask that every analysis rule parses against.</summary>
public class StSourceTests
{
    [Fact]
    public void Mask_PreservesLengthAndNewlines()
    {
        var source = "VAR\n    a : INT; // comment\nEND_VAR";

        var masked = StSource.Mask(source);

        Assert.Equal(source.Length, masked.Length);
        Assert.Equal(
            source.Count(c => c == '\n'),
            masked.Count(c => c == '\n'));
    }

    [Fact]
    public void Mask_LineComment_BlanksToEndOfLineOnly()
    {
        var masked = StSource.Mask("a : INT; // hide me\nb : BOOL;");

        Assert.DoesNotContain("hide", masked, StringComparison.Ordinal);
        Assert.Contains("b : BOOL;", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_NestedBlockComment_ConsumesInnerCloseMarker()
    {
        // IEC 61131-3 block comments nest, so the first "*)" must not end the outer comment.
        var masked = StSource.Mask("(* outer (* inner *) still comment *) code : INT;");

        Assert.DoesNotContain("still", masked, StringComparison.Ordinal);
        Assert.Contains("code : INT;", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_CStyleBlockComment_IsBlanked()
    {
        var masked = StSource.Mask("/* gone */ kept : INT;");

        Assert.DoesNotContain("gone", masked, StringComparison.Ordinal);
        Assert.Contains("kept : INT;", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_StringLiteral_DollarEscapeDoesNotTerminate()
    {
        // ST escapes with '$', so "$'" is an embedded quote rather than the end of the literal.
        var masked = StSource.Mask("s : STRING := 'it$'s here'; after : INT;");

        Assert.DoesNotContain("here", masked, StringComparison.Ordinal);
        Assert.Contains("after : INT;", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_CommentInsideString_IsPartOfTheString()
    {
        var masked = StSource.Mask("s : STRING := 'a (* not a comment *) b'; after : INT;");

        Assert.Contains("after : INT;", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_StringInsideComment_DoesNotOpenALiteral()
    {
        var masked = StSource.Mask("(* it's fine *) after : INT;");

        Assert.Contains("after : INT;", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_Pragma_IsBlanked()
    {
        var masked = StSource.Mask("{attribute 'hide'}\nn : INT;");

        Assert.DoesNotContain("attribute", masked, StringComparison.Ordinal);
        Assert.Contains("n : INT;", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_UnterminatedComment_BlanksToEndWithoutThrowing()
    {
        var masked = StSource.Mask("a : INT; (* never closed");

        Assert.Contains("a : INT;", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("never", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void LineAt_CountsFromOne()
    {
        var source = "one\ntwo\nthree";

        Assert.Equal(1, StSource.LineAt(source, 0));
        Assert.Equal(2, StSource.LineAt(source, 4));
        Assert.Equal(3, StSource.LineAt(source, 8));
    }
}
