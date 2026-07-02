using TcKit.Core.Models;

namespace TcKit.Core.Ports;

/// <summary>
/// Symbol-level ADS I/O on a running PLC runtime (port 851): read symbols by instance path, write
/// them best-effort, and invoke <c>{attribute 'TcRpcEnable'}</c> methods. Pure ADS, no XAE. Distinct
/// from <see cref="IRuntimeControl"/> (run/config transition) and the offline reader lane.
/// </summary>
public interface ISymbolIo
{
    /// <summary>
    /// Read PLC symbols by instance path. Best-effort: an unreadable path maps to <c>null</c> rather
    /// than failing the call. An empty path list returns an empty map.
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> ReadSymbolsAsync(
        string targetAmsId, IReadOnlyList<string> paths, CancellationToken cancellationToken);

    /// <summary>
    /// Write PLC symbols by instance path. Best-effort: per-symbol failures land in
    /// <c>Details["errors"]</c> keyed by path and do not abort the remaining writes; the written paths
    /// land in <c>Details["written"]</c>. <see cref="Result.Success"/> is true only when every write
    /// succeeded. The ADS layer resolves each symbol's declared type and coerces the supplied value.
    /// </summary>
    Task<Result> WriteSymbolsAsync(
        string targetAmsId, IReadOnlyDictionary<string, object?> writes, CancellationToken cancellationToken);

    /// <summary>
    /// Invoke a PLC method decorated with <c>{attribute 'TcRpcEnable'}</c> on an FB instance.
    /// Parameters are positional, matching the method's <c>VAR_INPUT</c> order.
    /// <c>Details["return_value"]</c> / <c>Details["return_type"]</c> are populated when the method
    /// returns a value.
    /// </summary>
    Task<Result> InvokeRpcAsync(
        string targetAmsId, string symbolPath, string methodName, IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken);
}
