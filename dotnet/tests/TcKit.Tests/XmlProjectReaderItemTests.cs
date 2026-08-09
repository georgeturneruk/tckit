using TcKit.Adapters.Xml;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>
/// Behavioural tests for the per-symbol readers (get_pou_interface / _declaration / _item / get_gvl
/// / get_dut). These resolve against the index built by a prior get_structure call, so each test
/// uses a freshly-primed reader.
/// </summary>
public class XmlProjectReaderItemTests
{
    private static async Task<XmlProjectReader> Primed(string projectPath)
    {
        var reader = new XmlProjectReader();
        await reader.GetStructureAsync(projectPath, null, CancellationToken.None);
        return reader;
    }

    [Fact]
    public async Task GetPouInterface_ListsMethodsAndProperties()
    {
        var reader = await Primed(Fixtures.SampleProject);

        var iface = await reader.GetPouInterfaceAsync("FB_Example", null, CancellationToken.None);

        Assert.Equal(PouType.FunctionBlock, iface.PouType);
        Assert.Equal(
            ["Execute", "CalculateInternal", "Reset"],
            iface.Methods.Select(m => m.Name).ToArray());
        var errorId = Assert.Single(iface.Properties, p => p.Name == "ErrorId");
        Assert.Equal("UDINT", errorId.ReturnType);
        Assert.True(errorId.HasGet);
        Assert.True(errorId.HasSet);
    }

    [Fact]
    public async Task GetPouDeclaration_ReturnsFbBlockWithoutMethodBodies()
    {
        var reader = await Primed(Fixtures.SampleProject);

        var decl = await reader.GetPouDeclarationAsync("FB_Example", null, CancellationToken.None);

        Assert.Equal(PouType.FunctionBlock, decl.PouType);
        Assert.Contains("FUNCTION_BLOCK FB_Example", decl.Declaration);
        Assert.Contains("VAR_INPUT", decl.Declaration);
    }

    [Fact]
    public async Task GetPouItem_Method_ReturnsDeclarationAndBody()
    {
        var reader = await Primed(Fixtures.SampleProject);

        var item = await reader.GetPouItemAsync("FB_Example", "Execute", null, CancellationToken.None);

        Assert.Equal("Execute", item.ItemName);
        Assert.Contains("METHOD", item.Declaration);
        Assert.NotEqual("", item.Body);
    }

    [Fact]
    public async Task GetPouItem_PropertyGetAccessor_Resolves()
    {
        var reader = await Primed(Fixtures.SampleProject);

        var item = await reader.GetPouItemAsync("FB_Example", "ErrorId.Get", null, CancellationToken.None);

        Assert.Equal("ErrorId.Get", item.ItemName);
    }

    [Fact]
    public async Task GetPouItem_UnknownItem_Throws()
    {
        var reader = await Primed(Fixtures.SampleProject);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => reader.GetPouItemAsync("FB_Example", "NoSuchThing", null, CancellationToken.None));
    }

    [Fact]
    public async Task GetGvl_ReturnsDeclaration()
    {
        var reader = await Primed(Fixtures.SampleProject);

        var gvl = await reader.GetGvlAsync("GVL_Params", null, CancellationToken.None);

        Assert.Equal("GVL_Params", gvl.Name);
        Assert.EndsWith("GVL_Params.TcGVL", gvl.Path);
        Assert.Contains("VAR_GLOBAL", gvl.Declaration);
    }

    [Fact]
    public async Task GetDut_StructAndEnum_ClassifyCorrectly()
    {
        var reader = await Primed(Fixtures.SampleProject);

        var config = await reader.GetDutAsync("ST_ExampleConfig", null, CancellationToken.None);
        Assert.Equal(DutKind.Struct, config.DutKind);
        Assert.Equal("", config.BaseType);
        Assert.Contains("STRUCT", config.Declaration);

        var state = await reader.GetDutAsync("E_ExampleState", null, CancellationToken.None);
        Assert.Equal(DutKind.Enum, state.DutKind);
    }

    [Fact]
    public async Task GetDut_AmbiguousAcrossPlcs_ThrowsUnlessDisambiguated()
    {
        var reader = await Primed(Fixtures.MultiProject);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => reader.GetDutAsync("E_State", null, CancellationToken.None));
        Assert.Contains("multiple PLC projects", ex.Message);

        var scoped = await reader.GetDutAsync("E_State", "Library", CancellationToken.None);
        Assert.EndsWith(Path.Combine("Library", "E_State.TcDUT"), scoped.Path);
    }

    [Fact]
    public async Task PerSymbolRead_BeforeGetStructure_Throws()
    {
        var reader = new XmlProjectReader();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => reader.GetGvlAsync("GVL_Params", null, CancellationToken.None));
    }
}
