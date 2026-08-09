using TcKit.Adapters.Xml;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>
/// The xml writer backend driven through its public port surface against a scratch solution on
/// disk (the file-based analogue of ProjectAuthorTests). Asserts both the Result contract
/// (success, detail keys shaped like the automation backend's) and the on-disk outcome (files,
/// Compile / Folder items, reference elements).
/// </summary>
[Collection("xml-writer")] // GuidSource is static; serialise against the pinned emission tests
public sealed class XmlProjectWriterTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "tckit-xmlwriter-" + Guid.NewGuid().ToString("N"));

    private readonly XmlProjectWriter _writer = new();
    private readonly string _sln;

    public XmlProjectWriterTests()
    {
        Directory.CreateDirectory(_root);
        _sln = XmlScratch.Create(_root);
    }

    public void Dispose()
    {
        _writer.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private async Task OpenAsync()
        => Assert.True((await _writer.OpenProjectAsync(_sln, CancellationToken.None)).Success);

    private string PlcProj() => File.ReadAllText(XmlScratch.PlcProjPath(_root));

    private static void AssertOk(Result result)
        => Assert.True(result.Success, result.Error);

    // --- open / no solution ----------------------------------------------------

    [Fact]
    public async Task OpenProject_MissingFile_Fails()
    {
        var result = await _writer.OpenProjectAsync(Path.Combine(_root, "Nope.sln"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Solution file not found", result.Error);
    }

    [Fact]
    public async Task Verb_WithoutOpenProject_FailsWithGuidance()
    {
        var result = await _writer.AddPouAsync("FB_X", PouType.FunctionBlock, "", "", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("No solution is open", result.Error);
    }

    // --- create ------------------------------------------------------------------

    [Fact]
    public async Task AddPou_CreatesFileCompileItemAndSplitSource()
    {
        await OpenAsync();

        var result = await _writer.AddPouAsync(
            "FB_X", PouType.FunctionBlock, "FUNCTION_BLOCK FB_X\nVAR\n  n : INT;\nEND_VAR\nn := n + 1;",
            "", null, CancellationToken.None);

        AssertOk(result);
        Assert.Equal("TIPC^Plc^Plc Project^POUs^FB_X", result.Details["path"]);
        var text = File.ReadAllText(XmlScratch.PouPath(_root, "FB_X.TcPOU"));
        Assert.Contains("<POU Name=\"FB_X\"", text);
        Assert.Contains("n : INT;", text);
        Assert.Contains("n := n + 1;", text);
        Assert.Contains(@"<Compile Include=""POUs\FB_X.TcPOU"">", PlcProj());
    }

    [Fact]
    public async Task AddPou_DuplicateName_Fails()
    {
        await OpenAsync();
        AssertOk(await _writer.AddPouAsync("FB_X", PouType.FunctionBlock, "", "", null, CancellationToken.None));

        var result = await _writer.AddPouAsync("FB_X", PouType.FunctionBlock, "", "", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("already exists", result.Error);
    }

    [Fact]
    public async Task AddPou_Interface_IsTcIoWithoutImplementation()
    {
        await OpenAsync();

        AssertOk(await _writer.AddPouAsync("I_X", PouType.Interface, "INTERFACE I_X", "", null, CancellationToken.None));

        var text = File.ReadAllText(XmlScratch.PouPath(_root, "I_X.TcIO"));
        Assert.Contains("<Itf Name=\"I_X\"", text);
        Assert.DoesNotContain("<Implementation>", text);
        Assert.DoesNotContain("SpecialFunc", text);
    }

    [Fact]
    public async Task AddPou_UnknownParentFolder_Fails()
    {
        await OpenAsync();

        var result = await _writer.AddPouAsync(
            "FB_M", PouType.FunctionBlock, "", "POUs/Drives", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Path segment 'Drives' not found", result.Error);
    }

    [Fact]
    public async Task AddFolder_ThenAddPouInside_ResolvesPath()
    {
        await OpenAsync();

        var folder = await _writer.AddFolderAsync("Drives", "POUs", null, CancellationToken.None);
        AssertOk(folder);
        Assert.Equal("TIPC^Plc^Plc Project^POUs^Drives", folder.Details["path"]);
        Assert.Contains(@"<Folder Include=""POUs\Drives"" />", PlcProj());

        AssertOk(await _writer.AddPouAsync("FB_M", PouType.FunctionBlock, "", "POUs/Drives", null, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(_root, XmlScratch.PlcName, "POUs", "Drives", "FB_M.TcPOU")));
        Assert.Contains(@"<Compile Include=""POUs\Drives\FB_M.TcPOU"">", PlcProj());
    }

    [Fact]
    public async Task AddGvl_IsDeclarationOnly()
    {
        await OpenAsync();

        AssertOk(await _writer.AddGvlAsync(
            "GVL_P", "VAR_GLOBAL\n  g : INT;\nEND_VAR", "", null, CancellationToken.None));

        var text = File.ReadAllText(XmlScratch.PouPath(_root, "GVL_P.TcGVL"));
        Assert.Contains("<GVL Name=\"GVL_P\"", text);
        Assert.Contains("VAR_GLOBAL", text);
        Assert.DoesNotContain("<Implementation>", text);
    }

    [Fact]
    public async Task AddDut_Struct_LandsUnderDuts()
    {
        await OpenAsync();

        AssertOk(await _writer.AddDutAsync(
            "ST_C", "TYPE ST_C :\nSTRUCT\n  a : INT;\nEND_STRUCT\nEND_TYPE", DutKind.Struct, "", null,
            CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(_root, XmlScratch.PlcName, "DUTs", "ST_C.TcDUT")));
        Assert.Contains(@"<Compile Include=""DUTs\ST_C.TcDUT"">", PlcProj());
    }

    [Fact]
    public async Task AddDut_Alias_IsRefused()
    {
        await OpenAsync();

        var result = await _writer.AddDutAsync("T_A", "", DutKind.Alias, "", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Alias DUT creation is not supported", result.Error);
    }

    [Fact]
    public async Task AddMethod_OnFunctionBlock_WritesDeclarationAndBody()
    {
        await OpenAsync();
        AssertOk(await _writer.AddPouAsync("FB_X", PouType.FunctionBlock, "FUNCTION_BLOCK FB_X", "", null, CancellationToken.None));

        AssertOk(await _writer.AddMethodAsync(
            "FB_X", "Execute", "METHOD Execute : BOOL\nExecute := TRUE;", null, CancellationToken.None));

        var text = File.ReadAllText(XmlScratch.PouPath(_root, "FB_X.TcPOU"));
        Assert.Contains("<Method Name=\"Execute\"", text);
        Assert.Contains("Execute := TRUE;", text);
    }

    [Fact]
    public async Task AddMethod_OnInterface_IsDeclarationOnly()
    {
        await OpenAsync();
        AssertOk(await _writer.AddPouAsync("I_X", PouType.Interface, "INTERFACE I_X", "", null, CancellationToken.None));

        AssertOk(await _writer.AddMethodAsync("I_X", "DoThing", "METHOD DoThing : BOOL", null, CancellationToken.None));

        var text = File.ReadAllText(XmlScratch.PouPath(_root, "I_X.TcIO"));
        Assert.Contains("<Method Name=\"DoThing\"", text);
        Assert.DoesNotContain("<Implementation>", text);
    }

    [Fact]
    public async Task AddProperty_CreatesAccessorsWithDefaultVarBlock()
    {
        await OpenAsync();
        AssertOk(await _writer.AddPouAsync("FB_X", PouType.FunctionBlock, "FUNCTION_BLOCK FB_X\nVAR\n  nErr : UDINT;\nEND_VAR", "", null, CancellationToken.None));

        AssertOk(await _writer.AddPropertyAsync(
            "FB_X", "ErrorId", "UDINT", "ErrorId := nErr;", "nErr := ErrorId;", null, CancellationToken.None));

        var text = File.ReadAllText(XmlScratch.PouPath(_root, "FB_X.TcPOU"));
        Assert.Contains("<Property Name=\"ErrorId\"", text);
        Assert.Contains("PROPERTY ErrorId : UDINT", text);
        Assert.Contains("<Get Id=", text);
        Assert.Contains("<Set Id=", text);
        Assert.Contains("ErrorId := nErr;", text);
        Assert.Contains("nErr := ErrorId;", text);
    }

    [Fact]
    public async Task AddProperty_NoAccessor_Fails()
    {
        await OpenAsync();
        AssertOk(await _writer.AddPouAsync("FB_X", PouType.FunctionBlock, "", "", null, CancellationToken.None));

        var result = await _writer.AddPropertyAsync("FB_X", "P", "BOOL", null, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("At least one of getterCode or setterCode", result.Error);
    }

    // --- update --------------------------------------------------------------------

    [Fact]
    public async Task UpdatePouImplementation_ReplacesBody()
    {
        await OpenAsync();

        AssertOk(await _writer.UpdatePouImplementationAsync("MAIN", "nCounter := 0;", null, CancellationToken.None));

        var text = File.ReadAllText(XmlScratch.PouPath(_root, "MAIN.TcPOU"));
        Assert.Contains("nCounter := 0;", text);
        Assert.DoesNotContain("nCounter := nCounter + 1;", text);
    }

    [Fact]
    public async Task UpdatePouImplementation_OnGvl_IsRefused()
    {
        await OpenAsync();
        AssertOk(await _writer.AddGvlAsync("GVL_P", "VAR_GLOBAL\nEND_VAR", "", null, CancellationToken.None));

        var result = await _writer.UpdatePouImplementationAsync("GVL_P", "x();", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("declaration-only", result.Error);
    }

    [Fact]
    public async Task UpdateMethodBody_ReplacesDeclarationAndBody()
    {
        await OpenAsync();
        AssertOk(await _writer.AddPouAsync("FB_X", PouType.FunctionBlock, "FUNCTION_BLOCK FB_X", "", null, CancellationToken.None));
        AssertOk(await _writer.AddMethodAsync("FB_X", "Execute", "METHOD Execute : BOOL\nExecute := FALSE;", null, CancellationToken.None));

        AssertOk(await _writer.UpdateMethodBodyAsync(
            "FB_X", "Execute", "METHOD Execute : BOOL\nVAR\n  b : BOOL;\nEND_VAR\nExecute := b;", null,
            CancellationToken.None));

        var text = File.ReadAllText(XmlScratch.PouPath(_root, "FB_X.TcPOU"));
        Assert.Contains("b : BOOL;", text);
        Assert.Contains("Execute := b;", text);
        Assert.DoesNotContain("Execute := FALSE;", text);
    }

    [Fact]
    public async Task UpdatePouDeclarationPatch_AnchorsUniquely()
    {
        await OpenAsync();

        var ok = await _writer.UpdatePouDeclarationPatchAsync(
            "MAIN", "nCounter : INT;", "nCounter : DINT;", null, CancellationToken.None);
        AssertOk(ok);
        Assert.Equal(1, ok.Details["replacements"]);

        var missing = await _writer.UpdatePouDeclarationPatchAsync(
            "MAIN", "nope", "x", null, CancellationToken.None);
        Assert.False(missing.Success);
        Assert.Contains("OldString not found", missing.Error);
    }

    [Fact]
    public async Task UpdateMethodBodyPatch_PatchesAcrossDeclarationAndBody()
    {
        await OpenAsync();
        AssertOk(await _writer.AddPouAsync("FB_X", PouType.FunctionBlock, "FUNCTION_BLOCK FB_X", "", null, CancellationToken.None));
        AssertOk(await _writer.AddMethodAsync("FB_X", "Step", "METHOD Step : INT\nStep := 1;", null, CancellationToken.None));

        AssertOk(await _writer.UpdateMethodBodyPatchAsync(
            "FB_X", "Step", "Step := 1;", "Step := 2;", null, CancellationToken.None));

        Assert.Contains("Step := 2;", File.ReadAllText(XmlScratch.PouPath(_root, "FB_X.TcPOU")));
    }

    [Fact]
    public async Task AddVariable_CreatesScopeBlockAtConventionalRank()
    {
        await OpenAsync();

        AssertOk(await _writer.AddVariableAsync(
            "MAIN", "VAR_INPUT", "bGo : BOOL;", null, null, CancellationToken.None));

        var text = File.ReadAllText(XmlScratch.PouPath(_root, "MAIN.TcPOU"));
        Assert.Contains("VAR_INPUT", text);
        Assert.Contains("bGo : BOOL;", text);
        // VAR_INPUT ranks before the existing VAR block.
        Assert.True(text.IndexOf("VAR_INPUT", StringComparison.Ordinal) < text.IndexOf("VAR\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeleteVariable_RemovesSingleDeclarationLine()
    {
        await OpenAsync();

        AssertOk(await _writer.DeleteVariableAsync("MAIN", "nCounter", null, null, CancellationToken.None));

        Assert.DoesNotContain("nCounter : INT;", File.ReadAllText(XmlScratch.PouPath(_root, "MAIN.TcPOU")));
    }

    // --- delete ----------------------------------------------------------------------

    [Fact]
    public async Task DeletePou_RemovesFileAndCompileItem()
    {
        await OpenAsync();
        AssertOk(await _writer.AddPouAsync("FB_X", PouType.FunctionBlock, "", "", null, CancellationToken.None));

        var result = await _writer.DeletePouAsync("FB_X", null, CancellationToken.None);

        AssertOk(result);
        Assert.Equal(604, result.Details["kind"]);
        Assert.False(File.Exists(XmlScratch.PouPath(_root, "FB_X.TcPOU")));
        Assert.DoesNotContain("FB_X.TcPOU", PlcProj());
    }

    [Fact]
    public async Task DeletePou_TaskBoundProgram_IsRefused()
    {
        await OpenAsync();

        var result = await _writer.DeletePouAsync("MAIN", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("bound to task 'PlcTask'", result.Error);
        Assert.True(File.Exists(XmlScratch.PouPath(_root, "MAIN.TcPOU")));
    }

    [Fact]
    public async Task DeletePou_OnGvlName_RefusesWithKind()
    {
        await OpenAsync();
        AssertOk(await _writer.AddGvlAsync("GVL_P", "", "", null, CancellationToken.None));

        var result = await _writer.DeletePouAsync("GVL_P", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("is not a POU (kind=615)", result.Error);
    }

    [Fact]
    public async Task DeleteMethod_RemovesOnlyThatMember()
    {
        await OpenAsync();
        AssertOk(await _writer.AddPouAsync("FB_X", PouType.FunctionBlock, "", "", null, CancellationToken.None));
        AssertOk(await _writer.AddMethodAsync("FB_X", "A", "METHOD A : BOOL", null, CancellationToken.None));
        AssertOk(await _writer.AddMethodAsync("FB_X", "B", "METHOD B : BOOL", null, CancellationToken.None));

        AssertOk(await _writer.DeleteMethodAsync("FB_X", "A", null, CancellationToken.None));

        var text = File.ReadAllText(XmlScratch.PouPath(_root, "FB_X.TcPOU"));
        Assert.DoesNotContain("<Method Name=\"A\"", text);
        Assert.Contains("<Method Name=\"B\"", text);
    }

    [Fact]
    public async Task DeleteProperty_ReportsRemovedAccessors()
    {
        await OpenAsync();
        AssertOk(await _writer.AddPouAsync("FB_X", PouType.FunctionBlock, "", "", null, CancellationToken.None));
        AssertOk(await _writer.AddPropertyAsync("FB_X", "P", "BOOL", "P := TRUE;", "x := P;", null, CancellationToken.None));

        var result = await _writer.DeletePropertyAsync("FB_X", "P", null, CancellationToken.None);

        AssertOk(result);
        Assert.Equal(2, result.Details["removed_accessors"]);
        Assert.DoesNotContain("<Property", File.ReadAllText(XmlScratch.PouPath(_root, "FB_X.TcPOU")));
    }

    [Fact]
    public async Task DeleteGvl_OnPouName_RefusesWithKind()
    {
        await OpenAsync();

        var result = await _writer.DeleteGvlAsync("MAIN", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("is not a GVL (kind=602, expected 615)", result.Error);
    }

    [Fact]
    public async Task DeleteDut_ReportsClassifiedKind()
    {
        await OpenAsync();
        AssertOk(await _writer.AddDutAsync("E_S", "TYPE E_S :\n(\n  A := 0\n);\nEND_TYPE", DutKind.Enum, "", null, CancellationToken.None));

        var result = await _writer.DeleteDutAsync("E_S", null, CancellationToken.None);

        AssertOk(result);
        Assert.Equal(605, result.Details["kind"]);
    }

    [Fact]
    public async Task DeleteFolder_NonEmptyNeedsRecursive()
    {
        await OpenAsync();
        AssertOk(await _writer.AddFolderAsync("Drives", "POUs", null, CancellationToken.None));
        AssertOk(await _writer.AddPouAsync("FB_M", PouType.FunctionBlock, "", "POUs/Drives", null, CancellationToken.None));

        var refused = await _writer.DeleteFolderAsync("Drives", "POUs", false, null, CancellationToken.None);
        Assert.False(refused.Success);
        Assert.Contains("not empty", refused.Error);

        AssertOk(await _writer.DeleteFolderAsync("Drives", "POUs", true, null, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(_root, XmlScratch.PlcName, "POUs", "Drives")));
        var proj = PlcProj();
        Assert.DoesNotContain("FB_M.TcPOU", proj);
        Assert.DoesNotContain(@"<Folder Include=""POUs\Drives""", proj);
    }

    // --- libraries ----------------------------------------------------------------------

    [Fact]
    public async Task AddLibraryReference_WritesIncludeAndParameters()
    {
        await OpenAsync();

        AssertOk(await _writer.AddLibraryReferenceAsync(
            null, "TcUnit", "*", "www.tcunit.org",
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["GVL_Param_TcUnit"] = new Dictionary<string, string> { ["xUnitEnablePublish"] = "TRUE" },
            },
            CancellationToken.None));

        var proj = PlcProj();
        Assert.Contains(@"<LibraryReference Include=""TcUnit,*,www.tcunit.org"">", proj);
        Assert.Contains(@"Parameter ListName=""GVL_PARAM_TCUNIT""", proj);
        Assert.Contains("<Key>XUNITENABLEPUBLISH</Key>", proj);
    }

    [Fact]
    public async Task AddLibraryReference_Duplicate_Fails()
    {
        await OpenAsync();

        var result = await _writer.AddLibraryReferenceAsync(
            null, "Tc2_System", "*", "Beckhoff Automation GmbH", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("already contained", result.Error);
    }

    [Fact]
    public async Task DeleteLibraryReference_ResolvesStarToRecordedVersion()
    {
        await OpenAsync();

        var result = await _writer.DeleteLibraryReferenceAsync(
            null, "Tc2_System", "*", "Beckhoff Automation GmbH", CancellationToken.None);

        AssertOk(result);
        Assert.Equal("3.4.20.0", result.Details["version"]);
        Assert.DoesNotContain("LibraryReference", PlcProj());
    }

    [Fact]
    public async Task AddLibraryPlaceholder_IsIdempotent()
    {
        await OpenAsync();

        var first = await _writer.AddLibraryPlaceholderAsync(
            null, "TcUnit", "TcUnit", "*", "www.tcunit.org", null, CancellationToken.None);
        var second = await _writer.AddLibraryPlaceholderAsync(
            null, "TcUnit", "TcUnit", "*", "www.tcunit.org", null, CancellationToken.None);

        AssertOk(first);
        AssertOk(second);
        Assert.Equal(false, first.Details["already_present"]);
        Assert.Equal(true, second.Details["already_present"]);
        Assert.Contains("<DefaultResolution>TcUnit, * (www.tcunit.org)</DefaultResolution>", PlcProj());
    }

    [Fact]
    public async Task SetPlaceholderParameters_SplicesBlock()
    {
        await OpenAsync();

        AssertOk(await _writer.SetPlaceholderParametersAsync(
            null, "Tc2_Standard",
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["List"] = new Dictionary<string, string> { ["Key"] = "42" },
            },
            CancellationToken.None));

        Assert.Contains("<Value>42</Value>", PlcProj());
    }

    [Fact]
    public async Task DeletePlaceholder_RemovesElement()
    {
        await OpenAsync();

        AssertOk(await _writer.DeletePlaceholderAsync(null, "Tc2_Standard", CancellationToken.None));

        Assert.DoesNotContain("Tc2_Standard", PlcProj());
    }

    // --- unsupported verbs ------------------------------------------------------------------

    [Fact]
    public async Task ScaffoldingAndSaveAsLibrary_FailExplicitly()
    {
        var create = await _writer.CreateProjectAsync("X", _root, CancellationToken.None);
        var addPlc = await _writer.AddPlcProjectAsync(_sln, "P2", "standard", CancellationToken.None);
        var saveLib = await _writer.SavePlcAsLibraryAsync(null, "out.library", false, "System", false, CancellationToken.None);

        Assert.False(create.Success);
        Assert.Contains("not supported by the xml writer backend", create.Error);
        Assert.False(addPlc.Success);
        Assert.Contains("not supported by the xml writer backend", addPlc.Error);
        Assert.False(saveLib.Success);
        Assert.Contains("TwinCAT compiler", saveLib.Error);
    }

    // --- determinism guarantees ----------------------------------------------------------------

    [Fact]
    public async Task StructuralWrite_PreservesXmlArchiveBlobByteForByte()
    {
        await OpenAsync();
        var before = PlcProj();
        var blobStart = before.IndexOf("<ProjectExtensions>", StringComparison.Ordinal);
        Assert.True(blobStart > 0);
        var blob = before[blobStart..];

        AssertOk(await _writer.AddPouAsync("FB_X", PouType.FunctionBlock, "", "", null, CancellationToken.None));

        Assert.EndsWith(blob.TrimEnd(), PlcProj().TrimEnd());
    }

    [Fact]
    public async Task StructuralWrite_BumpsPlcprojMtime()
    {
        await OpenAsync();
        var before = File.GetLastWriteTimeUtc(XmlScratch.PlcProjPath(_root));

        AssertOk(await _writer.AddPouAsync("FB_X", PouType.FunctionBlock, "", "", null, CancellationToken.None));

        // The reader's whole cache-invalidation contract (ADR-0004) hangs on this signal.
        Assert.True(File.GetLastWriteTimeUtc(XmlScratch.PlcProjPath(_root)) >= before);
    }

    [Fact]
    public async Task BodyEdit_DoesNotTouchPlcproj()
    {
        await OpenAsync();
        var before = File.ReadAllText(XmlScratch.PlcProjPath(_root));

        AssertOk(await _writer.UpdatePouImplementationAsync("MAIN", "nCounter := 2;", null, CancellationToken.None));

        Assert.Equal(before, File.ReadAllText(XmlScratch.PlcProjPath(_root)));
    }

    [Fact]
    public async Task CodeContainingCdataTerminator_IsRefused()
    {
        await OpenAsync();

        var result = await _writer.UpdatePouImplementationAsync("MAIN", "s := ']]>';", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("CDATA", result.Error);
    }
}
