using TwinCAT.Ads;
using TwinSharp;
using TwinSharp.EtherCAT;
using TwinSharp.IPC;

namespace TcKit.Adapters.Ads;

/// <summary>
/// Native <see cref="IHardwareSource"/> over TwinSharp + Beckhoff.TwinCAT.Ads. Pure reads against a live
/// TwinCAT system; per-module / per-slave failures are tolerated (best-effort), mirroring the bridge
/// harness. All decoding into the Core models happens in <see cref="HardwareDecode"/>.
/// </summary>
internal sealed class TwinSharpHardwareSource : IHardwareSource
{
    public IReadOnlyList<EcMasterIdentityRaw> ListMasters(string targetAmsId)
    {
        var system = new TcSystem(AmsNetId.Parse(targetAmsId));
        return system.ListEtherCatMasters()
            .Select(m => new EcMasterIdentityRaw(m.AmsNetId.ToString(), m.Name))
            .ToList();
    }

    public EcStatusRaw ReadEtherCatStatus(string targetAmsId, string masterNetId)
    {
        var system = new TcSystem(AmsNetId.Parse(targetAmsId));
        var masters = system.ListEtherCatMasters();
        var master = masters.FirstOrDefault(m => m.AmsNetId.ToString() == masterNetId)
            ?? masters.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No EtherCAT master found on {targetAmsId}. Ensure TwinCAT is running and the AMS route is reachable.");

        var devState = master.DevState;

        var slaves = new List<EcSlaveRaw>();
        foreach (var address in master.GetAllSlaveAddr())
        {
            slaves.Add(ReadSlave(master, address));
        }

        return new EcStatusRaw(devState, slaves);
    }

    public IpcRaw ReadIpc(string targetAmsId)
    {
        using var ipc = new IPC(AmsNetId.Parse(targetAmsId));
        return new IpcRaw(
            TwincatVersion: TryRead(() =>
            {
                var tc = ipc.TwinCAT;
                return tc is null ? null : $"{tc.MajorVersion}.{tc.MinorVersion}.{tc.BuildNumber}";
            }),
            Cpu: TryRead(() =>
            {
                var cpu = ipc.Cpu;
                return cpu is null ? null : new IpcCpuRaw(cpu.TemperatureCelsius, cpu.UsagePercent, cpu.Frequency);
            }),
            Memory: TryRead(() =>
            {
                var mem = ipc.Memory;
                if (mem is null)
                {
                    return null;
                }

                const long bytesPerMb = 1024 * 1024;
                var used = (long)(mem.ProgramMemoryAllocated / bytesPerMb);
                var free = (long)(mem.ProgramMemoryAvailable / bytesPerMb);
                return new IpcMemoryRaw(used + free, free);
            }),
            Fans: TryReadList(() => ipc.Fans?.Select(f => new IpcFanRaw(f.FanSpeedRPM)).ToList() ?? []),
            Nics: TryReadList(() => ipc.NICs?.Select(n => new IpcNicRaw(n.MACAddress ?? "", n.IPv4Address ?? "")).ToList() ?? []),
            Ups: TryRead(() =>
            {
                var ups = ipc.UPS;
                return ups is null
                    ? null
                    : new IpcUpsRaw(ups.BatteryCapacityPercent, ups.PowerStatus, ups.BatteryStatus, ups.PowerFailCounter);
            }));
    }

    public IReadOnlyList<AxisRaw> ReadAxes(string targetAmsId, uint? axisId)
    {
        var net = AmsNetId.Parse(targetAmsId);
        TwinSharp.NC.NC nc;
        try
        {
            nc = new TwinSharp.NC.NC(net);
        }
#pragma warning disable CA1031 // No NC configured (or unreachable) reads as "no axes".
        catch (Exception)
        {
            return [];
        }
#pragma warning restore CA1031

        var result = new List<AxisRaw>();
        foreach (var axis in nc.Axes ?? [])
        {
            var raw = TryRead(() =>
            {
                var id = axis.Parameters.ID;
                if (axisId.HasValue && id != axisId.Value)
                {
                    return (AxisRaw?)null;
                }

                var state = axis.State;
                return new AxisRaw(
                    id,
                    axis.Parameters.Name ?? "",
                    state.ErrorCode,
                    state.DelayedErrorCode,
                    state.ActualPosition,
                    state.ActualVelocity,
                    state.LagErrorPosition);
            });

            if (raw is not null)
            {
                result.Add(raw);
            }
        }

        return result;
    }

    private static EcSlaveRaw ReadSlave(EtherCatMaster master, ushort address)
    {
        var slave = master.GetSlave(address);

        // State is read directly; identity and CRC counters are best-effort (an offline slave still
        // appears in the table with zeroed identity), matching Get-EtherCatStatus.ps1.
        var state = TryReadStruct(() => slave.State);
        var identity = TryReadStruct(() => slave.Identity);
        var crc = TryReadStruct(() => slave.CrcError);

        return new EcSlaveRaw(
            Address: address,
            DeviceState: (ushort)state.DeviceState,
            LinkState: (ushort)state.LinkState,
            VendorId: identity.VendorId,
            ProductCode: identity.ProductCode,
            Revision: identity.RevisionNo,
            Serial: identity.SerialNo,
            CrcA: crc.PortACount,
            CrcB: crc.PortBCount,
            CrcC: crc.PortCCount,
            CrcD: crc.PortDCount);
    }

    private static T? TryRead<T>(Func<T?> read)
        where T : class
    {
        try
        {
            return read();
        }
#pragma warning disable CA1031 // Best-effort module read: an absent / unsupported module maps to null.
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    private static IReadOnlyList<T> TryReadList<T>(Func<IReadOnlyList<T>> read)
    {
        try
        {
            return read();
        }
#pragma warning disable CA1031 // Best-effort module read: an absent module maps to an empty list.
        catch (Exception)
        {
            return [];
        }
#pragma warning restore CA1031
    }

    private static T TryReadStruct<T>(Func<T> read)
        where T : struct
    {
        try
        {
            return read();
        }
#pragma warning disable CA1031 // Best-effort per-slave read: an offline slave maps to a zeroed struct.
        catch (Exception)
        {
            return default;
        }
#pragma warning restore CA1031
    }
}
