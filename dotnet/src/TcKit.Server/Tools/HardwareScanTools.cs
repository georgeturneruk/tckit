using System.ComponentModel;
using ModelContextProtocol.Server;
using TcKit.Core.Ports;
using TcKit.Core.Security;
using TcKit.Core.Serialization;

namespace TcKit.Server.Tools;

/// <summary>
/// COM hardware tools: read the configured EtherCAT topology from the open project, and scaffold a GVL
/// of I/O declarations from it. These require XAE open with a solution loaded (the writer constraint),
/// unlike the ADS hardware tools. No physical bus scan, so no I/O is interrupted. ScanHardware is
/// read-class; ScaffoldHardwareCode authors a GVL, so it is write-class.
/// </summary>
[McpServerToolType]
public sealed class HardwareScanTools(IHardwareScanner scanner, IPermissionGate gate)
{
    [McpServerTool(Name = "ScanHardware")]
    [Description("Read the hardware topology from a TwinCAT project: every EtherCAT master with its "
        + "terminals (slot, full tree name, order number), plus the resolved project name. Requires XAE "
        + "open with a solution loaded. project is the TwinCAT project name to read and is REQUIRED when "
        + "the solution has more than one project. Reads the configured topology without a physical bus scan.")]
    public Task<string> ScanHardware(string project = "", CancellationToken cancellationToken = default)
        => Run(PermissionLevel.Read, async () => await scanner.ScanHardwareAsync(Optional(project), cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "ScaffoldHardwareCode")]
    [Description("Scaffold a GVL of VAR_GLOBAL I/O declarations from a TwinCAT project's hardware topology. "
        + "Variables are named Slot{N}_{OrderNumber}_{Channel}; terminals not in the bundled catalogue "
        + "get a comment placeholder. gvlName defaults to 'HardwareIO'. project is the TwinCAT project "
        + "whose I/O is scanned (REQUIRED when >1); plcName is the PLC project the GVL is added to. Run "
        + "ScanHardware first to preview.")]
    public Task<string> ScaffoldHardwareCode(
        string gvlName = "HardwareIO", string plcName = "", string parentFolder = "", string project = "",
        CancellationToken cancellationToken = default)
        => Run(PermissionLevel.Write, async () => await scanner
            .ScaffoldHardwareCodeAsync(gvlName, Optional(plcName), parentFolder, Optional(project), cancellationToken)
            .ConfigureAwait(false));

    private static string? Optional(string value) => string.IsNullOrEmpty(value) ? null : value;

    private async Task<string> Run<T>(PermissionLevel level, Func<Task<T>> call)
    {
        var denied = gate.Deny(level);
        if (denied is not null)
        {
            return TckitJson.Serialize(new { error = denied });
        }

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
