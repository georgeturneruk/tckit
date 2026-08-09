using System.Runtime.CompilerServices;
using TcKit.Adapters.Xml;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>
/// Behavioural tests for <see cref="XmlProjectReader.GetStructureAsync"/> against the in-repo
/// fixtures. This is the primary verification gate for the get_structure port (ADR-0015); the
/// Python parity oracle is a supplementary cross-check, not a byte gate.
/// </summary>
public class XmlProjectReaderTests
{
    private static readonly XmlProjectReader s_reader = new();

    private static Task<ProjectStructure> Read(string path, string? plc = null)
        => s_reader.GetStructureAsync(path, plc, CancellationToken.None);

    // --- single PLC, no .sln -------------------------------------------------

    [Fact]
    public async Task GetStructure_SingleProject_HasOnePlcAndNoSolution()
    {
        var structure = await Read(SampleProject);

        Assert.Equal(["SampleProject"], structure.Plcs.Keys);
        Assert.Equal("", structure.SolutionPath);
        Assert.Equal(SampleProject, structure.ProjectPath);
    }

    [Fact]
    public async Task GetStructure_SingleProject_ClassifiesPousByType()
    {
        var plc = (await Read(SampleProject)).Plcs["SampleProject"];

        Assert.Equal(PouType.FunctionBlock, PouNamed(plc, "FB_Example").PouType);
        Assert.Equal(PouType.Interface, PouNamed(plc, "I_Example").PouType);
        Assert.All(plc.Pous, p => Assert.Equal("", p.Folder));
    }

    [Fact]
    public async Task GetStructure_SingleProject_ClassifiesDutsAndGvls()
    {
        var plc = (await Read(SampleProject)).Plcs["SampleProject"];

        Assert.Contains(plc.Gvls, g => g.Name == "GVL_Params");
        Assert.Equal(DutKind.Enum, DutNamed(plc, "E_ExampleState").DutKind);
        Assert.Equal(DutKind.Struct, DutNamed(plc, "ST_ExampleConfig").DutKind);
    }

    [Fact]
    public async Task GetStructure_SingleProject_ReadsLibrariesIncludingDirectReference()
    {
        var plc = (await Read(SampleProject)).Plcs["SampleProject"];

        Assert.Contains(plc.Libraries, l => l.Name == "Tc2_Standard" && l.Placeholder == "Tc2_Standard");
        var baseInterfaces = Assert.Single(plc.Libraries, l => l.Name == "Base Interfaces");
        Assert.Null(baseInterfaces.Placeholder);
        Assert.Equal("newest", baseInterfaces.Version);
    }

    [Fact]
    public async Task GetStructure_SingleProject_ReadsTaskFromTcTto()
    {
        var task = Assert.Single((await Read(SampleProject)).Tasks, t => t.Name == "PlcTask");

        Assert.Equal(10000, task.CycleTimeUs);
        Assert.Equal(20, task.Priority);
        Assert.Contains("PRG_MAIN", task.Programs);
    }

    // --- multi PLC, with .sln ------------------------------------------------

    [Fact]
    public async Task GetStructure_MultiProject_GroupsPousByPlc()
    {
        var structure = await Read(MultiProject);

        Assert.Equal(["Library", "Tests"], structure.Plcs.Keys.OrderBy(k => k).ToArray());
        Assert.Contains(structure.Plcs["Library"].Pous, p => p.Name == "FB_Filter");
        Assert.Contains(structure.Plcs["Tests"].Pous, p => p.Name == "FB_FilterTests");
        Assert.Contains(structure.Plcs["Library"].Duts, d => d.Name == "E_State");
        Assert.Contains(structure.Plcs["Tests"].Duts, d => d.Name == "E_State");
    }

    [Fact]
    public async Task GetStructure_MultiProject_ResolvesSolutionPath()
    {
        var structure = await Read(MultiProject);

        Assert.True(Path.IsPathRooted(structure.SolutionPath));
        Assert.EndsWith("multi_project_sln.sln", structure.SolutionPath);
    }

    [Fact]
    public async Task GetStructure_SlnFilePath_IsShorthandForItsDirectory()
    {
        var viaDir = await Read(MultiProject);
        var viaSln = await Read(Path.Combine(MultiProject, "multi_project_sln.sln"));

        Assert.Equal(viaDir.Plcs.Keys.OrderBy(k => k), viaSln.Plcs.Keys.OrderBy(k => k));
    }

    // --- deploy-style .sln referencing projects outside its directory --------

    [Fact]
    public async Task GetStructure_DeploySln_FollowsExternalProjectReferences()
    {
        var structure = await Read(DeploySln);

        // The referenced PLC lives under ..\src, outside the solution directory; without
        // following the .sln references the reader would synthesise an empty "deploy" PLC.
        var plc = Assert.Single(structure.Plcs).Value;
        Assert.Equal("Machine_Plc", plc.Name);
        Assert.EndsWith("Machine_Plc.plcproj", plc.PlcprojPath);
        Assert.Single(plc.Pous, p => p.Name == "FB_Remote");
    }

    [Fact]
    public async Task GetStructure_DeploySln_MergesTasksFromInternalAndExternalTsprojs()
    {
        var tasks = (await Read(DeploySln)).Tasks;

        Assert.Contains(tasks, t => t.Name == "ShellTask");
        Assert.Contains(tasks, t => t.Name == "MachineTask");
    }

    [Fact]
    public async Task GetStructure_DeploySln_SolutionPathIsThePassedSln()
    {
        var structure = await Read(DeploySln);

        Assert.Equal(Path.GetFullPath(DeploySln), structure.SolutionPath);
    }

    [Fact]
    public async Task GetStructure_DeploySln_ExternalSymbolsResolveAfterIndexing()
    {
        await Read(DeploySln);
        var pou = await s_reader.GetPouInterfaceAsync("FB_Remote", null, CancellationToken.None);

        Assert.Contains("FUNCTION_BLOCK FB_Remote", pou.Declaration);
    }

    // --- nested folders + plc_name filter ------------------------------------

    [Fact]
    public async Task GetStructure_PlcNameFilter_RestrictsToOnePlcAndKeepsNestedFolders()
    {
        var structure = await Read(T3Solution, "T3TckitUtils_Plc");

        Assert.Equal(["T3TckitUtils_Plc"], structure.Plcs.Keys);
        var plc = structure.Plcs["T3TckitUtils_Plc"];
        Assert.Equal("POUs/PID", PouNamed(plc, "FB_Pid").Folder);
        Assert.Equal("POUs", PouNamed(plc, "MAIN").Folder);
        Assert.Equal(PouType.Program, PouNamed(plc, "MAIN").PouType);

        var contains = PouNamed(plc, "F_Contains");
        Assert.Equal(PouType.Function, contains.PouType);
        Assert.Equal("POUs/Strings", contains.Folder);
    }

    [Fact]
    public async Task GetStructure_IndexesTcIoInterfaces()
    {
        var structure = await Read(T3Solution, "T3TckitUtils_Plc");

        // I_Pid lives in a .TcIO file (XAE's on-disk shape for interfaces).
        var itf = PouNamed(structure.Plcs["T3TckitUtils_Plc"], "I_Pid");
        Assert.Equal(PouType.Interface, itf.PouType);
        Assert.EndsWith("I_Pid.TcIO", itf.Path);
        Assert.Equal("POUs/PID", itf.Folder);
    }

    [Fact]
    public async Task GetStructure_UnknownPlcName_Throws()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Read(T3Solution, "NoSuchPlc"));

        Assert.Contains("does not match any PLC project", ex.Message);
    }

    // --- helpers -------------------------------------------------------------

    private static PouRef PouNamed(PlcSection plc, string name)
        => Assert.Single(plc.Pous, p => p.Name == name);

    private static DutRef DutNamed(PlcSection plc, string name)
        => Assert.Single(plc.Duts, d => d.Name == name);

    // --- POU type detection --------------------------------------------------

    [Theory]
    [InlineData("PROGRAM PRG_Test\nVAR\n    WriteProtectedFunctions : FB_Wpf;\nEND_VAR", PouType.Program)]
    [InlineData("FUNCTION_BLOCK FB_Host\nVAR\nEND_VAR", PouType.FunctionBlock)]
    [InlineData("FUNCTION F_Add : INT\nVAR_INPUT\nEND_VAR", PouType.Function)]
    [InlineData("(* FUNCTION in a comment *)\nPROGRAM PRG_Test\nVAR\nEND_VAR", PouType.Program)]
    [InlineData("PROGRAM PRG_Test\nVAR\n    Note : STRING := 'FUNCTION_BLOCK';\nEND_VAR", PouType.Program)]
    public async Task GetStructure_PouType_ComesFromTheHeaderNotTheWholeDeclaration(
        string declaration, PouType expected)
    {
        // TcUnit's PRG_TEST declares "WriteProtectedFunctions : FB_WriteProtectedFunctions", whose
        // "Functions" made a substring search read the PROGRAM as a FUNCTION.
        var directory = Path.Combine(Path.GetTempPath(), $"tckit-pou-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "Subject.TcPOU"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
                + "<TcPlcObject Version=\"1.1.0.1\">\n"
                + "  <POU Name=\"Subject\" Id=\"{00000000-0000-0000-0000-000000000001}\">\n"
                + $"    <Declaration><![CDATA[{declaration}]]></Declaration>\n"
                + "    <Implementation><ST><![CDATA[]]></ST></Implementation>\n"
                + "  </POU>\n"
                + "</TcPlcObject>\n");

            var structure = await new XmlProjectReader()
                .GetStructureAsync(directory, null, CancellationToken.None);

            Assert.Equal(expected, Assert.Single(structure.Plcs.Values).Pous.Single().PouType);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string SampleProject => Path.Combine(RepoRoot(), "tests", "fixtures", "sample_project");

    private static string MultiProject => Path.Combine(RepoRoot(), "tests", "fixtures", "multi_project_sln");

    private static string DeploySln => Path.Combine(
        RepoRoot(), "tests", "fixtures", "deploy_sln", "deploy", "DeploySolution.sln");

    private static string T3Solution => Path.Combine(
        RepoRoot(), "bench", "fixtures", "bug-hunting", "T3-tckit-utils", "T3TckitUtils.sln");

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        // thisFile = <repo>/dotnet/tests/TcKit.Tests/XmlProjectReaderTests.cs
        var testsDir = Path.GetDirectoryName(thisFile)!;        // TcKit.Tests
        return Directory.GetParent(testsDir)!.Parent!.Parent!.FullName; // tests -> dotnet -> repo root
    }
}
