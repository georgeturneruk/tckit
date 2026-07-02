using TcKit.Adapters.Automation;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>
/// Tests the authoring logic against the in-memory automation fake: correct tree placement, kind
/// integers, declaration/implementation writes, property accessor kinds and vInfo, PLC resolution,
/// and the declaration-only guard. This is the CI-runnable behaviour spec for the writer lane.
/// </summary>
public class ProjectAuthorTests
{
    private static FakeTreeItem ChildNamed(FakeTreeItem parent, string name)
    {
        var child = parent.FindDirect(name);
        Assert.NotNull(child);
        return child!;
    }

    [Fact]
    public void AddPou_CreatesFunctionBlockUnderPous_WithSplitSource()
    {
        var (session, pous, _) = FakeProject.Build("Plc");

        var result = ProjectAuthor.AddPou(
            session, "FB_X", PouType.FunctionBlock, "FUNCTION_BLOCK FB_X\nVAR\n  n : INT;\nEND_VAR\nn := n + 1;", "", null);

        Assert.True(result.Success);
        var fb = ChildNamed(pous["Plc"], "FB_X");
        Assert.Equal(604, fb.Kind);
        Assert.EndsWith("END_VAR", fb.DeclarationText);
        Assert.Equal("n := n + 1;", fb.ImplementationText);
        Assert.Equal(1, session.SaveCount);
        Assert.Equal("TIPC^Plc^Plc Project^POUs^FB_X", result.Details["path"]);
    }

    [Fact]
    public void AddPou_IntoExistingSubfolder_ResolvesPath()
    {
        var (session, pous, _) = FakeProject.Build("Plc");
        pous["Plc"].Add(new FakeTreeItem("Drives", TcKind.Folder));

        ProjectAuthor.AddPou(session, "FB_M", PouType.FunctionBlock, "", "POUs/Drives", null);

        var drives = ChildNamed(pous["Plc"], "Drives");
        Assert.NotNull(drives.FindDirect("FB_M"));
    }

    [Fact]
    public void AddGvl_IsDeclarationOnly_AndKind615()
    {
        var (session, pous, _) = FakeProject.Build("Plc");

        ProjectAuthor.AddGvl(session, "GVL_P", "VAR_GLOBAL\n  g : INT;\nEND_VAR", "", null);

        var gvl = ChildNamed(pous["Plc"], "GVL_P");
        Assert.Equal(615, gvl.Kind);
        Assert.Contains("VAR_GLOBAL", gvl.DeclarationText);
        Assert.Equal("", gvl.ImplementationText);
    }

    [Fact]
    public void AddDut_Struct_IsKind606()
    {
        var (session, _, duts) = FakeProject.Build("Plc");

        ProjectAuthor.AddDut(session, "ST_C", "TYPE ST_C :\nSTRUCT\n  a : INT;\nEND_STRUCT\nEND_TYPE", DutKind.Struct, "", null);

        Assert.Equal(606, ChildNamed(duts["Plc"], "ST_C").Kind);
    }

    [Fact]
    public void AddFolder_DefaultsUnderPous_AsKind601()
    {
        var (session, pous, _) = FakeProject.Build("Plc");

        ProjectAuthor.AddFolder(session, "Drives", "POUs", null);

        Assert.Equal(601, ChildNamed(pous["Plc"], "Drives").Kind);
    }

    [Fact]
    public void AddMethod_OnFunctionBlock_IsKind609()
    {
        var (session, pous, _) = FakeProject.Build("Plc");
        ProjectAuthor.AddPou(session, "FB_X", PouType.FunctionBlock, "FUNCTION_BLOCK FB_X", "", null);

        ProjectAuthor.AddMethod(session, "FB_X", "Execute", "METHOD Execute : BOOL\nExecute := TRUE;", null);

        var method = ChildNamed(ChildNamed(pous["Plc"], "FB_X"), "Execute");
        Assert.Equal(609, method.Kind);
        Assert.Equal("Execute := TRUE;", method.ImplementationText);
    }

    [Fact]
    public void AddMethod_OnInterface_IsKind610()
    {
        var (session, pous, _) = FakeProject.Build("Plc");
        ProjectAuthor.AddPou(session, "I_X", PouType.Interface, "INTERFACE I_X", "", null);

        ProjectAuthor.AddMethod(session, "I_X", "DoThing", "METHOD DoThing : BOOL", null);

        Assert.Equal(610, ChildNamed(ChildNamed(pous["Plc"], "I_X"), "DoThing").Kind);
    }

    [Fact]
    public void AddProperty_OnFunctionBlock_CreatesParentAndAccessorsWithKindsAndVInfo()
    {
        var (session, pous, _) = FakeProject.Build("Plc");
        ProjectAuthor.AddPou(session, "FB_X", PouType.FunctionBlock, "FUNCTION_BLOCK FB_X", "", null);

        ProjectAuthor.AddProperty(session, "FB_X", "ErrorId", "UDINT", "ErrorId := nErr;", "nErr := ErrorId;", null);

        var property = ChildNamed(ChildNamed(pous["Plc"], "FB_X"), "ErrorId");
        Assert.Equal(611, property.Kind);
        Assert.Equal(new[] { "ST", "UDINT", "PUBLIC" }, Assert.IsType<string[]>(property.VInfo));
        Assert.Equal(613, property.Children.Single(c => c.Kind == 613).Kind);
        Assert.Equal(614, property.Children.Single(c => c.Kind == 614).Kind);
        Assert.Equal("ErrorId := nErr;", property.Children.Single(c => c.Kind == 613).ImplementationText);
    }

    [Fact]
    public void AddProperty_OnInterface_UsesInterfaceKinds_AndNoBodies()
    {
        var (session, pous, _) = FakeProject.Build("Plc");
        ProjectAuthor.AddPou(session, "I_X", PouType.Interface, "INTERFACE I_X", "", null);

        ProjectAuthor.AddProperty(session, "I_X", "Value", "LREAL", "x", "x", null);

        var property = ChildNamed(ChildNamed(pous["Plc"], "I_X"), "Value");
        Assert.Equal(612, property.Kind);
        Assert.Equal("LREAL", property.VInfo);
        Assert.Equal(654, property.Children.Single(c => c.Kind == 654).Kind);
        Assert.Equal(655, property.Children.Single(c => c.Kind == 655).Kind);
        Assert.Equal("", property.Children.Single(c => c.Kind == 654).ImplementationText);
    }

    [Fact]
    public void AddProperty_NoAccessor_Throws()
    {
        var (session, _, _) = FakeProject.Build("Plc");
        ProjectAuthor.AddPou(session, "FB_X", PouType.FunctionBlock, "FUNCTION_BLOCK FB_X", "", null);

        Assert.Throws<ArgumentException>(
            () => ProjectAuthor.AddProperty(session, "FB_X", "P", "BOOL", null, null, null));
    }

    [Fact]
    public void AddPou_AmbiguousPlc_ThrowsUnlessNamed()
    {
        var (session, pous, _) = FakeProject.Build("Library", "Tests");

        Assert.Throws<InvalidOperationException>(
            () => ProjectAuthor.AddPou(session, "FB_X", PouType.FunctionBlock, "", "", null));

        ProjectAuthor.AddPou(session, "FB_X", PouType.FunctionBlock, "", "", "Tests");
        Assert.NotNull(pous["Tests"].FindDirect("FB_X"));
    }

    [Fact]
    public void AddMethod_UnknownPou_Throws()
    {
        var (session, _, _) = FakeProject.Build("Plc");

        Assert.Throws<InvalidOperationException>(
            () => ProjectAuthor.AddMethod(session, "FB_Nope", "M", "METHOD M : BOOL", null));
    }

    [Fact]
    public void Fake_RejectsImplementationOnDeclarationOnlyKind()
    {
        var gvl = new FakeTreeItem("GVL_P", TcKind.Gvl);

        Assert.Throws<InvalidOperationException>(() => gvl.ImplementationText = "body();");
    }
}
