using System.Globalization;

namespace TcKit.Ads;

/// <summary>Thrown when a symbol operation fails after the stale-handle recovery attempt.</summary>
public sealed class TcSymbolException : Exception
{
    public TcSymbolException()
    {
    }

    public TcSymbolException(string message)
        : base(message)
    {
    }

    public TcSymbolException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public TcSymbolException(string symbolPath, string message, Exception? innerException)
        : base($"{symbolPath}: {message}", innerException) => SymbolPath = symbolPath;

    /// <summary>The symbol instance path the failed operation targeted; empty for session-level failures.</summary>
    public string SymbolPath { get; } = "";
}

/// <summary>Outcome of an RPC invocation: whether the method returned a value, and its rendering.</summary>
public sealed record TcRpcResult(bool HasReturn, string? Value, string? TypeName);

/// <summary>
/// A long-lived connection to a PLC runtime port with a variable-handle cache and the stale-handle
/// policy baked in. Handles cached across polls silently break when the target restarts or the
/// symbol table changes (download, online change) — reads through a stale handle can return wrong
/// data rather than an error. Every handle-lane operation therefore checks the target's symbol
/// version (one 1-byte ADS read) and drops the cache when it moved; a failed operation re-resolves
/// its handle once and retries; anything still failing surfaces as <see cref="TcSymbolException"/>,
/// never as a silently stale value.
///
/// Typed access sizes itself from the symbol table, so enum symbols (whose width varies with their
/// base type) read and write correctly from plain integers or field names — no matching .NET enum
/// type required.
/// </summary>
public sealed class AdsSymbolSession : IDisposable
{
    /// <summary>The standard first-PLC runtime port.</summary>
    public const int DefaultPlcPort = 851;

    private readonly ISymbolChannel _channel;
    private readonly object _sync = new();
    private readonly Dictionary<string, uint> _handles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SymbolShape> _shapes = new(StringComparer.OrdinalIgnoreCase);
    private byte? _symbolVersion;

    public AdsSymbolSession(string netId, int port = DefaultPlcPort)
        : this(new NativeSymbolChannel(netId, port))
    {
    }

    internal AdsSymbolSession(ISymbolChannel channel) => _channel = channel;

    /// <summary>Read a primitive symbol via its cached handle, marshalled as <typeparamref name="T"/>.</summary>
    public T Read<T>(string path)
        where T : unmanaged
    {
        lock (_sync)
        {
            return (T)HandleOp(path, handle => _channel.ReadViaHandle(handle, typeof(T)));
        }
    }

    /// <summary>Non-throwing <see cref="Read{T}"/>; false when the symbol is missing or the target unreachable.</summary>
    public bool TryRead<T>(string path, out T value)
        where T : unmanaged
    {
        try
        {
            value = Read<T>(path);
            return true;
        }
        catch (TcSymbolException)
        {
            value = default;
            return false;
        }
    }

    /// <summary>
    /// Read any 1/2/4/8-byte integer or enum symbol as a long, sized from the symbol table
    /// (PLC enums are not all INT-width).
    /// </summary>
    public long ReadInteger(string path)
    {
        lock (_sync)
        {
            var size = Shape(path).ByteSize;
            var bytes = (byte[])HandleOp(path, handle => _channel.ReadBytesViaHandle(handle, size));
            return bytes.Length switch
            {
                1 => bytes[0],
                2 => BitConverter.ToInt16(bytes),
                4 => BitConverter.ToInt32(bytes),
                8 => BitConverter.ToInt64(bytes),
                _ => throw new TcSymbolException(path, $"unexpected integer width {bytes.Length}.", null),
            };
        }
    }

    /// <summary>
    /// Read any symbol via the symbolic lane (fresh resolution, no handle cache) rendered as an
    /// invariant string. The slow-but-always-fresh lane; use the typed reads for polling.
    /// </summary>
    public string ReadRendered(string path)
    {
        lock (_sync)
        {
            try
            {
                return _channel.ReadRendered(path);
            }
            catch (Exception ex) when (ex is not TcSymbolException)
            {
                throw new TcSymbolException(path, ex.Message, ex);
            }
        }
    }

    /// <summary>
    /// Write a value to a symbol. Enum symbols accept a field name (string) or any integer, written
    /// at the enum's declared width; everything else goes through the symbolic lane, which coerces
    /// to the declared type.
    /// </summary>
    public void Write(string path, object? value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_sync)
        {
            var shape = Shape(path);
            if (shape.EnumFields is not null)
            {
                var bytes = EnumBytes(path, shape, value);
                HandleOp(path, handle =>
                {
                    _channel.WriteBytesViaHandle(handle, bytes);
                    return true;
                });
                return;
            }

            try
            {
                _channel.WriteSymbolic(path, value);
            }
            catch (Exception ex) when (ex is not TcSymbolException)
            {
                throw new TcSymbolException(path, ex.Message, ex);
            }
        }
    }

    /// <summary>Invoke a {attribute 'TcRpcEnable'} method positionally and render any return value.</summary>
    public TcRpcResult InvokeRpc(string symbolPath, string methodName, params object?[] parameters)
    {
        lock (_sync)
        {
            try
            {
                return _channel.InvokeRpc(symbolPath, methodName, parameters);
            }
            catch (Exception ex) when (ex is not TcSymbolException)
            {
                throw new TcSymbolException($"{symbolPath}.{methodName}", ex.Message, ex);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var handle in _handles.Values)
            {
                _channel.ReleaseHandle(handle);
            }

            _handles.Clear();
            _channel.Dispose();
        }
    }

    /// <summary>
    /// The stale-handle policy, applied around every handle-lane operation. Callers hold the lock.
    /// </summary>
    private object HandleOp(string path, Func<uint, object> operation)
    {
        SyncSymbolVersion(path);

        var handle = ResolveHandle(path);
        try
        {
            return operation(handle);
        }
#pragma warning disable CA1031 // Any failure through a cached handle triggers the one re-resolve retry.
        catch (Exception)
#pragma warning restore CA1031
        {
            InvalidateHandle(path);
        }

        try
        {
            handle = ResolveHandle(path);
            return operation(handle);
        }
        catch (Exception ex) when (ex is not TcSymbolException)
        {
            InvalidateHandle(path);
            throw new TcSymbolException(path, ex.Message, ex);
        }
    }

    /// <summary>
    /// Detect a restart / symbol table change via the 1-byte symbol version; a changed version
    /// drops every cached handle and shape. An unreachable target gets one reconnect attempt.
    /// </summary>
    private void SyncSymbolVersion(string path)
    {
        byte version;
        try
        {
            version = _channel.ReadSymbolVersion();
        }
#pragma warning disable CA1031 // No answer means down or restarting: reconnect once, then give up loudly.
        catch (Exception)
#pragma warning restore CA1031
        {
            try
            {
                _channel.Reconnect();
                InvalidateAll();
                version = _channel.ReadSymbolVersion();
            }
            catch (Exception ex) when (ex is not TcSymbolException)
            {
                throw new TcSymbolException(path, $"target unreachable: {ex.Message}", ex);
            }
        }

        if (_symbolVersion != version)
        {
            InvalidateAll();
            _symbolVersion = version;
        }
    }

    private uint ResolveHandle(string path)
    {
        if (_handles.TryGetValue(path, out var cached))
        {
            return cached;
        }

        try
        {
            var handle = _channel.CreateHandle(path);
            _handles[path] = handle;
            return handle;
        }
        catch (Exception ex) when (ex is not TcSymbolException)
        {
            throw new TcSymbolException(path, ex.Message, ex);
        }
    }

    private SymbolShape Shape(string path)
    {
        SyncSymbolVersion(path);
        if (_shapes.TryGetValue(path, out var cached))
        {
            return cached;
        }

        try
        {
            var shape = _channel.DescribeSymbol(path);
            _shapes[path] = shape;
            return shape;
        }
        catch (Exception ex) when (ex is not TcSymbolException)
        {
            throw new TcSymbolException(path, ex.Message, ex);
        }
    }

    private static byte[] EnumBytes(string path, SymbolShape shape, object value)
    {
        long numeric;
        if (value is string name)
        {
            if (shape.EnumFields!.TryGetValue(name, out var mapped))
            {
                numeric = mapped;
            }
            else if (long.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                numeric = parsed;
            }
            else
            {
                var known = string.Join(", ", shape.EnumFields.Keys);
                throw new TcSymbolException(path, $"'{name}' is not a field of the enum (known: {known}).", null);
            }
        }
        else
        {
            try
            {
                numeric = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                throw new TcSymbolException(path, $"cannot write a {value.GetType().Name} to an enum symbol.", ex);
            }
        }

        return shape.ByteSize switch
        {
            1 => [(byte)numeric],
            2 => BitConverter.GetBytes((short)numeric),
            4 => BitConverter.GetBytes((int)numeric),
            8 => BitConverter.GetBytes(numeric),
            var n => throw new TcSymbolException(path, $"unexpected enum width {n}.", null),
        };
    }

    private void InvalidateHandle(string path)
    {
        if (_handles.Remove(path, out var handle))
        {
            _channel.ReleaseHandle(handle);
        }
    }

    private void InvalidateAll()
    {
        _handles.Clear();
        _shapes.Clear();
    }
}
