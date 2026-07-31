using System.Globalization;
using TcKit.Ads;
using TwinCAT.Ads;

namespace TcKit.Adapters.Ads;

/// <summary>Native ADS implementation of the runtime seam over the TcKit.Ads library.</summary>
internal sealed class AdsFactory : IAdsFactory
{
    public IAdsSystem OpenSystem(string netId) => new AdsSystemService(netId);

    public IPlcSymbols OpenPlc(string netId, int port) => new AdsPlcSymbols(netId, port);
}

/// <summary>The system run/config transition, delegated to <see cref="AdsRuntimeState"/>.</summary>
internal sealed class AdsSystemService(string netId) : IAdsSystem
{
    public SystemStateResult SetState(TcSystemState target, int waitTimeoutMs)
    {
        var transition = new AdsRuntimeState(netId).SetState(
            target == TcSystemState.Run ? TcTargetState.Run : TcTargetState.Config, waitTimeoutMs);
        return new SystemStateResult(
            transition.Reached, transition.Target, transition.Final, transition.Original, transition.LatencyMs);
    }
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
