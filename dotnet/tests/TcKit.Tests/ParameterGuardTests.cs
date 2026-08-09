using TcKit.Adapters.Automation;
using TcKit.Core.Authoring;

namespace TcKit.Tests;

/// <summary>
/// The library-parameter persistence lane (TASKS task 5): PlcProjXml's generalised reference
/// matching + parameter verification, and the ParameterGuard that re-splices blocks an XAE save
/// silently dropped. All guard state is static, so every test starts from Clear(); guard-touching
/// tests live only in this class.
/// </summary>
public sealed class ParameterGuardTests : IDisposable
{
    private const string PlcProjTemplate =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <ItemGroup>
            <PlaceholderReference Include="TcUnit">
              <DefaultResolution>TcUnit, * (www.tcunit.org)</DefaultResolution>
            </PlaceholderReference>
            <LibraryReference Include="Tc2_Standard,3.3.3.0,Beckhoff Automation GmbH">
              <Namespace>Tc2_Standard</Namespace>
            </LibraryReference>
          </ItemGroup>
        </Project>
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tckit-guard-" + Guid.NewGuid().ToString("N"));
    private readonly string _plcProj;

    public ParameterGuardTests()
    {
        Directory.CreateDirectory(_dir);
        _plcProj = Path.Combine(_dir, "Plc.plcproj");
        File.WriteAllText(_plcProj, PlcProjTemplate);
        ParameterGuard.Clear();
    }

    public void Dispose()
    {
        ParameterGuard.Clear();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone.
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Params(
        string list, string key, string value)
        => new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            [list] = new Dictionary<string, string> { [key] = value },
        };

    // --- PlcProjXml generalisation --------------------------------------------

    [Fact]
    public void SetReferenceParameters_OnLibraryReference_MatchesNameSegment()
    {
        var parameters = Params("GVL_Param_TcUnit", "xUnitEnablePublish", "TRUE");

        PlcProjXml.SetReferenceParameters(_plcProj, PlcProjXml.LibraryElement, "Tc2_Standard", parameters);

        Assert.True(PlcProjXml.HasParameters(_plcProj, PlcProjXml.LibraryElement, "Tc2_Standard", parameters));
        Assert.Contains("XUNITENABLEPUBLISH", File.ReadAllText(_plcProj)); // keys uppercased on disk
    }

    [Fact]
    public void HasParameters_FalseWhenValueDiffers()
    {
        PlcProjXml.SetReferenceParameters(
            _plcProj, PlcProjXml.PlaceholderElement, "TcUnit", Params("L", "K", "TRUE"));

        Assert.True(PlcProjXml.HasParameters(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", Params("L", "K", "TRUE")));
        Assert.False(PlcProjXml.HasParameters(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", Params("L", "K", "FALSE")));
        Assert.False(PlcProjXml.HasParameters(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", Params("L", "Other", "TRUE")));
    }

    [Fact]
    public void HasReference_BothKinds()
    {
        Assert.True(PlcProjXml.HasReference(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit"));
        Assert.True(PlcProjXml.HasReference(_plcProj, PlcProjXml.LibraryElement, "Tc2_Standard"));
        Assert.False(PlcProjXml.HasReference(_plcProj, PlcProjXml.LibraryElement, "Absent"));
    }

    // --- the guard -------------------------------------------------------------

    [Fact]
    public void VerifyOrRestore_IntactBlock_DoesNothing()
    {
        var parameters = Params("GVL_Param_TcUnit", "xUnitEnablePublish", "TRUE");
        PlcProjXml.SetReferenceParameters(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", parameters);
        ParameterGuard.Register(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", parameters);
        var session = new FakeSession { SolutionPath = Path.Combine(_dir, "Fake.sln") };

        var restored = ParameterGuard.VerifyOrRestore(session);

        Assert.Empty(restored);
        Assert.False(session.Closed);
    }

    [Fact]
    public void VerifyOrRestore_DroppedBlock_ResplicesWithReloadCycle()
    {
        var parameters = Params("GVL_Param_TcUnit", "xUnitEnablePublish", "TRUE");
        PlcProjXml.SetReferenceParameters(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", parameters);
        ParameterGuard.Register(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", parameters);

        // Simulate the XAE save that regenerates the .plcproj from a stale in-memory tree.
        File.WriteAllText(_plcProj, PlcProjTemplate);
        var session = new FakeSession { SolutionPath = Path.Combine(_dir, "Fake.sln") };

        var restored = ParameterGuard.VerifyOrRestore(session);

        Assert.Equal(["TcUnit"], restored);
        Assert.True(session.Closed); // the restore reloads the solution so XAE re-reads the block
        Assert.True(PlcProjXml.HasParameters(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", parameters));
    }

    [Fact]
    public void ReadReferenceParameters_RoundTripsSplicedBlocks()
    {
        PlcProjXml.SetReferenceParameters(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", Params("L1", "K1", "TRUE"));
        PlcProjXml.SetReferenceParameters(_plcProj, PlcProjXml.LibraryElement, "Tc2_Standard", Params("L2", "K2", "42"));

        var blocks = PlcProjXml.ReadReferenceParameters(_plcProj);

        Assert.Equal(2, blocks.Count);
        var placeholder = Assert.Single(blocks, b => b.ElementName == PlcProjXml.PlaceholderElement);
        Assert.Equal("TcUnit", placeholder.ReferenceName);
        Assert.Equal("TRUE", placeholder.Parameters["L1"]["K1"]);
        var library = Assert.Single(blocks, b => b.ElementName == PlcProjXml.LibraryElement);
        Assert.Equal("Tc2_Standard", library.ReferenceName);
        Assert.Equal("42", library.Parameters["L2"]["K2"]);
    }

    [Fact]
    public void SeedFromDisk_AdoptsBlocksFromAnEarlierProcess_AndRestoresAfterDrop()
    {
        // Process 1 splices and dies (Clear simulates the CLI's one-verb process boundary).
        var parameters = Params("GVL_Param_TcUnit", "xUnitEnablePublish", "TRUE");
        PlcProjXml.SetReferenceParameters(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", parameters);
        ParameterGuard.Clear();

        // Process 2 seeds from disk, then an XAE save regenerates the file without the block.
        ParameterGuard.SeedFromDisk(_dir);
        File.WriteAllText(_plcProj, PlcProjTemplate);
        var session = new FakeSession { SolutionPath = Path.Combine(_dir, "Fake.sln") };

        var restored = ParameterGuard.VerifyOrRestore(session);

        Assert.Equal(["TcUnit"], restored);
        Assert.True(PlcProjXml.HasParameters(
            _plcProj, PlcProjXml.PlaceholderElement, "TcUnit",
            Params("GVL_PARAM_TCUNIT", "XUNITENABLEPUBLISH", "TRUE")));
    }

    [Fact]
    public void SeedFromDisk_ExistingRegistrationWins()
    {
        // Disk carries a stale TRUE; this process has already registered the newer FALSE.
        PlcProjXml.SetReferenceParameters(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", Params("L", "K", "TRUE"));
        ParameterGuard.Register(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", Params("L", "K", "FALSE"));

        ParameterGuard.SeedFromDisk(_dir);
        var session = new FakeSession { SolutionPath = Path.Combine(_dir, "Fake.sln") };
        ParameterGuard.VerifyOrRestore(session);

        Assert.True(PlcProjXml.HasParameters(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", Params("L", "K", "FALSE")));
    }

    [Fact]
    public void VerifyOrRestore_ReferenceDeleted_DropsEntrySilently()
    {
        var parameters = Params("L", "K", "V");
        PlcProjXml.SetReferenceParameters(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", parameters);
        ParameterGuard.Register(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", parameters);

        // The placeholder itself is gone: a deliberate delete, not a lost block.
        File.WriteAllText(_plcProj, PlcProjTemplate.Replace("PlaceholderReference", "RemovedReference"));
        var session = new FakeSession { SolutionPath = Path.Combine(_dir, "Fake.sln") };

        Assert.Empty(ParameterGuard.VerifyOrRestore(session));
        Assert.False(session.Closed);

        // And the entry is forgotten: restoring the file does not resurrect the block.
        File.WriteAllText(_plcProj, PlcProjTemplate);
        Assert.Empty(ParameterGuard.VerifyOrRestore(session));
    }

    [Fact]
    public void Register_MergesAcrossCalls()
    {
        var first = Params("GVL_Param_TcUnit", "xUnitEnablePublish", "TRUE");
        var second = Params("GVL_Param_TcUnit", "xUnitFilePath", "C:\\results.xml");
        PlcProjXml.SetReferenceParameters(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", first);
        PlcProjXml.SetReferenceParameters(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", second);
        ParameterGuard.Register(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", first);
        ParameterGuard.Register(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", second);

        File.WriteAllText(_plcProj, PlcProjTemplate); // drop both
        ParameterGuard.VerifyOrRestore(new FakeSession { SolutionPath = Path.Combine(_dir, "Fake.sln") });

        Assert.True(PlcProjXml.HasParameters(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", first));
        Assert.True(PlcProjXml.HasParameters(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", second));
    }

    [Fact]
    public void Unregister_StopsGuarding()
    {
        var parameters = Params("L", "K", "V");
        PlcProjXml.SetReferenceParameters(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", parameters);
        ParameterGuard.Register(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", parameters);
        ParameterGuard.Unregister(_plcProj, "TcUnit");

        File.WriteAllText(_plcProj, PlcProjTemplate); // drop the block

        Assert.Empty(ParameterGuard.VerifyOrRestore(new FakeSession { SolutionPath = Path.Combine(_dir, "Fake.sln") }));
        Assert.False(PlcProjXml.HasParameters(_plcProj, PlcProjXml.PlaceholderElement, "TcUnit", parameters));
    }

    // --- author wiring -----------------------------------------------------------

    [Fact]
    public void AddLibraryReference_WithParameters_SplicesAndRegisters()
    {
        var (session, _, _, references) = FakeProject.BuildWithReferences("Plc");
        session.SolutionPath = Path.Combine(_dir, "Fake.sln");
        var parameters = Params("GVL_Param_TcUnit", "xUnitEnablePublish", "TRUE");

        var result = ProjectAuthor.AddLibraryReference(
            session, null, "Tc2_Standard", "*", "Beckhoff Automation GmbH", parameters);

        Assert.True(result.Success);
        Assert.NotNull(references["Plc"].FindDirect("Tc2_Standard"));
        Assert.True(session.Closed); // splice runs the close/reopen cycle
        Assert.True(PlcProjXml.HasParameters(_plcProj, PlcProjXml.LibraryElement, "Tc2_Standard", parameters));

        // And it is guarded: a save that drops the block gets restored.
        File.WriteAllText(_plcProj, PlcProjTemplate);
        var restored = ParameterGuard.VerifyOrRestore(session);
        Assert.Equal(["Tc2_Standard"], restored);
    }

    [Fact]
    public void SetPlaceholderParameters_RegistersWithGuard()
    {
        var (session, _, _, _) = FakeProject.BuildWithReferences("Plc");
        session.SolutionPath = Path.Combine(_dir, "Fake.sln");
        var parameters = Params("GVL_Param_TcUnit", "xUnitEnablePublish", "TRUE");

        var result = ProjectAuthor.SetPlaceholderParameters(session, null, "TcUnit", parameters);

        Assert.True(result.Success);
        File.WriteAllText(_plcProj, PlcProjTemplate);
        Assert.Equal(["TcUnit"], ParameterGuard.VerifyOrRestore(session));
    }
}
