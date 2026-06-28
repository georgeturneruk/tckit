using TcKit.Adapters.Automation;

namespace TcKit.Tests;

/// <summary>
/// save_plc_as_library against the fake PLC project node: the ProjectInfo metadata round-trip
/// (ProduceXml -> set Title/Company/Version -> ConsumeXml) lands before SaveAsLibrary records the
/// path + install flag, and the repository guard rejects anything but System on install.
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
