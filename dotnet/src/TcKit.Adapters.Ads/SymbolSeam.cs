namespace TcKit.Adapters.Ads;

/// <summary>Outcome of an RPC invocation: whether the method returned a value, and its rendering.</summary>
internal sealed record RpcOutcome(bool HasReturn, string? Value, string? TypeName);

/// <summary>
/// A connection to a PLC runtime port for symbol-level I/O. Each operation throws on failure so the
/// best-effort orchestration can record the error per path. The seam lets <see cref="SymbolOperations"/>
/// run against an in-memory fake without a live runtime.
/// </summary>
internal interface ISymbolSession : IDisposable
{
    /// <summary>Read a symbol by instance path, rendered as an invariant string. Throws on failure.</summary>
    string ReadValue(string path);

    /// <summary>Write a value to a symbol by instance path; the ADS layer coerces to the declared type. Throws on failure.</summary>
    void WriteValue(string path, object? value);

    /// <summary>Invoke a TcRpcEnable method positionally and render any return value. Throws on failure.</summary>
    RpcOutcome InvokeRpc(string symbolPath, string methodName, object?[] parameters);
}

/// <summary>Opens symbol sessions on a runtime port. The seam injected into the symbol-I/O adapter.</summary>
internal interface ISymbolSessionFactory
{
    ISymbolSession Open(string netId, int port);
}
