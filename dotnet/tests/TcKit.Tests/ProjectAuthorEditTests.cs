using TcKit.Adapters.Automation;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>
/// Behaviour spec for the update_* and delete_* writer verbs, exercised against the in-memory
/// automation fake (no COM). Covers text replacement, anchored patches, kind validation, the
/// property-accessor cascade, folder emptiness, and the not-found / wrong-kind error paths.
/// </summary>
public class ProjectAuthorEditTests
{
    private static FakeTreeItem ChildNamed(FakeTreeItem parent, string name)
    {
        var child = parent.FindDirect(name);
        Assert.NotNull(child);
        return child!;
    }

    private static (FakeSession Session, FakeTreeItem Pous) WithFb(string code = "FUNCTION_BLOCK FB_X")
    {
        var (session, pous, _) = FakeProject.Build("Plc");
        ProjectAuthor.AddPou(session, "FB_X", PouType.FunctionBlock, code, "", null);
        return (session, pous["Plc"]);
    }

    [Fact]
    public void UpdatePouDeclaration_ReplacesDeclarationText()
    {
        var (session, pous) = WithFb();

        ProjectAuthor.UpdatePouDeclaration(session, "FB_X", "FUNCTION_BLOCK FB_X\nVAR\n  n : INT;\nEND_VAR", null);

        Assert.Contains("n : INT;", ChildNamed(pous, "FB_X").DeclarationText);
    }

    [Fact]
    public void UpdatePouImplementation_ReplacesImplementationText()
    {
        var (session, pous) = WithFb();

        ProjectAuthor.UpdatePouImplementation(session, "FB_X", "nCounter := nCounter + 1;", null);

        Assert.Equal("nCounter := nCounter + 1;", ChildNamed(pous, "FB_X").ImplementationText);
    }

    [Fact]
    public void UpdateMethodBody_ResplitsDeclarationAndImplementation()
    {
        var (session, pous) = WithFb();
        ProjectAuthor.AddMethod(session, "FB_X", "M", "METHOD M : BOOL\nM := FALSE;", null);

        ProjectAuthor.UpdateMethodBody(session, "FB_X", "M", "METHOD M : BOOL\nVAR\n  t : INT;\nEND_VAR\nM := TRUE;", null);

        var method = ChildNamed(ChildNamed(pous, "FB_X"), "M");
        Assert.EndsWith("END_VAR", method.DeclarationText);
        Assert.Equal("M := TRUE;", method.ImplementationText);
    }

    [Fact]
    public void UpdatePouImplementationPatch_ReplacesSingleOccurrence()
    {
        var (session, pous) = WithFb();
        ProjectAuthor.UpdatePouImplementation(session, "FB_X", "a := 1;\nb := 2;", null);

        ProjectAuthor.UpdatePouImplementationPatch(session, "FB_X", "b := 2;", "b := 3;", null);

        Assert.Equal("a := 1;\nb := 3;", ChildNamed(pous, "FB_X").ImplementationText);
    }

    [Fact]
    public void UpdatePatch_MissingAnchor_Throws()
    {
        var (session, _) = WithFb();
        ProjectAuthor.UpdatePouImplementation(session, "FB_X", "a := 1;", null);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectAuthor.UpdatePouImplementationPatch(session, "FB_X", "zzz", "y", null));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void UpdatePatch_AmbiguousAnchor_Throws()
    {
        var (session, _) = WithFb();
        ProjectAuthor.UpdatePouImplementation(session, "FB_X", "x();\nx();", null);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectAuthor.UpdatePouImplementationPatch(session, "FB_X", "x();", "y();", null));
        Assert.Contains("appears 2 times", ex.Message);
    }

    [Fact]
    public void DeletePou_RemovesFunctionBlock()
    {
        var (session, pous) = WithFb();

        var result = ProjectAuthor.DeletePou(session, "FB_X", null);

        Assert.True(result.Success);
        Assert.Null(pous.FindDirect("FB_X"));
        Assert.Equal(604, result.Details["kind"]);
    }

    [Fact]
    public void DeletePou_WrongKind_Throws()
    {
        var (session, pous, _) = FakeProject.Build("Plc");
        ProjectAuthor.AddGvl(session, "GVL_P", "VAR_GLOBAL\nEND_VAR", "", null);

        var ex = Assert.Throws<InvalidOperationException>(() => ProjectAuthor.DeletePou(session, "GVL_P", null));
        Assert.Contains("is not a POU", ex.Message);
        Assert.NotNull(pous["Plc"].FindDirect("GVL_P"));
    }

    [Fact]
    public void DeleteProperty_RemovesPropertyAndAccessors()
    {
        var (session, pous) = WithFb();
        ProjectAuthor.AddProperty(session, "FB_X", "ErrorId", "UDINT", "x", "x", null);

        var result = ProjectAuthor.DeleteProperty(session, "FB_X", "ErrorId", null);

        Assert.Null(ChildNamed(pous, "FB_X").FindDirect("ErrorId"));
        Assert.Equal(new[] { "Get", "Set" }, Assert.IsAssignableFrom<IReadOnlyList<string>>(result.Details["removed_accessors"]));
    }

    [Fact]
    public void DeleteGvl_And_DeleteDut_ValidateKind()
    {
        var (session, pous, duts) = FakeProject.Build("Plc");
        ProjectAuthor.AddGvl(session, "GVL_P", "VAR_GLOBAL\nEND_VAR", "", null);
        ProjectAuthor.AddDut(session, "ST_C", "TYPE ST_C :\nSTRUCT\nEND_STRUCT\nEND_TYPE", DutKind.Struct, "", null);

        ProjectAuthor.DeleteGvl(session, "GVL_P", null);
        ProjectAuthor.DeleteDut(session, "ST_C", null);

        Assert.Null(pous["Plc"].FindDirect("GVL_P"));
        Assert.Null(duts["Plc"].FindDirect("ST_C"));
        // A DUT is not a GVL: delete_gvl must refuse it.
        ProjectAuthor.AddDut(session, "ST_D", "TYPE ST_D :\nSTRUCT\nEND_STRUCT\nEND_TYPE", DutKind.Struct, "", null);
        Assert.Throws<InvalidOperationException>(() => ProjectAuthor.DeleteGvl(session, "ST_D", null));
    }

    [Fact]
    public void DeleteFolder_RefusesNonEmptyUnlessRecursive()
    {
        var (session, pous, _) = FakeProject.Build("Plc");
        ProjectAuthor.AddFolder(session, "Drives", "POUs", null);
        ProjectAuthor.AddPou(session, "FB_M", PouType.FunctionBlock, "", "POUs/Drives", null);

        Assert.Throws<InvalidOperationException>(() => ProjectAuthor.DeleteFolder(session, "Drives", "POUs", false, null));

        ProjectAuthor.DeleteFolder(session, "Drives", "POUs", true, null);
        Assert.Null(pous["Plc"].FindDirect("Drives"));
    }

    [Fact]
    public void DeleteMethod_UnknownItem_Throws()
    {
        var (session, _) = WithFb();

        Assert.Throws<InvalidOperationException>(() => ProjectAuthor.DeleteMethod(session, "FB_X", "Nope", null));
    }
}
