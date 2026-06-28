using TcKit.Core.Models;
using TcKit.Core.Ports;
using TwinCAT.Ads;

namespace TcKit.Adapters.Ads;

/// <summary>
/// ADS-backed <see cref="ISymbolReader"/>. Skeleton only: the dependency wiring is
/// proven here; the live read lands in the Phase 0 on-machine spike.
/// </summary>
public sealed class AdsSymbolReader : ISymbolReader
{
    public Task<SymbolValue> ReadAsync(string instancePath, CancellationToken cancellationToken)
    {
        // Touch the Beckhoff.TwinCAT.Ads assembly so linkage is proven at build time.
        _ = typeof(AdsClient);
        throw new NotImplementedException("ADS symbol read lands in the Phase 0 on-machine spike.");
    }
}
