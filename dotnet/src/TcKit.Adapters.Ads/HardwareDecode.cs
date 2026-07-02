using TcKit.Core.Models;

namespace TcKit.Adapters.Ads;

/// <summary>
/// Pure decoding of raw hardware reads into the Core diagnostic models: EtherCAT slave state names and
/// link health, master device-state flags, and the NC axis state name. No ADS; unit-tested directly.
/// Mirrors the bit decoding in Get-EtherCatStatus.ps1 / Get-NcAxes.ps1.
/// </summary>
internal static class HardwareDecode
{
    // EtherCAT slave device-state byte: lower nibble is the state, bit 0x10 is the error flag.
    private const int StateMask = 0x0F;
    private const int ErrorBit = 0x10;

    // EtherCAT link state (TwinSharp EcLinkState): 0 == Ok, any other value indicates a link problem.
    private const ushort LinkOkValue = 0;

    // Master device-state word flags (FB_EcGetMasterDevState).
    private const int MasterLinkError = 0x0001;
    private const int MasterIoLocked = 0x0002;
    private const int MasterWatchdog = 0x0020;
    private const int MasterDcOutOfSync = 0x1000;

    // NC: velocity magnitude above which an error-free axis counts as Moving (units/s).
    private const double MotionThreshold = 0.001;

    public static string SlaveStateName(ushort deviceState)
    {
        var name = (deviceState & StateMask) switch
        {
            0x01 => "INIT",
            0x02 => "PREOP",
            0x03 => "BOOTSTRAP",
            0x04 => "SAFEOP",
            0x08 => "OP",
            _ => "UNKNOWN",
        };

        return (deviceState & ErrorBit) != 0 ? name + "+ERROR" : name;
    }

    public static bool LinkOk(ushort linkState) => linkState == LinkOkValue;

    public static EtherCatMasterState MasterState(ushort devState) => new(
        StateFlags: devState,
        LinkError: (devState & MasterLinkError) != 0,
        IoLocked: (devState & MasterIoLocked) != 0,
        WatchdogTriggered: (devState & MasterWatchdog) != 0,
        DcOutOfSync: (devState & MasterDcOutOfSync) != 0);

    public static string AxisStateName(uint errorCode, double velocity)
    {
        if (errorCode != 0)
        {
            return "Error";
        }

        return Math.Abs(velocity) > MotionThreshold ? "Moving" : "Standstill";
    }

    public static EtherCatSlaveInfo ToSlaveInfo(EcSlaveRaw raw) => new(
        Address: raw.Address,
        Name: $"Slave {raw.Address}",
        VendorId: raw.VendorId,
        ProductCode: raw.ProductCode,
        Revision: raw.Revision,
        Serial: raw.Serial,
        State: SlaveStateName(raw.DeviceState),
        LinkOk: LinkOk(raw.LinkState),
        CrcErrorsA: raw.CrcA,
        CrcErrorsB: raw.CrcB,
        CrcErrorsC: raw.CrcC,
        CrcErrorsD: raw.CrcD);

    public static AxisState ToAxisState(AxisRaw raw) => new(
        Id: (int)raw.Id,
        Name: raw.Name,
        ErrorCode: raw.ErrorCode,
        DelayedErrorCode: raw.DelayedErrorCode,
        Position: raw.Position,
        Velocity: raw.Velocity,
        LagError: raw.LagError,
        StateName: AxisStateName(raw.ErrorCode, raw.Velocity));

    public static IpcHardware ToIpcHardware(IpcRaw raw) => new(
        TwincatVersion: raw.TwincatVersion,
        Cpu: raw.Cpu is null ? null : new IpcCpuInfo(raw.Cpu.TemperatureCelsius, raw.Cpu.UsagePercent, raw.Cpu.FrequencyMhz),
        Memory: raw.Memory is null ? null : new IpcMemoryInfo(raw.Memory.TotalMb, raw.Memory.FreeMb),
        Fans: raw.Fans.Select((f, i) => new IpcFanInfo(i, f.Rpm)).ToList(),
        Nics: raw.Nics.Select((n, i) => new IpcNicInfo(i, n.Mac, n.Ipv4)).ToList(),
        Ups: raw.Ups is null ? null : new IpcUpsInfo(
            BatteryCapacityPercent: raw.Ups.BatteryPercent,
            PowerOk: raw.Ups.PowerStatus == 0,
            BatteryOk: raw.Ups.BatteryStatus == 0,
            PowerFailCounter: raw.Ups.PowerFailCounter));
}
