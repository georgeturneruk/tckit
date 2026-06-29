using TcKit.Adapters.Ads;

namespace TcKit.Tests;

/// <summary>
/// In-memory fake of the symbol-I/O seam. Reads serve preset values (a missing path throws, so the
/// orchestration records null); writes land in a captured map (paths in <see cref="FailWrites"/> throw);
/// RPC returns a preset outcome per method.
/// </summary>
internal sealed class FakeSymbolSession : ISymbolSession
{
    public Dictionary<string, string> Readable { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?> Written { get; } = new(StringComparer.Ordinal);
    public HashSet<string> FailWrites { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, RpcOutcome> RpcReturns { get; } = new(StringComparer.Ordinal);
    public List<object?[]> RpcCalls { get; } = [];
    public bool Disposed { get; private set; }

    public string ReadValue(string path)
        => Readable.TryGetValue(path, out var v) ? v : throw new InvalidOperationException($"symbol '{path}' not found");

    public void WriteValue(string path, object? value)
    {
        if (FailWrites.Contains(path))
        {
            throw new InvalidOperationException($"write to '{path}' rejected");
        }

        Written[path] = value;
    }

    public RpcOutcome InvokeRpc(string symbolPath, string methodName, object?[] parameters)
    {
        RpcCalls.Add(parameters);
        return RpcReturns.TryGetValue(methodName, out var outcome) ? outcome : new RpcOutcome(false, null, null);
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeSymbolSessionFactory : ISymbolSessionFactory
{
    public FakeSymbolSessionFactory(FakeSymbolSession? session = null) => Session = session ?? new FakeSymbolSession();

    public FakeSymbolSession Session { get; }

    public string? OpenedNetId { get; private set; }

    public int OpenedPort { get; private set; }

    public ISymbolSession Open(string netId, int port)
    {
        OpenedNetId = netId;
        OpenedPort = port;
        return Session;
    }
}
