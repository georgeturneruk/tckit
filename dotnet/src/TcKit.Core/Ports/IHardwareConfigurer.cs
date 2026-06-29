using TcKit.Core.Models;

namespace TcKit.Core.Ports;

/// <summary>
/// Authors the I/O configuration of the open TwinCAT project over the COM Automation Interface: add an
/// EtherCAT master device, add couplers/terminals (boxes) by order number, and remove I/O items.
/// Requires XAE open with a solution loaded (the writer constraint). The write counterpart to
/// <see cref="IHardwareScanner"/>.
/// </summary>
public interface IHardwareConfigurer
{
    /// <summary>Add an EtherCAT master device under the project's I/O Devices (TIID) tree.</summary>
    Task<Result> AddEtherCatMasterAsync(string deviceName, CancellationToken cancellationToken);

    /// <summary>
    /// Add an EtherCAT box (coupler or terminal) under a named parent device or box. <c>parentName</c>
    /// is the device/box to nest under (E-bus EL terminals nest under their coupler; EtherCAT-native
    /// slaves sit directly under the master); <c>boxName</c> is the display name; <c>orderNumber</c> is
    /// the Beckhoff order number, optionally revision-qualified (e.g. "EL1008" or "EK1100-0000-0017");
    /// <c>before</c> optionally names the sibling to insert before (empty appends at the end).
    /// </summary>
    Task<Result> AddEtherCatBoxAsync(
        string parentName, string boxName, string orderNumber, string before, CancellationToken cancellationToken);

    /// <summary>Remove an I/O device or box from the TIID tree by name (cascades its children).</summary>
    Task<Result> DeleteIoDeviceAsync(string name, CancellationToken cancellationToken);
}
