using TcKit.Core.Models;

namespace TcKit.Core.Ports;

/// <summary>
/// Authors the I/O configuration of a TwinCAT project over the COM Automation Interface: add an EtherCAT
/// master device, add couplers/terminals (boxes) by order number, and remove I/O items. Requires XAE open
/// with a solution loaded (the writer constraint). Every verb targets an explicit TwinCAT
/// <c>projectName</c>; in a multi-project solution an empty name is refused rather than guessing. Each
/// write persists that project's <c>.tsproj</c> to disk immediately. The write counterpart to
/// <see cref="IHardwareScanner"/>.
/// </summary>
public interface IHardwareConfigurer
{
    /// <summary>Add an EtherCAT master device under the target project's I/O Devices (TIID) tree.
    /// <paramref name="projectName"/> selects the TwinCAT project (required when the solution has >1).</summary>
    Task<Result> AddEtherCatMasterAsync(string deviceName, string? projectName, CancellationToken cancellationToken);

    /// <summary>
    /// Add an EtherCAT box (coupler or terminal) under a named parent device or box. <c>parentName</c>
    /// is the device/box to nest under (E-bus EL terminals nest under their coupler; EtherCAT-native
    /// slaves sit directly under the master); <c>boxName</c> is the display name; <c>orderNumber</c> is
    /// the Beckhoff order number, optionally revision-qualified (e.g. "EL1008" or "EK1100-0000-0017");
    /// <c>before</c> optionally names the sibling to insert before (empty appends at the end);
    /// <c>projectName</c> selects the TwinCAT project (required when the solution has >1).
    /// </summary>
    Task<Result> AddEtherCatBoxAsync(
        string parentName, string boxName, string orderNumber, string before, string? projectName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Remove an I/O device or box from the target project's TIID tree (cascades its children).
    /// <paramref name="target"/> is a display name (must be unique in the project) or an exact
    /// <c>^</c>-delimited tree path. Destructive: <paramref name="confirmed"/> must be true; an
    /// unconfirmed call returns a preview (resolved path + cascade) instead of deleting.
    /// </summary>
    Task<Result> DeleteIoDeviceAsync(
        string target, string? projectName, bool confirmed, CancellationToken cancellationToken);
}
