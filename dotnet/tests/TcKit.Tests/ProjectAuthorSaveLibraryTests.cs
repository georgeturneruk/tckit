using TcKit.Adapters.Automation;

namespace TcKit.Tests;

/// <summary>
/// save_plc_as_library against the fake PLC project node: the ProjectInfo metadata round-trip
/// (ProduceXml -> fill blank Title/Company/Version -> ConsumeXml) lands before SaveAsLibrary
/// records the path + install flag, existing metadata survives untouched (no rewrite when
/// nothing is blank), and the repository guard rejects anything but System on install.
/// </summary>
public sealed class ProjectAuthorSaveLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tckit-savelib-" + Guid.NewGuid().ToString("N"));

    public ProjectAuthorSaveLibraryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
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
    public void SavePlcAsLibrary_SetsMetadataAndRecordsSave()
    {
        var (session, pous, _) = FakeProject.Build("Plc");
        var projectNode = (FakeTreeItem)pous["Plc"].Parent!;
        var outputPath = Path.Combine(_dir, "Plc.library");

        var result = ProjectAuthor.SavePlcAsLibrary(session, null, outputPath, install: true, "System", overwrite: false);

        Assert.True(result.Success);
        Assert.Equal(outputPath, projectNode.SavedLibraryPath);
        Assert.True(projectNode.SavedLibraryInstall);
        Assert.Equal("Plc", projectNode.ProjectTitle); // Title defaults to the PLC name via ConsumeXml
        Assert.Equal(false, result.Details["cold_start_warmup"]);
    }

    [Fact]
    public void SavePlcAsLibrary_PreservesExistingMetadata_WithoutRewrite()
    {
        var (session, pous, _) = FakeProject.Build("Plc");
        var projectNode = (FakeTreeItem)pous["Plc"].Parent!;
        projectNode.SeedProjectInfo("My Library", "Acme Automation", "2.3.0.0");
        var outputPath = Path.Combine(_dir, "Plc.library");

        var result = ProjectAuthor.SavePlcAsLibrary(session, null, outputPath, install: false, "System", overwrite: false);

        Assert.True(result.Success);
        Assert.Equal("My Library", projectNode.ProjectTitle);
        Assert.Equal("Acme Automation", projectNode.ProjectCompany);
        Assert.Equal("2.3.0.0", projectNode.ProjectVersion);
        Assert.Equal(0, projectNode.ConsumeXmlCount); // fully-populated ProjectInfo skips the rewrite
        Assert.Equal("My Library", result.Details["title"]);
        Assert.Equal("Acme Automation", result.Details["company"]);
        Assert.Equal("2.3.0.0", result.Details["version"]);
    }

    [Fact]
    public void SavePlcAsLibrary_FillsOnlyBlankFields()
    {
        var (session, pous, _) = FakeProject.Build("Plc");
        var projectNode = (FakeTreeItem)pous["Plc"].Parent!;
        projectNode.SeedProjectInfo("", "Acme Automation", "");
        var outputPath = Path.Combine(_dir, "Plc.library");

        var result = ProjectAuthor.SavePlcAsLibrary(session, null, outputPath, install: false, "System", overwrite: false);

        Assert.True(result.Success);
        Assert.Equal("Plc", projectNode.ProjectTitle);
        Assert.Equal("Acme Automation", projectNode.ProjectCompany);
        Assert.Equal("1.0.0.0", projectNode.ProjectVersion);
        Assert.Equal(1, projectNode.ConsumeXmlCount);
    }

    [Fact]
    public void SavePlcAsLibrary_CreatesOutputDirectory()
    {
        var (session, pous, _) = FakeProject.Build("Plc");
        var projectNode = (FakeTreeItem)pous["Plc"].Parent!;
        var outputPath = Path.Combine(_dir, "nested", "deep", "Plc.library");

        var result = ProjectAuthor.SavePlcAsLibrary(session, null, outputPath, install: false, "System", overwrite: false);

        Assert.True(result.Success);
        Assert.True(Directory.Exists(Path.GetDirectoryName(outputPath)));
        Assert.False(projectNode.SavedLibraryInstall);
    }

    [Fact]
    public void SavePlcAsLibrary_NonSystemRepositoryOnInstall_Throws()
    {
        var (session, _, _) = FakeProject.Build("Plc");
        var outputPath = Path.Combine(_dir, "Plc.library");

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectAuthor.SavePlcAsLibrary(session, null, outputPath, install: true, "Custom", overwrite: false));
        Assert.Contains("only 'System'", ex.Message);
    }

    [Fact]
    public void SavePlcAsLibrary_Overwrite_RemovesExistingArtefact()
    {
        var (session, pous, _) = FakeProject.Build("Plc");
        var projectNode = (FakeTreeItem)pous["Plc"].Parent!;
        var outputPath = Path.Combine(_dir, "Plc.library");
        File.WriteAllText(outputPath, "stale");

        var result = ProjectAuthor.SavePlcAsLibrary(session, null, outputPath, install: false, "System", overwrite: true);

        Assert.True(result.Success);
        Assert.Equal(outputPath, projectNode.SavedLibraryPath);
    }
}
