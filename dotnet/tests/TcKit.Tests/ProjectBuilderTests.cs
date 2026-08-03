using TcKit.Adapters.Automation;

namespace TcKit.Tests;

/// <summary>
/// Build + deploy logic against the fake session: CheckAllObjects drives the success flag, the Error
/// List read maps to structured diagnostics by severity, and deploy resolves a config, sets the
/// target, enables autostart, and activates.
/// </summary>
public class ProjectBuilderTests
{
    private static (FakeSession Session, FakeTreeItem Project, FakeTreeItem SysNode) WithPlc()
    {
        var (session, pous, _) = FakeProject.Build("Plc");
        var project = (FakeTreeItem)pous["Plc"].Parent!;     // TIPC^Plc^Plc Project
        var sysNode = (FakeTreeItem)project.Parent!;          // TIPC^Plc
        return (session, project, sysNode);
    }

    [Fact]
    public void Build_Succeeds_WhenCheckAllObjectsOk()
    {
        var (session, project, _) = WithPlc();
        project.CheckAllObjectsResult = true;

        var result = ProjectBuilder.Build(session, null, forceLog: false);

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Equal(true, result.Details["check_all_objects"]);
    }

    [Fact]
    public void Build_ReadsErrorList_OnFailure_AndMapsBySeverity()
    {
        var (session, project, _) = WithPlc();
        project.CheckAllObjectsResult = false;
        session.ErrorListItems =
        [
            new ComErrorItem("FB_X.TcPOU", 12, "C0046: cannot convert INT to BOOL", 1, "Plc"),
            new ComErrorItem("FB_Y.TcPOU", 3, "C0100: unused variable", 2, "Plc"),
            new ComErrorItem("FB_Z.TcPOU", 1, "informational note", 3, "Plc"),
        ];

        var result = ProjectBuilder.Build(session, null, forceLog: false);

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Equal("C0046", error.Code);
        Assert.Equal("cannot convert INT to BOOL", error.Message);
        Assert.Equal(12, error.Line);
        Assert.Single(result.Warnings);
        Assert.Single(result.Infos);
    }

    [Fact]
    public void Build_FallsBackToUiaRead_WhenEnvDteErrorListNotExposed()
    {
        var (session, project, _) = WithPlc();
        project.CheckAllObjectsResult = false;
        session.ErrorListItems = null; // TcXaeShell Express: EnvDTE tool window is null
        session.UiaErrorListItems =
        [
            new ComErrorItem("FB_X.TcPOU", 12, "C0046: identifier not defined", 1, "Plc"),
        ];

        var result = ProjectBuilder.Build(session, null, forceLog: false);

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Equal("C0046", error.Code);
        Assert.Equal("identifier not defined", error.Message);
        Assert.Equal(12, error.Line);
        Assert.Equal(false, session.UiaCompileSucceeded); // severity inference gets the real flag
    }

    [Fact]
    public void Build_HonestMessage_WhenBothErrorListSourcesUnreachable()
    {
        var (session, project, _) = WithPlc();
        project.CheckAllObjectsResult = false;
        session.ErrorListItems = null;
        session.UiaErrorListItems = null;

        var result = ProjectBuilder.Build(session, null, forceLog: false);

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Contains("couldn't be read", error.Message);
    }

    [Fact]
    public void Build_ForceLog_SurfacesWarnings_EvenWhenSuccessful()
    {
        var (session, project, _) = WithPlc();
        project.CheckAllObjectsResult = true;
        session.ErrorListItems = [new ComErrorItem("FB_Y.TcPOU", 3, "C0100: unused", 2, "Plc")];

        var result = ProjectBuilder.Build(session, null, forceLog: true);

        Assert.True(result.Success);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void Build_NoErrorList_AndFailure_ReturnsHonestMessage()
    {
        var (session, project, _) = WithPlc();
        project.CheckAllObjectsResult = false;
        session.ErrorListItems = null; // tool window not exposed

        var result = ProjectBuilder.Build(session, null, forceLog: false);

        Assert.False(result.Success);
        Assert.Single(result.Errors);
        Assert.Contains("couldn't be read", result.Errors[0].Message);
    }

    [Fact]
    public void Deploy_SetsTarget_EnablesAutostart_Activates()
    {
        var (session, _, sysNode) = WithPlc();
        var sm = (FakeSysManager)session.GetSysManagers()[0];

        var result = ProjectBuilder.Deploy(session, "192.168.1.100.1.1", null, bootAutostart: true);

        Assert.True(result.Success);
        Assert.Equal("192.168.1.100.1.1", sm.TargetNetId);
        Assert.True(sysNode.BootProjectAutostart);
        Assert.True(sysNode.BootProjectGenerated);
        Assert.True(sm.Activated);
        Assert.Equal(true, result.Details["autostart"]);
    }

    [Fact]
    public void Deploy_WithoutAutostart_StillActivates_ButSkipsBootProject()
    {
        var (session, _, sysNode) = WithPlc();
        var sm = (FakeSysManager)session.GetSysManagers()[0];

        var result = ProjectBuilder.Deploy(session, "1.2.3.4.1.1", null, bootAutostart: false);

        Assert.True(result.Success);
        Assert.False(sysNode.BootProjectGenerated);
        Assert.True(sm.Activated);
    }

    [Fact]
    public void Deploy_EmptyTarget_Throws()
    {
        var (session, _, _) = WithPlc();
        Assert.Throws<ArgumentException>(() => ProjectBuilder.Deploy(session, "", null, bootAutostart: true));
    }
}
