using TcKit.Core.Models;

namespace TcKit.Core.Ports;

/// <summary>
/// Reads the configured hardware topology from the open TwinCAT project over the COM Automation
/// Interface (the TIID I/O-devices tree), and scaffolds I/O variable declarations from it. Requires
/// XAE open with a solution loaded (same constraint as the writer port); no physical bus scan, so no
/// I/O is interrupted. Distinct from <see cref="IHardwareInspector"/>, which reads a live runtime over
/// ADS.
/// </summary>
public interface IHardwareScanner
{
    /// <summary>Read the EtherCAT masters and their terminals from the target project's I/O tree.
    /// <paramref name="projectName"/> selects the TwinCAT project (required when the solution has >1).</summary>
    Task<HardwareTopology> ScanHardwareAsync(string? projectName, CancellationToken cancellationToken);

    /// <summary>
    /// Scan the topology of <paramref name="projectName"/> and generate a GVL of <c>VAR_GLOBAL</c>
    /// declarations for every terminal whose order number is in the bundled device catalogue, then add it
    /// to <paramref name="plcName"/>. <paramref name="projectName"/> is the TwinCAT project whose I/O is
    /// scanned (required when >1); <paramref name="plcName"/> is the PLC project the GVL is added to.
    /// </summary>
    Task<Result> ScaffoldHardwareCodeAsync(
        string gvlName, string? plcName, string parentFolder, string? projectName, CancellationToken cancellationToken);
}
