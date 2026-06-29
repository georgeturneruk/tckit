using System.ComponentModel;
using ModelContextProtocol.Server;
using TcKit.Core.Models;
using TcKit.Core.Ports;
using TcKit.Core.Serialization;

namespace TcKit.Server.Tools;

/// <summary>
/// COM hardware-authoring tools: add an EtherCAT master, add couplers/terminals by order number, and
/// remove I/O items. These mutate the open TwinCAT project's I/O configuration (XAE must be open with a
/// solution loaded). The write counterpart to <see cref="HardwareScanTools"/>.
/// </summary>
[McpServerToolType]
public sealed class HardwareConfigTools(IHardwareConfigurer hardware)
{
    [McpServerTool(Name = "AddEtherCatMaster")]
    [Description("Add an EtherCAT master device to the open project's I/O Devices tree. deviceName is the "
        + "display name (default 'Device 1 (EtherCAT)'). Add couplers/terminals under it with "
        + "AddEtherCatBox.")]
    public Task<string> AddEtherCatMaster(
        string deviceName = "Device 1 (EtherCAT)", CancellationToken cancellationToken = default)
        => Run(() => hardware.AddEtherCatMasterAsync(deviceName, cancellationToken));

    [McpServerTool(Name = "AddEtherCatBox")]
    [Description("Add an EtherCAT box (coupler or terminal) by Beckhoff order number under a named parent. "
        + "E-bus terminals (EL...) nest under their coupler, so parentName is the coupler (e.g. 'Box 1 "
        + "(EK1100)'); EtherCAT-native slaves go directly under the master. orderNumber may be "
        + "revision-qualified (e.g. 'EL1008' or 'EK1100-0000-0017'). before optionally names the sibling "
        + "to insert before (empty appends).")]
    public Task<string> AddEtherCatBox(
        string parentName, string boxName, string orderNumber, string before = "",
        CancellationToken cancellationToken = default)
        => Run(() => hardware.AddEtherCatBoxAsync(parentName, boxName, orderNumber, before, cancellationToken));

    [McpServerTool(Name = "DeleteIoDevice")]
    [Description("Remove an I/O device or box from the project's I/O Devices tree by name (cascades its "
        + "children). name is the display name (e.g. 'Device 1 (EtherCAT)' or 'Box 1 (EK1100)').")]
    public Task<string> DeleteIoDevice(string name, CancellationToken cancellationToken = default)
        => Run(() => hardware.DeleteIoDeviceAsync(name, cancellationToken));

    private static async Task<string> Run(Func<Task<Result>> call)
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
