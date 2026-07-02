using TcKit.Core.Models;

namespace TcKit.Core.Ports;

/// <summary>
/// Read-only hardware diagnostics on a running TwinCAT system over ADS (no XAE). EtherCAT master/slave
/// status, IPC hardware (CPU / memory / fans / NICs / UPS), and NC axis state, each targeted by AMS
/// Net ID. The system must be reachable via ADS and in Config or Run mode.
/// </summary>
public interface IHardwareInspector
{
    /// <summary>Return every EtherCAT master on the target (usually exactly one). Empty when none is found.</summary>
    Task<IReadOnlyList<EtherCatMasterInfo>> ListEtherCatMastersAsync(
        string targetAmsId, CancellationToken cancellationToken);

    /// <summary>
    /// Read the full EtherCAT status (master flags + slave table) for one master. When
    /// <paramref name="masterNetId"/> is empty, the first master on the target is used.
    /// </summary>
    Task<EtherCatStatus> GetEtherCatStatusAsync(
        string targetAmsId, string masterNetId, CancellationToken cancellationToken);

    /// <summary>Read IPC hardware diagnostics (TwinCAT version, CPU, memory, fans, NICs, UPS).</summary>
    Task<IpcHardware> GetIpcHardwareAsync(string targetAmsId, CancellationToken cancellationToken);

    /// <summary>Enumerate all configured NC axes and their live state. Empty when no NC axes exist.</summary>
    Task<IReadOnlyList<AxisState>> ListAxesAsync(string targetAmsId, CancellationToken cancellationToken);

    /// <summary>Read the live state of a single NC axis by ID. Throws when the axis does not exist.</summary>
    Task<AxisState> GetAxisStateAsync(string targetAmsId, int axisId, CancellationToken cancellationToken);
}
