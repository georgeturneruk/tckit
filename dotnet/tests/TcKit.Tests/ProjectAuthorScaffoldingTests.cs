using TcKit.Adapters.Automation;

namespace TcKit.Tests;

/// <summary>
/// create_project / add_plc_project against the fake session: both add a TwinCAT project from the
/// template, then drop the PLC under the freshly-added (empty) TIPC. Covers the template-path
/// resolution, the PlcName collision guard, and the library-type guard.
/// </summary>
public sealed class ProjectAuthorScaffoldingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tckit-scaffold-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousTemplate;

    public ProjectAuthorScaffoldingTests()
    {
        Directory.CreateDirectory(_dir);
        // Point template resolution at a real (empty) file so File.Exists passes without a TwinCAT install.
        var template = Path.Combine(_dir, "TwinCAT Project.tsproj");
        File.WriteAllText(template, "<TcSmProject/>");
        _previousTemplate = Environment.GetEnvironmentVariable("TC_PROJECT_TEMPLATE");
        Environment.SetEnvironmentVariable("TC_PROJECT_TEMPLATE", template);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TC_PROJECT_TEMPLATE", _previousTemplate);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone.
        }
    }

    [Fact]
    public void CreateProject_BuildsSolutionAndPlc()
    {
        var (session, _, _) = FakeProject.Build("Seed");

        var result = ProjectAuthor.CreateProject(session, "Proj", _dir);

        Assert.True(result.Success);
        Assert.Equal("Proj_Plc", result.Details["plc"]);
        Assert.Equal("Proj", session.CreatedSolutionName);
        Assert.Equal(Path.Combine(_dir, "Proj.sln"), session.SavedAsPath);

        var tipc = session.GetSysManagers().Single().LookupTreeItem("TIPC");
        Assert.Equal("Proj_Plc", tipc.Child(1).Name);
    }

    [Fact]
    public void AddPlcProject_AddsSecondPlcUnderNewProject()
    {
        var (session, _, _) = FakeProject.Build("Plc");
        var slnPath = Path.Combine(_dir, "Existing.sln");

        var result = ProjectAuthor.AddPlcProject(session, slnPath, "Plc2", "standard");

        Assert.True(result.Success);
        Assert.Equal("Plc2", result.Details["plc"]);
        Assert.Equal(slnPath, session.SavedAsPath);
        Assert.Contains("Plc2_Tc", session.AddedTemplates);
    }

    [Fact]
    public void AddPlcProject_NameCollision_Throws()
    {
        var (session, _, _) = FakeProject.Build("Plc");
        var slnPath = Path.Combine(_dir, "Existing.sln");

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectAuthor.AddPlcProject(session, slnPath, "Plc", "standard"));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public void AddPlcProject_LibraryType_Throws()
    {
        var (session, _, _) = FakeProject.Build("Plc");
        var slnPath = Path.Combine(_dir, "Existing.sln");

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectAuthor.AddPlcProject(session, slnPath, "Plc2", "library"));
        Assert.Contains("not supported", ex.Message);
    }
}
