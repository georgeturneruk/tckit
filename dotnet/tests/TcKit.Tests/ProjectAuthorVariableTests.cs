using TcKit.Adapters.Automation;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>Tests add_variable / delete_variable against the fake: they edit the located item's declaration.</summary>
public class ProjectAuthorVariableTests
{
    private static (FakeSession Session, FakeTreeItem Fb) WithFb(string declaration)
    {
        var (session, pous, _) = FakeProject.Build("Plc");
        ProjectAuthor.AddPou(session, "FB_X", PouType.FunctionBlock, declaration, "", null);
        return (session, pous["Plc"].FindDirect("FB_X")!);
    }

    [Fact]
    public void AddVariable_InsertsIntoFbDeclaration()
    {
        var (session, fb) = WithFb("FUNCTION_BLOCK FB_X\nVAR_INPUT\n    a : INT;\nEND_VAR");

        ProjectAuthor.AddVariable(session, "FB_X", "VAR_INPUT", "b : BOOL;", null, null);

        Assert.Contains("b : BOOL;", fb.DeclarationText);
        Assert.Equal(2, session.SaveCount); // AddPou + AddVariable
    }

    [Fact]
    public void DeleteVariable_RemovesFromFbDeclaration()
    {
        var (session, fb) = WithFb("FUNCTION_BLOCK FB_X\nVAR\n    a : INT;\n    b : BOOL;\nEND_VAR");

        ProjectAuthor.DeleteVariable(session, "FB_X", "a", null, null);

        Assert.DoesNotContain("a : INT;", fb.DeclarationText);
        Assert.Contains("b : BOOL;", fb.DeclarationText);
    }

    [Fact]
    public void AddVariable_TargetsMethodLocals_WhenItemNameGiven()
    {
        var (session, fb) = WithFb("FUNCTION_BLOCK FB_X");
        ProjectAuthor.AddMethod(session, "FB_X", "M", "METHOD M : BOOL\nVAR\n    t : INT;\nEND_VAR\nM := TRUE;", null);

        ProjectAuthor.AddVariable(session, "FB_X", "VAR", "u : INT;", "M", null);

        var method = fb.FindDirect("M")!;
        Assert.Contains("u : INT;", method.DeclarationText);
        Assert.DoesNotContain("u : INT;", fb.DeclarationText);
    }

    [Fact]
    public void DeleteVariable_MultiName_Throws()
    {
        var (session, _) = WithFb("FUNCTION_BLOCK FB_X\nVAR\n    a, b : INT;\nEND_VAR");

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectAuthor.DeleteVariable(session, "FB_X", "a", null, null));
        Assert.Contains("multi-name", ex.Message);
    }
}
