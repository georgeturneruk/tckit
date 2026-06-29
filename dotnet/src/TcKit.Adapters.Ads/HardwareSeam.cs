namespace TcKit.Adapters.Ads;

// Raw, pre-decode hardware reads from the source seam. The adapter turns these into the Core models
// via the pure HardwareDecode helpers, so the decoding (slave state names, master flags, axis state,
// power/battery health) is unit-tested against a fake source without a live TwinCAT system.

/// <summary>Identity of one EtherCAT master as enumerated from the target.</summary>
internal sealed record EcMasterIdentityRaw(string NetId, string Name);

/// <summary>Raw per-slave read: the state/link bytes plus identity and CRC counters, pre-decode.</summary>
internal sealed record EcSlaveRaw(
    int Address,
    ushort DeviceState,
    ushort LinkState,
    uint VendorId,
    uint ProductCode,
    uint Revision,
    uint Serial,
    uint CrcA,
    uint CrcB,
    uint CrcC,
    uint CrcD);

/// <summary>Raw EtherCAT status: the master device-state word plus the slave table.</summary>
internal sealed record EcStatusRaw(ushort MasterDevState, IReadOnlyList<EcSlaveRaw> Slaves);

/// <summary>Raw CPU read; <c>TemperatureCelsius</c> is null when the BIOS does not report it.</summary>
internal sealed record IpcCpuRaw(int? TemperatureCelsius, int UsagePercent, long FrequencyMhz);

/// <summary>Raw memory read in MB (TwinCAT router/program memory).</summary>
internal sealed record IpcMemoryRaw(long TotalMb, long FreeMb);

/// <summary>Raw fan read.</summary>
internal sealed record IpcFanRaw(int Rpm);

/// <summary>Raw NIC read.</summary>
internal sealed record IpcNicRaw(string Mac, string Ipv4);

/// <summary>Raw UPS read; the status bytes are decoded to power/battery health by the adapter.</summary>
internal sealed record IpcUpsRaw(int BatteryPercent, byte PowerStatus, byte BatteryStatus, long PowerFailCounter);

/// <summary>Raw IPC read: every discovered module, pre-decode.</summary>
internal sealed record IpcRaw(
    string? TwincatVersion,
    IpcCpuRaw? Cpu,
    IpcMemoryRaw? Memory,
    IReadOnlyList<IpcFanRaw> Fans,
    IReadOnlyList<IpcNicRaw> Nics,
    IpcUpsRaw? Ups);

/// <summary>Raw per-axis read; the state name is derived by the adapter from error code + velocity.</summary>
internal sealed record AxisRaw(
    uint Id, string Name, uint ErrorCode, uint DelayedErrorCode, double Position, double Velocity, double LagError);

/// <summary>
/// Source of raw hardware reads from a live TwinCAT system. The native implementation drives TwinSharp;
/// a fake serves canned reads so the decoding/mapping in <see cref="TwinSharpHardwareInspector"/> is
/// testable without a runtime.
/// </summary>
internal interface IHardwareSource
{
    IReadOnlyList<EcMasterIdentityRaw> ListMasters(string targetAmsId);

    EcStatusRaw ReadEtherCatStatus(string targetAmsId, string masterNetId);

    IpcRaw ReadIpc(string targetAmsId);

    /// <summary>Read all axes, or just <paramref name="axisId"/> when set. Empty list when none match.</summary>
    IReadOnlyList<AxisRaw> ReadAxes(string targetAmsId, uint? axisId);
}
