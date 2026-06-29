using System.ComponentModel;
using ModelContextProtocol.Server;
using TcKit.Core.Ports;
using TcKit.Core.Serialization;

namespace TcKit.Server.Tools;

/// <summary>
/// COM hardware tools: read the configured EtherCAT topology from the open project, and scaffold a GVL
/// of I/O declarations from it. These require XAE open with a solution loaded (the writer constraint),
/// unlike the ADS hardware tools. No physical bus scan, so no I/O is interrupted.
/// </summary>
[McpServerToolType]
public sealed class HardwareScanTools(IHardwareScanner scanner)
{
    [McpServerTool(Name = "ScanHardware")]
    [Description("Read the hardware topology from the open TwinCAT project: every EtherCAT master with "
        + "its terminals (slot, full tree name, order number). Requires XAE open with a solution loaded. "
        + "Reads the configured topology without triggering a physical bus scan.")]
    public Task<string> ScanHardware(CancellationToken cancellationToken = default)
        => Run(async () => await scanner.ScanHardwareAsync(cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "ScaffoldHardwareCode")]
    [Description("Scaffold a GVL of VAR_GLOBAL I/O declarations from the connected hardware topology. "
        + "Variables are named Slot{N}_{OrderNumber}_{Channel}; terminals not in the bundled catalogue "
        + "get a comment placeholder. gvlName defaults to 'HardwareIO'. Run ScanHardware first to preview.")]
    public Task<string> ScaffoldHardwareCode(
        string gvlName = "HardwareIO", string plcName = "", string parentFolder = "",
        CancellationToken cancellationToken = default)
        => Run(async () => await scanner
            .ScaffoldHardwareCodeAsync(gvlName, Optional(plcName), parentFolder, cancellationToken)
            .ConfigureAwait(false));

    private static string? Optional(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static async Task<string> Run<T>(Func<Task<T>> call)
    {
        try
        {
            return TckitJson.Serialize(await call().ConfigureAwait(false));
        }
#pragma warning disable CA1031 // The tool boundary funnels every failure into the error contract.
        catch (Exception exc)
        {
            return TckitJson.Serialize(new { error = exc.Message });
        }
#pragma warning restore CA1031
    }
}
