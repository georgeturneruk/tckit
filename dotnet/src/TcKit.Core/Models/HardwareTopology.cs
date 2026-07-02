namespace TcKit.Core.Models;

// Hardware topology read from the open TwinCAT project over the COM Automation Interface (the TIID
// I/O-devices tree). Distinct from the ADS hardware diagnostics in HardwareDiagnostics.cs: this is the
// configured topology, read without any bus traffic.

/// <summary>One EtherCAT terminal or coupler in the bus topology.</summary>
public sealed record TerminalInfo(int Slot, string Name, string OrderNumber);

/// <summary>One EtherCAT master and the terminals configured under it.</summary>
public sealed record EtherCatSegment(string MasterName, IReadOnlyList<TerminalInfo> Terminals);

/// <summary>The configured hardware topology of the scanned TwinCAT project (one segment per master).
/// <c>Project</c> is the resolved TwinCAT project the topology was read from, so a multi-project scan is
/// unambiguous about which system it describes.</summary>
public sealed record HardwareTopology(
    IReadOnlyList<EtherCatSegment> Segments, string ScanTimestamp, string Project = "");
