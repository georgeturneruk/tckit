using System.Diagnostics;
using System.Globalization;
using TwinCAT.Ads;

namespace TcKit.Adapters.Ads;

/// <summary>Native ADS implementation of the runtime seam over Beckhoff.TwinCAT.Ads.</summary>
internal sealed class AdsFactory : IAdsFactory
{
    public IAdsSystem OpenSystem(string netId) => new AdsSystemService(netId);

    public IPlcSymbols OpenPlc(string netId, int port) => new AdsPlcSymbols(netId, port);
}

/// <summary>
/// Drives the TwinCAT system run/config transition via WriteControl on the system service (port
/// 10000): Reset -> Run, Reconfig -> Config (mirrors TcXaeMgmt's Restart-TwinCAT). The system
/// restarts, so polling reconnects on each cycle until the target state is read back.
/// </summary>
internal sealed class AdsSystemService(string netId) : IAdsSystem
{
    private const int SystemServicePort = 10000;
    private const int PollIntervalMs = 500;

    public SystemStateResult SetState(TcSystemState target, int waitTimeoutMs)
    {
        var (command, expected) = target == TcSystemState.Run
            ? (AdsState.Reset, AdsState.Run)
            : (AdsState.Reconfig, AdsState.Config);

        var stopwatch = Stopwatch.StartNew();

        var original = TryReadState() ?? AdsState.Invalid;
        using (var client = new AdsClient())
        {
            client.Connect(AmsNetId.Parse(netId), SystemServicePort);
            client.WriteControl(new StateInfo(command, 0));
        }

        var reached = AdsState.Invalid;
        while (stopwatch.ElapsedMilliseconds < waitTimeoutMs)
        {
            Thread.Sleep(PollIntervalMs);
            var state = TryReadState();
            if (state.HasValue)
            {
                reached = state.Value;
                if (reached == expected)
                {
                    break;
                }
            }
        }

        return new SystemStateResult(
            reached == expected, target.ToString(), reached.ToString(), original.ToString(), stopwatch.ElapsedMilliseconds);
    }

#pragma warning disable CA1031 // Best-effort state read on a restarting system: any ADS failure means "not yet".
    private AdsState? TryReadState()
    {
        try
        {
            using var client = new AdsClient();
            client.Connect(AmsNetId.Parse(netId), SystemServicePort);
            return client.ReadState().AdsState;
        }
        catch (Exception)
        {
            return null;
        }
    }
#pragma warning restore CA1031
}

/// <summary>Symbolic reads on a PLC runtime port via variable handles. Best-effort: missing symbols return false.</summary>
internal sealed class AdsPlcSymbols : IPlcSymbols
{
    private readonly AdsClient _client;

    public AdsPlcSymbols(string netId, int port)
    {
        _client = new AdsClient();
        _client.Connect(AmsNetId.Parse(netId), port);
    }

    public bool TryReadBool(string symbolPath, out bool value)
    {
        value = false;
        if (TryReadAny(symbolPath, typeof(bool)) is bool b)
        {
            value = b;
            return true;
        }

        return false;
    }

    public bool TryReadInt(string symbolPath, out int value)
    {
        value = 0;
        var raw = TryReadAny(symbolPath, typeof(int));
        if (raw is null)
        {
            return false;
        }

        value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        return true;
    }

#pragma warning disable CA1031 // Best-effort symbolic read: a missing/unresolvable symbol maps to null.
    private object? TryReadAny(string symbolPath, Type type)
    {
        uint handle = 0;
        try
        {
            handle = _client.CreateVariableHandle(symbolPath);
            return _client.ReadAny(handle, type);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (handle != 0)
            {
                try
                {
                    _client.DeleteVariableHandle(handle);
                }
                catch (Exception)
                {
                    // Handle already gone; nothing to release.
                }
            }
        }
    }
#pragma warning restore CA1031

    public void Dispose() => _client.Dispose();
}
