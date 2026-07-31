using TcKit.Core.Models;
using TcKit.Core.Ports;

namespace TcKit.Adapters.Ads;

/// <summary>
/// ADS <see cref="ISymbolIo"/>: symbol read/write and RPC invocation on a running PLC runtime. Thin
/// shell over <see cref="SymbolOperations"/> (seam-driven, unit-tested against a fake); failures map
/// to the Result error contract. The live ADS specifics live in <see cref="LiveSymbolSession"/>.
/// </summary>
public sealed class AdsSymbolIo : ISymbolIo
{
    private readonly ISymbolSessionFactory _factory;

    public AdsSymbolIo()
        : this(new AdsSymbolSessionFactory())
    {
    }

    internal AdsSymbolIo(ISymbolSessionFactory factory) => _factory = factory;

    public Task<IReadOnlyDictionary<string, string?>> ReadSymbolsAsync(
        string targetAmsId, IReadOnlyList<string> paths, CancellationToken cancellationToken)
        => Task.Run(() => SymbolOperations.ReadSymbols(_factory, targetAmsId, paths), cancellationToken);

    public Task<Result> WriteSymbolsAsync(
        string targetAmsId, IReadOnlyDictionary<string, object?> writes, CancellationToken cancellationToken)
        => Task.Run(() => Guarded(() => SymbolOperations.WriteSymbols(_factory, targetAmsId, writes)), cancellationToken);

    public Task<Result> InvokeRpcAsync(
        string targetAmsId, string symbolPath, string methodName, IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken)
        => Task.Run(
            () => Guarded(() => SymbolOperations.InvokeRpc(_factory, targetAmsId, symbolPath, methodName, parameters)),
            cancellationToken);

    private static Result Guarded(Func<Result> call)
    {
        try
        {
            return call();
        }
#pragma warning disable CA1031 // The adapter boundary funnels every failure into the Result error contract.
        catch (Exception exc)
        {
            return Result.Fail(exc.Message);
        }
#pragma warning restore CA1031
    }
}

/// <summary>Native session factory over the TcKit.Ads library.</summary>
internal sealed class AdsSymbolSessionFactory : ISymbolSessionFactory
{
    public ISymbolSession Open(string netId, int port) => new LiveSymbolSession(netId, port);
}

/// <summary>
/// A live TcKit.Ads symbol session on a PLC runtime port: rendered reads via the symbolic lane,
/// enum-aware writes, and positional {attribute 'TcRpcEnable'} invocation.
/// </summary>
internal sealed class LiveSymbolSession(string netId, int port) : ISymbolSession
{
    private readonly TcKit.Ads.AdsSymbolSession _session = new(netId, port);

    public string ReadValue(string path) => _session.ReadRendered(path);

    public void WriteValue(string path, object? value) => _session.Write(path, value);

    public RpcOutcome InvokeRpc(string symbolPath, string methodName, object?[] parameters)
    {
        var result = _session.InvokeRpc(symbolPath, methodName, parameters);
        return new RpcOutcome(result.HasReturn, result.Value, result.TypeName);
    }

    public void Dispose() => _session.Dispose();
}
