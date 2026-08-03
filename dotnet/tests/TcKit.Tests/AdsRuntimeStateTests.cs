using TcKit.Ads;
using TwinCAT.Ads;

namespace TcKit.Tests;

/// <summary>
/// TcKit.Ads runtime state transitions against the fake system service: command mapping, reconnect
/// polling through no-answer windows, timeout, and the liveness probe.
/// </summary>
public sealed class AdsRuntimeStateTests
{
    /// <summary>Fake system service: scripted state answers, recorded WriteControl commands.</summary>
    private sealed class FakeSystemService : ISystemService
    {
        /// <summary>Answers returned by successive polls after the scripted queue drains.</summary>
        public AdsState? Current { get; set; } = AdsState.Config;

        /// <summary>Optional per-poll script; each read dequeues one answer (null = no answer).</summary>
        public Queue<AdsState?> Scripted { get; } = new();

        public List<AdsState> Commands { get; } = [];

        public AdsState? TryReadState() => Scripted.Count > 0 ? Scripted.Dequeue() : Current;

        public void WriteControl(AdsState command) => Commands.Add(command);
    }

    [Fact]
    public void RestartToRun_SendsResetAndReachesRun()
    {
        var service = new FakeSystemService { Current = AdsState.Run };
        service.Scripted.Enqueue(AdsState.Config); // the pre-command original read

        var result = new AdsRuntimeState(service).RestartToRun(waitTimeoutMs: 1000, pollIntervalMs: 1);

        Assert.True(result.Reached);
        Assert.Equal(AdsState.Reset, Assert.Single(service.Commands));
        Assert.Equal("Config", result.Original);
        Assert.Equal("Run", result.Final);
    }

    [Fact]
    public void SetState_Config_SendsReconfig()
    {
        var service = new FakeSystemService { Current = AdsState.Config };

        var result = new AdsRuntimeState(service).SetState(
            TcTargetState.Config, waitTimeoutMs: 1000, pollIntervalMs: 1);

        Assert.True(result.Reached);
        Assert.Equal(AdsState.Reconfig, Assert.Single(service.Commands));
    }

    [Fact]
    public void SetState_ToleratesNoAnswerDuringRestartWindow()
    {
        var service = new FakeSystemService { Current = AdsState.Run };
        service.Scripted.Enqueue(AdsState.Run);  // original read
        service.Scripted.Enqueue(null);          // router down
        service.Scripted.Enqueue(null);          // still down
        service.Scripted.Enqueue(AdsState.Config); // back, not there yet

        var result = new AdsRuntimeState(service).RestartToRun(waitTimeoutMs: 1000, pollIntervalMs: 1);

        Assert.True(result.Reached);
        Assert.Equal("Run", result.Final);
    }

    [Fact]
    public void SetState_TimesOut_WhenTargetNeverReached()
    {
        var service = new FakeSystemService { Current = AdsState.Config };

        var result = new AdsRuntimeState(service).RestartToRun(waitTimeoutMs: 20, pollIntervalMs: 1);

        Assert.False(result.Reached);
        Assert.Equal("Config", result.Final);
    }

    [Fact]
    public void IsAlive_ReflectsAnswer()
    {
        var alive = new FakeSystemService { Current = AdsState.Config };
        var dead = new FakeSystemService { Current = null };

        Assert.True(new AdsRuntimeState(alive).IsAlive());
        Assert.False(new AdsRuntimeState(dead).IsAlive());
    }

    [Fact]
    public void TryReadState_ReturnsStateOrInvalid()
    {
        var service = new FakeSystemService { Current = AdsState.Run };

        Assert.True(new AdsRuntimeState(service).TryReadState(out var state));
        Assert.Equal(AdsState.Run, state);

        service.Current = null;
        Assert.False(new AdsRuntimeState(service).TryReadState(out var none));
        Assert.Equal(AdsState.Invalid, none);
    }
}
