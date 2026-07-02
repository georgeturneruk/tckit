using System.ComponentModel;
using ModelContextProtocol.Server;
using TcKit.Core.Models;
using TcKit.Core.Ports;
using TcKit.Core.Security;
using TcKit.Core.Serialization;

namespace TcKit.Server.Tools;

/// <summary>
/// COM hardware-authoring tools: add an EtherCAT master, add couplers/terminals by order number, and
/// remove I/O items. These mutate the open TwinCAT project's I/O configuration (XAE must be open with a
/// solution loaded). The write counterpart to <see cref="HardwareScanTools"/>; all write-class.
/// </summary>
[McpServerToolType]
public sealed class HardwareConfigTools(IHardwareConfigurer hardware, IPermissionGate gate)
{
    [McpServerTool(Name = "AddEtherCatMaster")]
    [Description("Add an EtherCAT master device to a TwinCAT project's I/O Devices tree. deviceName is the "
        + "display name (default 'Device 1 (EtherCAT)'). project is the TwinCAT project name to target and "
        + "is REQUIRED when the solution has more than one project (otherwise the call is refused, listing "
        + "the available projects) — do not let I/O land in the wrong project. The change is saved to that "
        + "project's .tsproj immediately. Add couplers/terminals under the master with AddEtherCatBox.")]
    public Task<string> AddEtherCatMaster(
        string deviceName = "Device 1 (EtherCAT)", string project = "",
        CancellationToken cancellationToken = default)
        => Run(() => hardware.AddEtherCatMasterAsync(deviceName, Optional(project), cancellationToken));

    [McpServerTool(Name = "AddEtherCatBox")]
    [Description("Add an EtherCAT box (coupler or terminal) by Beckhoff order number under a named parent. "
        + "E-bus terminals (EL...) nest under their coupler, so parentName is the coupler (e.g. 'Box 1 "
        + "(EK1100)'); EtherCAT-native slaves go directly under the master. orderNumber may be "
        + "revision-qualified (e.g. 'EL1008' or 'EK1100-0000-0017'). before optionally names the sibling "
        + "to insert before (empty appends). project is the TwinCAT project name to target and is REQUIRED "
        + "when the solution has more than one project. Saved to the project's .tsproj immediately.")]
    public Task<string> AddEtherCatBox(
        string parentName, string boxName, string orderNumber, string before = "", string project = "",
        CancellationToken cancellationToken = default)
        => Run(() => hardware.AddEtherCatBoxAsync(
            parentName, boxName, orderNumber, before, Optional(project), cancellationToken));

    [McpServerTool(Name = "DeleteIoDevice")]
    [Description("Remove an I/O device or box from a TwinCAT project's I/O Devices tree (cascades its "
        + "children). target is a display name (must be UNIQUE in the project — an ambiguous name is "
        + "refused, listing the candidate paths) or an exact '^'-delimited tree path. project is the "
        + "TwinCAT project name, REQUIRED when the solution has more than one. WARNING: destructive; "
        + "requires confirmed=true. The first call with confirmed=false returns a preview (the resolved "
        + "path and the child items that will cascade) and deletes nothing.")]
    public Task<string> DeleteIoDevice(
        string target, string project = "", bool confirmed = false, CancellationToken cancellationToken = default)
        => Run(() => hardware.DeleteIoDeviceAsync(target, Optional(project), confirmed, cancellationToken));

    private static string? Optional(string value) => string.IsNullOrEmpty(value) ? null : value;

    // Every verb here is write-class (mutates the project I/O on disk, not a live target).
    private async Task<string> Run(Func<Task<Result>> call)
    {
        var denied = gate.Deny(PermissionLevel.Write);
        if (denied is not null)
        {
            return TckitJson.Serialize(Result.Fail(denied));
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
