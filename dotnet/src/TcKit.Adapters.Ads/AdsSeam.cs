namespace TcKit.Adapters.Ads;

/// <summary>The TwinCAT system run-mode targets we drive over ADS (system service, port 10000).</summary>
internal enum TcSystemState
{
    Run,
    Config,
}

/// <summary>Outcome of a system state transition: whether it reached the target, and diagnostics.</summary>
internal sealed record SystemStateResult(bool Reached, string Target, string Final, string Original, long LatencyMs);

/// <summary>The TwinCAT system service (ADS port 10000): drive the run/config transition.</summary>
internal interface IAdsSystem
{
    /// <summary>WriteControl the system toward <paramref name="target"/> and poll until reached or timeout.</summary>
    SystemStateResult SetState(TcSystemState target, int waitTimeoutMs);
}

/// <summary>A connection to a PLC runtime port (851) for symbolic reads. Best-effort: a missing symbol returns false.</summary>
internal interface IPlcSymbols : IDisposable
{
    bool TryReadBool(string symbolPath, out bool value);

    bool TryReadInt(string symbolPath, out int value);
}

/// <summary>Opens ADS connections. The seam that lets the runtime/test logic run against a fake in CI.</summary>
internal interface IAdsFactory
{
    IAdsSystem OpenSystem(string netId);

    IPlcSymbols OpenPlc(string netId, int port);
}
