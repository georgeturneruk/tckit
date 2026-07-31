using TcKit.Adapters.Ads;

namespace TcKit.Tests;

/// <summary>
/// In-memory fake of the ADS runtime seam. The system reports whether the requested state was
/// reached; the PLC symbol source serves preset bool/int values and, when the suites-finished flag
/// is read true, fires a callback so a test can simulate the xUnit publisher writing its XML.
/// </summary>
internal sealed class FakeAdsSystem : IAdsSystem
{
    public bool Reachable { get; set; } = true;

    /// <summary>The state reported when the transition fails ("Config" simulates the licence case).</summary>
    public string FinalOnFailure { get; set; } = "Stop";

    public TcSystemState? Requested { get; private set; }

    public SystemStateResult SetState(TcSystemState target, int waitTimeoutMs)
    {
        Requested = target;
        var final = Reachable ? target.ToString() : FinalOnFailure;
        return new SystemStateResult(Reachable, target.ToString(), final, "Config", 10);
    }
}

internal sealed class FakePlcSymbols : IPlcSymbols
{
    private readonly Dictionary<string, bool> _bools;
    private readonly Dictionary<string, int> _ints;
    private bool _firedFinished;

    public FakePlcSymbols(Dictionary<string, bool>? bools = null, Dictionary<string, int>? ints = null)
    {
        _bools = bools ?? [];
        _ints = ints ?? [];
    }

    /// <summary>Fired once when AllTestSuitesFinished is first read as true (simulates the XML publisher).</summary>
    public Action? OnFinished { get; set; }

    public bool Disposed { get; private set; }

    public bool TryReadBool(string symbolPath, out bool value)
    {
        if (_bools.TryGetValue(symbolPath, out value))
        {
            if (value && symbolPath.Contains("AllTestSuitesFinished", StringComparison.Ordinal) && !_firedFinished)
            {
                _firedFinished = true;
                OnFinished?.Invoke();
            }

            return true;
        }

        value = false;
        return false;
    }

    public bool TryReadInt(string symbolPath, out int value) => _ints.TryGetValue(symbolPath, out value);

    public void Dispose() => Disposed = true;
}

internal sealed class FakeAdsFactory : IAdsFactory
{
    public FakeAdsFactory(FakePlcSymbols? plc = null) => Plc = plc ?? new FakePlcSymbols();

    public FakeAdsSystem System { get; } = new();
    public FakePlcSymbols Plc { get; }

    /// <summary>The licence diagnosis returned when a transition ends in Config; null = no finding.</summary>
    public string? LicenceDiagnosis { get; set; }

    public IAdsSystem OpenSystem(string netId) => System;

    public IPlcSymbols OpenPlc(string netId, int port) => Plc;

    public string? DiagnoseStuckInConfig(string netId) => LicenceDiagnosis;
}
