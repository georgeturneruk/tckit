using TcKit.Adapters.Ads;

namespace TcKit.Tests;

/// <summary>
/// Pure decoding of raw hardware reads: EtherCAT slave state names (incl. the error bit), link health,
/// master device-state flags, and the NC axis state-name derivation.
/// </summary>
public sealed class HardwareDecodeTests
{
    [Theory]
    [InlineData(0x01, "INIT")]
    [InlineData(0x02, "PREOP")]
    [InlineData(0x03, "BOOTSTRAP")]
    [InlineData(0x04, "SAFEOP")]
    [InlineData(0x08, "OP")]
    [InlineData(0x00, "UNKNOWN")]
    [InlineData(0x18, "OP+ERROR")]
    [InlineData(0x12, "PREOP+ERROR")]
    public void SlaveStateName_DecodesStateNibbleAndErrorBit(int deviceState, string expected)
        => Assert.Equal(expected, HardwareDecode.SlaveStateName((ushort)deviceState));

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(16, false)]
    public void LinkOk_IsTrueOnlyForOk(int linkState, bool expected)
        => Assert.Equal(expected, HardwareDecode.LinkOk((ushort)linkState));

    [Fact]
    public void MasterState_DecodesEachFlag()
    {
        var state = HardwareDecode.MasterState(0x1023);

        Assert.Equal(0x1023, state.StateFlags);
        Assert.True(state.LinkError);       // 0x0001
        Assert.True(state.IoLocked);        // 0x0002
        Assert.True(state.WatchdogTriggered); // 0x0020
        Assert.True(state.DcOutOfSync);     // 0x1000
    }

    [Fact]
    public void MasterState_AllClear()
    {
        var state = HardwareDecode.MasterState(0);

        Assert.False(state.LinkError);
        Assert.False(state.IoLocked);
        Assert.False(state.WatchdogTriggered);
        Assert.False(state.DcOutOfSync);
    }

    [Theory]
    [InlineData(0u, 0.0, "Standstill")]
    [InlineData(0u, 0.5, "Moving")]
    [InlineData(0u, -0.5, "Moving")]
    [InlineData(0u, 0.0001, "Standstill")] // below the motion threshold
    [InlineData(0x4550u, 0.0, "Error")]
    [InlineData(0x4550u, 1.0, "Error")] // error trumps motion
    public void AxisStateName_DerivesFromErrorAndVelocity(uint errorCode, double velocity, string expected)
        => Assert.Equal(expected, HardwareDecode.AxisStateName(errorCode, velocity));

    [Fact]
    public void ToSlaveInfo_MapsIdentityAndCrc()
    {
        var raw = new EcSlaveRaw(1001, 0x08, 0, 2, 0x44, 24, 999, 1, 2, 3, 4);

        var info = HardwareDecode.ToSlaveInfo(raw);

        Assert.Equal(1001, info.Address);
        Assert.Equal("Slave 1001", info.Name);
        Assert.Equal("OP", info.State);
        Assert.True(info.LinkOk);
        Assert.Equal(2, info.VendorId);
        Assert.Equal(0x44, info.ProductCode);
        Assert.Equal(4, info.CrcErrorsD);
    }

    [Fact]
    public void ToIpcHardware_DerivesPowerAndBatteryHealth_AndIndexes()
    {
        var raw = new IpcRaw(
            "3.1.4026",
            new IpcCpuRaw(45, 12, 2400),
            new IpcMemoryRaw(2048, 1500),
            [new IpcFanRaw(3000), new IpcFanRaw(3200)],
            [new IpcNicRaw("00:11:22:33:44:55", "192.168.1.10")],
            new IpcUpsRaw(95, PowerStatus: 0, BatteryStatus: 1, PowerFailCounter: 2));

        var ipc = HardwareDecode.ToIpcHardware(raw);

        Assert.Equal("3.1.4026", ipc.TwincatVersion);
        Assert.Equal(45, ipc.Cpu!.TemperatureCelsius);
        Assert.Equal(2048, ipc.Memory!.ProgramTotalMb);
        Assert.Equal(1, ipc.Fans[1].Index);
        Assert.Equal(3200, ipc.Fans[1].SpeedRpm);
        Assert.Equal("192.168.1.10", ipc.Nics[0].Ipv4);
        Assert.True(ipc.Ups!.PowerOk);   // PowerStatus == 0
        Assert.False(ipc.Ups.BatteryOk); // BatteryStatus != 0
    }

    [Fact]
    public void ToIpcHardware_NullModules_StayNull()
    {
        var ipc = HardwareDecode.ToIpcHardware(new IpcRaw(null, null, null, [], [], null));

        Assert.Null(ipc.Cpu);
        Assert.Null(ipc.Memory);
        Assert.Null(ipc.Ups);
        Assert.Empty(ipc.Fans);
    }
}
