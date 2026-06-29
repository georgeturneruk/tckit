using TcKit.Adapters.Automation;

namespace TcKit.Tests;

/// <summary>
/// COM hardware scan: the pure terminal-name parsing (order number, slot, master detection) and the
/// TIID-tree topology build against the fake seam, plus the scaffold codegen + end-to-end scaffold.
/// </summary>
public sealed class HardwareScanTests
{
    [Theory]
    [InlineData("Box 1 (EL1008)", "EL1008")]
    [InlineData("Term 2 (EK1100)", "EK1100")]
    [InlineData("Drive 3 (AX5206)", "AX5206")]
    [InlineData("No parens here", "")]
    public void OrderNumber_ExtractsTrailingParenthesised(string name, string expected)
        => Assert.Equal(expected, HardwareScan.OrderNumber(name));

    [Theory]
    [InlineData("Box 1 (EL1008)", 1)]
    [InlineData("Drive 5 (AX5206)", 5)]
    [InlineData("Module 12 (X)", 12)]
    [InlineData("Box (EL1008)", 0)]
    public void TerminalSlot_ExtractsOrdinal(string name, int expected)
        => Assert.Equal(expected, HardwareScan.TerminalSlot(name));

    [Theory]
    [InlineData("Device 1 (EtherCAT)", true)]
    [InlineData("Device 2 (EL6695)", true)]
    [InlineData("Device 3 (EK9300)", true)]
    [InlineData("Device 4 (NOV/DP-RAM)", false)]
    public void IsEtherCatMaster_MatchesKnownNames(string name, bool expected)
        => Assert.Equal(expected, HardwareScan.IsEtherCatMaster(name));

    [Fact]
    public void Build_ReadsMastersAndTerminals_SkippingNonMasters()
    {
        var session = BuildSession(out _);

        var topology = ProjectAuthor.ScanHardware(session);

        var segment = Assert.Single(topology.Segments);
        Assert.Equal("Device 1 (EtherCAT)", segment.MasterName);
        Assert.Equal(4, segment.Terminals.Count);
        Assert.Equal(1, segment.Terminals[0].Slot);
        Assert.Equal("EL1008", segment.Terminals[0].OrderNumber);
        Assert.Equal("EK1100", segment.Terminals[1].OrderNumber);
        Assert.NotEqual("", topology.ScanTimestamp);
    }

    [Fact]
    public void Build_NoSysManagers_Throws()
    {
        var session = new FakeSession();

        Assert.Throws<InvalidOperationException>(() => ProjectAuthor.ScanHardware(session));
    }

    [Fact]
    public void Catalogue_KnownTerminals_ResolveWithSuffixStripping()
    {
        Assert.Equal(8, HardwareCatalogue.Lookup("EL1008")!.Count);
        Assert.Equal(8, HardwareCatalogue.Lookup("EL1008-0000")!.Count);   // hyphen suffix
        Assert.Equal(8, HardwareCatalogue.Lookup("el1008 0000")!.Count);   // case + space suffix
        Assert.Empty(HardwareCatalogue.Lookup("EK1100")!);                 // coupler: known, no I/O
        Assert.Null(HardwareCatalogue.Lookup("EL9999"));                   // unknown
    }

    [Fact]
    public void GenerateGvl_DeclaresKnownChannels_AndCommentsUnknowns()
    {
        var session = BuildSession(out _);
        var topology = ProjectAuthor.ScanHardware(session);

        var (code, scaffolded, unknown) = HardwareScaffold.GenerateGvl(topology);

        Assert.Equal(2, scaffolded); // EL1008 + EL2008 (EK1100 has no I/O; EL9999 unknown)
        Assert.Equal(["EL9999"], unknown);
        Assert.StartsWith("{attribute 'qualified_only'}\nVAR_GLOBAL", code, StringComparison.Ordinal);
        Assert.Contains("Slot1_EL1008_Input_1 : BOOL;", code, StringComparison.Ordinal);
        Assert.Contains("Slot3_EL2008_Output_8 : BOOL;", code, StringComparison.Ordinal);
        Assert.Contains("Box 4 (EL9999) - unknown terminal", code, StringComparison.Ordinal);
        Assert.EndsWith("END_VAR", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ScaffoldHardwareCode_AddsGeneratedGvl_AndReportsCounts()
    {
        var session = BuildSession(out var pous);

        var result = ProjectAuthor.ScaffoldHardwareCode(session, "HardwareIO", "", "Plc");

        Assert.True(result.Success);
        Assert.Equal(2, result.Details["terminals_scaffolded"]);
        Assert.Equal(["EL9999"], Assert.IsAssignableFrom<IReadOnlyList<string>>(result.Details["unknown_terminals"]));

        var gvl = pous.FindDirect("HardwareIO");
        Assert.NotNull(gvl);
        Assert.Equal(TcKind.Gvl, gvl!.Kind);
        Assert.Contains("Slot1_EL1008_Input_1 : BOOL;", gvl.DeclarationText, StringComparison.Ordinal);
        Assert.True(session.SaveCount > 0);
    }

    /// <summary>A fake session with a PLC (POUs folder) plus a TIID I/O tree of one master and four terminals.</summary>
    private static FakeSession BuildSession(out FakeTreeItem pous)
    {
        var tipc = new FakeTreeItem("TIPC");
        var project = tipc.Add(new FakeTreeItem("Plc")).Add(new FakeTreeItem("Plc Project"));
        pous = project.Add(new FakeTreeItem("POUs"));

        var tiid = new FakeTreeItem("TIID");
        var master = tiid.Add(new FakeTreeItem("Device 1 (EtherCAT)"));
        master.Add(new FakeTreeItem("Box 1 (EL1008)"));   // 8 digital inputs
        master.Add(new FakeTreeItem("Term 2 (EK1100)"));  // coupler, no I/O
        master.Add(new FakeTreeItem("Box 3 (EL2008)"));   // 8 digital outputs
        master.Add(new FakeTreeItem("Box 4 (EL9999)"));   // unknown
        tiid.Add(new FakeTreeItem("Device 2 (NOV/DP-RAM)")); // not a master; skipped

        return new FakeSession(new FakeSysManager(tipc, tiid));
    }
}
