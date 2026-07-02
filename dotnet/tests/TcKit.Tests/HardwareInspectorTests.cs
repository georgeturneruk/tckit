using TcKit.Adapters.Ads;

namespace TcKit.Tests;

/// <summary>
/// TwinSharpHardwareInspector orchestration against the fake source: master-net-id defaulting, the
/// EtherCAT master port stamp, axis enumeration vs. single-axis lookup, and the axis-not-found error.
/// </summary>
public sealed class HardwareInspectorTests
{
    private const string Target = "1.2.3.4.1.1";

    [Fact]
    public async Task ListEtherCatMasters_StampsThePort()
    {
        var source = new FakeHardwareSource();
        source.Masters.Add(new EcMasterIdentityRaw(Target, "Device 1 (EtherCAT)"));
        var inspector = new TwinSharpHardwareInspector(source);

        var masters = await inspector.ListEtherCatMastersAsync(Target, default);

        var master = Assert.Single(masters);
        Assert.Equal(65535, master.Port);
        Assert.Equal("Device 1 (EtherCAT)", master.Name);
    }

    [Fact]
    public async Task GetEtherCatStatus_DefaultsMasterNetIdToTarget()
    {
        var source = new FakeHardwareSource
        {
            Status = new EcStatusRaw(0x0001, [new EcSlaveRaw(1001, 0x08, 0, 1, 2, 3, 4, 0, 0, 0, 0)]),
        };
        var inspector = new TwinSharpHardwareInspector(source);

        var status = await inspector.GetEtherCatStatusAsync(Target, masterNetId: "", default);

        Assert.Equal(Target, source.RequestedMasterNetId);
        Assert.True(status.Master.LinkError);
        Assert.Equal("OP", Assert.Single(status.Slaves).State);
    }

    [Fact]
    public async Task GetEtherCatStatus_PassesExplicitMasterNetId()
    {
        var source = new FakeHardwareSource();
        var inspector = new TwinSharpHardwareInspector(source);

        await inspector.GetEtherCatStatusAsync(Target, "5.6.7.8.1.1", default);

        Assert.Equal("5.6.7.8.1.1", source.RequestedMasterNetId);
    }

    [Fact]
    public async Task ListAxes_ReadsAllAxes()
    {
        var source = new FakeHardwareSource();
        source.Axes.Add(new AxisRaw(1, "X", 0, 0, 10.0, 0.0, 0.0));
        source.Axes.Add(new AxisRaw(2, "Y", 0, 0, 0.0, 1.5, 0.0));
        var inspector = new TwinSharpHardwareInspector(source);

        var axes = await inspector.ListAxesAsync(Target, default);

        Assert.Null(source.RequestedAxisId);
        Assert.Equal(2, axes.Count);
        Assert.Equal("Standstill", axes[0].StateName);
        Assert.Equal("Moving", axes[1].StateName);
    }

    [Fact]
    public async Task GetAxisState_ReturnsTheRequestedAxis()
    {
        var source = new FakeHardwareSource();
        source.Axes.Add(new AxisRaw(1, "X", 0, 0, 10.0, 0.0, 0.0));
        source.Axes.Add(new AxisRaw(7, "C", 0x4550, 0, 0.0, 0.0, 5.0));
        var inspector = new TwinSharpHardwareInspector(source);

        var axis = await inspector.GetAxisStateAsync(Target, 7, default);

        Assert.Equal((uint?)7, source.RequestedAxisId);
        Assert.Equal("C", axis.Name);
        Assert.Equal("Error", axis.StateName);
    }

    [Fact]
    public async Task GetAxisState_MissingAxis_Throws()
    {
        var inspector = new TwinSharpHardwareInspector(new FakeHardwareSource());

        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.GetAxisStateAsync(Target, 99, default));
    }

    [Fact]
    public async Task EmptyTarget_Throws()
    {
        var inspector = new TwinSharpHardwareInspector(new FakeHardwareSource());

        await Assert.ThrowsAsync<ArgumentException>(() => inspector.ListEtherCatMastersAsync("", default));
    }
}
