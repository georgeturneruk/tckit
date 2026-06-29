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
    /// <summary>Read the EtherCAT masters and their terminals from the open project's I/O tree.</summary>
    Task<HardwareTopology> ScanHardwareAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Scan the topology and generate a GVL of <c>VAR_GLOBAL</c> declarations for every terminal whose
    /// order number is in the bundled device catalogue, then add it to <paramref name="plcName"/>.
    /// </summary>
    Task<Result> ScaffoldHardwareCodeAsync(
        string gvlName, string? plcName, string parentFolder, CancellationToken cancellationToken);
}
