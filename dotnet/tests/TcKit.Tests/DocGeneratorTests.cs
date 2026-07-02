using TcKit.Adapters.DocGen;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>
/// Doc-generator multi-project sln output (ADR-0005) and per-format layout. Ports the Python
/// <c>test_doc_generator_multi_project.py</c>.
/// </summary>
public sealed class DocGeneratorTests : IDisposable
{
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), "tckit_docgen_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_outDir))
        {
            Directory.Delete(_outDir, recursive: true);
        }
    }

    // -- Doc model multi-project ----------------------------------------------

    [Fact]
    public void Build_EmitsOnePlcDocPerPlcproj()
    {
        var project = DocModel.BuildProjectDoc(Fixtures.MultiProject);
        Assert.Equal(new HashSet<string> { "Library", "Tests" }, project.Plcs.Keys.ToHashSet());
    }

    [Fact]
    public void Build_ObjectDocCarriesOwningPlcName()
    {
        var project = DocModel.BuildProjectDoc(Fixtures.MultiProject);
        Assert.All(project.Plcs["Library"].Objects, o => Assert.Equal("Library", o.PlcName));
        Assert.All(project.Plcs["Tests"].Objects, o => Assert.Equal("Tests", o.PlcName));
    }

    [Fact]
    public void Build_UsedByScopedWithinPlc()
    {
        var project = DocModel.BuildProjectDoc(Fixtures.MultiProject);
        var fbFilter = project.Plcs["Library"].Objects.First(o => o.Name == "FB_Filter");
        Assert.DoesNotContain("FB_FilterTests", fbFilter.UsedBy);
    }

    [Fact]
    public void Build_UsedByWithinSamePlcStillWorks()
    {
        var project = DocModel.BuildProjectDoc(Fixtures.MultiProject);
        var eState = project.Plcs["Library"].Objects.First(o => o.Name == "E_State");
        Assert.Contains("FB_Filter", eState.UsedBy);
    }

    // -- HTML output layout ---------------------------------------------------

    [Fact]
    public async Task GenerateHtml_TopLevelIndexExists()
    {
        await GenerateAsync(DocFormat.Html);
        Assert.True(File.Exists(Path.Combine(_outDir, "index.html")));
    }

    [Fact]
    public async Task GenerateHtml_TopLevelIndexLinksToEachPlc()
    {
        await GenerateAsync(DocFormat.Html);
        var text = await File.ReadAllTextAsync(Path.Combine(_outDir, "index.html"));
        Assert.Contains("Library/index.html", text, StringComparison.Ordinal);
        Assert.Contains("Tests/index.html", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateHtml_PerPlcSubtreeExists()
    {
        await GenerateAsync(DocFormat.Html);
        Assert.True(File.Exists(Path.Combine(_outDir, "Library", "index.html")));
        Assert.True(File.Exists(Path.Combine(_outDir, "Library", "FB_Filter.html")));
        Assert.True(File.Exists(Path.Combine(_outDir, "Tests", "index.html")));
        Assert.True(File.Exists(Path.Combine(_outDir, "Tests", "FB_FilterTests.html")));
    }

    [Fact]
    public async Task GenerateHtml_DuplicatedSymbolLandsInEachPlc()
    {
        await GenerateAsync(DocFormat.Html);
        Assert.True(File.Exists(Path.Combine(_outDir, "Library", "E_State.html")));
        Assert.True(File.Exists(Path.Combine(_outDir, "Tests", "E_State.html")));
    }

    [Fact]
    public async Task GenerateHtml_PerPlcSearchIndexExists()
    {
        await GenerateAsync(DocFormat.Html);
        Assert.True(File.Exists(Path.Combine(_outDir, "Library", "search-index.json")));
        Assert.True(File.Exists(Path.Combine(_outDir, "Tests", "search-index.json")));
    }

    [Fact]
    public async Task GenerateHtml_HierarchyPageExists()
    {
        await GenerateAsync(DocFormat.Html);
        Assert.True(File.Exists(Path.Combine(_outDir, "Library", "hierarchy.html")));
    }

    // -- Markdown output layout -----------------------------------------------

    [Fact]
    public async Task GenerateMarkdown_TopLevelIndexExists()
    {
        await GenerateAsync(DocFormat.Markdown);
        Assert.True(File.Exists(Path.Combine(_outDir, "index.md")));
    }

    [Fact]
    public async Task GenerateMarkdown_TopLevelIndexLinksToEachPlc()
    {
        await GenerateAsync(DocFormat.Markdown);
        var text = await File.ReadAllTextAsync(Path.Combine(_outDir, "index.md"));
        Assert.Contains("Library/index.md", text, StringComparison.Ordinal);
        Assert.Contains("Tests/index.md", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateMarkdown_PerPlcSubtreeExists()
    {
        await GenerateAsync(DocFormat.Markdown);
        Assert.True(File.Exists(Path.Combine(_outDir, "Library", "FB_Filter.md")));
        Assert.True(File.Exists(Path.Combine(_outDir, "Tests", "FB_FilterTests.md")));
    }

    // -- Status + result contract ---------------------------------------------

    [Fact]
    public async Task Generate_ReportsCompleteAndObjectCount()
    {
        var generator = new DocGenerator();
        Assert.Equal(DocStatus.Idle, generator.Status);
        var result = await generator.GenerateAsync(Fixtures.MultiProject, _outDir, DocFormat.Html, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.Equal(DocStatus.Complete, generator.Status);
        Assert.Equal(2, result.Details["plcs"]);
        Assert.Equal(Path.Combine(_outDir, "index.html"), result.Details["index"]);
    }

    [Fact]
    public async Task Generate_EmptyProject_FailsAndReportsError()
    {
        var generator = new DocGenerator();
        var empty = Path.Combine(Path.GetTempPath(), "tckit_empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var result = await generator.GenerateAsync(empty, _outDir, DocFormat.Html, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Equal(DocStatus.Error, generator.Status);
            Assert.Contains("No TwinCAT source files", result.Error!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    private async Task GenerateAsync(DocFormat format)
    {
        var result = await new DocGenerator().GenerateAsync(Fixtures.MultiProject, _outDir, format, CancellationToken.None);
        Assert.True(result.Success, result.Error);
    }
}
