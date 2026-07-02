namespace TcKit.Core.Models;

// Read-only hardware diagnostics read from a running TwinCAT system over ADS (TwinSharp +
// Beckhoff.TwinCAT.Ads). Snake_case JSON field names follow from the property names via TckitJson.

/// <summary>Identity of an EtherCAT master on the target system.</summary>
public sealed record EtherCatMasterInfo(string NetId, string Name, int Port);

/// <summary>Master-level diagnostic flags decoded from the master device-state word (FB_EcGetMasterDevState).</summary>
public sealed record EtherCatMasterState(
    int StateFlags, bool LinkError, bool IoLocked, bool WatchdogTriggered, bool DcOutOfSync);

/// <summary>Per-slave identity, state-machine state, link health, and per-port CRC error counters.</summary>
public sealed record EtherCatSlaveInfo(
    int Address,
    string Name,
    long VendorId,
    long ProductCode,
    long Revision,
    long Serial,
    string State,
    bool LinkOk,
    long CrcErrorsA,
    long CrcErrorsB,
    long CrcErrorsC,
    long CrcErrorsD);

/// <summary>Full EtherCAT status snapshot: master flags + the slave table.</summary>
public sealed record EtherCatStatus(EtherCatMasterState Master, IReadOnlyList<EtherCatSlaveInfo> Slaves);

/// <summary>CPU diagnostics. <c>TemperatureCelsius</c> is null when the BIOS does not report it.</summary>
public sealed record IpcCpuInfo(int? TemperatureCelsius, int UsagePercent, long FrequencyMhz);

/// <summary>
/// TwinCAT router memory in MB (program memory). Note: this is the TwinCAT runtime's router memory,
/// not the system RAM total/free the legacy MDP read reported.
/// </summary>
public sealed record IpcMemoryInfo(long ProgramTotalMb, long ProgramFreeMb);

/// <summary>Fan speed in RPM.</summary>
public sealed record IpcFanInfo(int Index, int SpeedRpm);

/// <summary>Network adapter MAC and IPv4 address.</summary>
public sealed record IpcNicInfo(int Index, string Mac, string Ipv4);

/// <summary>UPS status: battery capacity, power/battery health, and the power-fail counter.</summary>
public sealed record IpcUpsInfo(int BatteryCapacityPercent, bool PowerOk, bool BatteryOk, long PowerFailCounter);

/// <summary>Full IPC hardware snapshot. Modules not present on the system are null or empty.</summary>
public sealed record IpcHardware(
    string? TwincatVersion,
    IpcCpuInfo? Cpu,
    IpcMemoryInfo? Memory,
    IReadOnlyList<IpcFanInfo> Fans,
    IReadOnlyList<IpcNicInfo> Nics,
    IpcUpsInfo? Ups);

/// <summary>Live state of one TwinCAT NC axis. <c>StateName</c> is Standstill / Moving / Error / Unknown.</summary>
public sealed record AxisState(
    int Id,
    string Name,
    long ErrorCode,
    long DelayedErrorCode,
    double Position,
    double Velocity,
    double LagError,
    string StateName);
