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

/// <summary>
/// Symbolic reads on a PLC runtime port, delegated to the TcKit.Ads session (cached handles with
/// the stale-handle policy). Best-effort: missing symbols return false.
/// </summary>
internal sealed class AdsPlcSymbols(string netId, int port) : IPlcSymbols
{
    private readonly TcKit.Ads.AdsSymbolSession _session = new(netId, port);

    public bool TryReadBool(string symbolPath, out bool value) => _session.TryRead(symbolPath, out value);

    public bool TryReadInt(string symbolPath, out int value)
    {
        try
        {
            value = (int)_session.ReadInteger(symbolPath);
            return true;
        }
        catch (TcSymbolException)
        {
            value = 0;
            return false;
        }
    }

    public void Dispose() => _session.Dispose();
}
