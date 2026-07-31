using System.Diagnostics;
using TwinCAT.Ads;

namespace TcKit.Ads;

/// <summary>The TwinCAT system run-mode targets driven over ADS (system service, port 10000).</summary>
public enum TcTargetState
{
    Run,
    Config,
}

/// <summary>Outcome of a system state transition: whether it reached the target, and diagnostics.</summary>
public sealed record TcStateTransition(bool Reached, string Target, string Final, string Original, long LatencyMs);

/// <summary>
/// Runtime state operations against a target's system service (ADS port 10000): liveness probe,
/// state query, and the run/config transition with reconnect polling. A state transition restarts
/// TwinCAT, which drops every ADS connection on the way, so each poll opens a fresh connection;
/// "no answer" during the down window reads as "not yet", not as failure.
/// </summary>
public sealed class AdsRuntimeState
{
    /// <summary>The TwinCAT system service AMS port.</summary>
    public const int SystemServicePort = 10000;

    private const int DefaultTimeoutMs = 30000;

    private readonly ISystemService _service;

    public AdsRuntimeState(string netId)
        : this(new NativeSystemService(netId))
    {
    }

    internal AdsRuntimeState(ISystemService service) => _service = service;

    /// <summary>The target's system service answered a state read. Even a Config answer is alive.</summary>
    public bool IsAlive() => _service.TryReadState() is not null;

    /// <summary>Read the current system state; false when the target gave no answer.</summary>
    public bool TryReadState(out AdsState state)
    {
        var read = _service.TryReadState();
        state = read ?? AdsState.Invalid;
        return read is not null;
    }

    /// <summary>Restart the target into Run mode and wait until it is reached.</summary>
    public TcStateTransition RestartToRun(int waitTimeoutMs = DefaultTimeoutMs, int pollIntervalMs = 500)
        => SetState(TcTargetState.Run, waitTimeoutMs, pollIntervalMs);

    /// <summary>
    /// WriteControl the system toward <paramref name="target"/> (Reset -> Run, Reconfig -> Config,
    /// mirroring TcXaeMgmt's Restart-TwinCAT) and poll until reached or timeout.
    /// </summary>
    public TcStateTransition SetState(
        TcTargetState target, int waitTimeoutMs = DefaultTimeoutMs, int pollIntervalMs = 500)
    {
        var (command, expected) = target == TcTargetState.Run
            ? (AdsState.Reset, AdsState.Run)
            : (AdsState.Reconfig, AdsState.Config);

        var stopwatch = Stopwatch.StartNew();

        var original = _service.TryReadState() ?? AdsState.Invalid;
        _service.WriteControl(command);

        var reached = AdsState.Invalid;
        while (stopwatch.ElapsedMilliseconds < waitTimeoutMs)
        {
            Thread.Sleep(pollIntervalMs);
            var state = _service.TryReadState();
            if (state.HasValue)
            {
                reached = state.Value;
                if (reached == expected)
                {
                    break;
                }
            }
        }

        return new TcStateTransition(
            reached == expected, target.ToString(), reached.ToString(), original.ToString(),
            stopwatch.ElapsedMilliseconds);
    }
}

/// <summary>The system-service wire operations, seamed so the transition logic tests without a target.</summary>
internal interface ISystemService
{
    /// <summary>Read the system state; null when the target gave no answer (down, restarting, no route).</summary>
    AdsState? TryReadState();

    /// <summary>Send a WriteControl command. Throws when the target is unreachable.</summary>
    void WriteControl(AdsState command);
}

/// <summary>Native implementation: a fresh connection per call, because transitions restart the router.</summary>
internal sealed class NativeSystemService(string netId) : ISystemService
{
#pragma warning disable CA1031 // Best-effort state read on a restarting system: any ADS failure means "no answer".
    public AdsState? TryReadState()
    {
        try
        {
            using var client = new AdsClient();
            client.Connect(AmsNetId.Parse(netId), AdsRuntimeState.SystemServicePort);
            return client.ReadState().AdsState;
        }
        catch (Exception)
        {
            return null;
        }
    }
#pragma warning restore CA1031

    public void WriteControl(AdsState command)
    {
        using var client = new AdsClient();
        client.Connect(AmsNetId.Parse(netId), AdsRuntimeState.SystemServicePort);
        client.WriteControl(new StateInfo(command, 0));
    }
}
