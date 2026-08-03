using System.Globalization;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace TcKit.Ads;

/// <summary>
/// A symbol's declared width and, when it is an enum, its field map (name -> value,
/// case-insensitive). Sized reads/writes and the enum coercion both come from here.
/// </summary>
internal sealed record SymbolShape(int ByteSize, IReadOnlyDictionary<string, long>? EnumFields);

/// <summary>
/// The wire operations under <see cref="AdsSymbolSession"/>, seamed so the stale-handle policy
/// tests without a live runtime. Every member throws on failure; the session owns recovery.
/// </summary>
internal interface ISymbolChannel : IDisposable
{
    /// <summary>Tear down and re-establish the connection.</summary>
    void Reconnect();

    /// <summary>The target's 1-byte symbol version (changes on restart, download, online change).</summary>
    byte ReadSymbolVersion();

    uint CreateHandle(string path);

    /// <summary>Best-effort: releasing a handle the target already dropped must not throw.</summary>
    void ReleaseHandle(uint handle);

    object ReadViaHandle(uint handle, Type clrType);

    byte[] ReadBytesViaHandle(uint handle, int count);

    void WriteBytesViaHandle(uint handle, byte[] bytes);

    /// <summary>The symbol's declared width and enum field map (null when not an enum).</summary>
    SymbolShape DescribeSymbol(string path);

    /// <summary>Symbolic-lane read (fresh resolution), rendered as an invariant string.</summary>
    string ReadRendered(string path);

    /// <summary>Symbolic-lane write; the ADS layer coerces to the declared type.</summary>
    void WriteSymbolic(string path, object value);

    TcRpcResult InvokeRpc(string symbolPath, string methodName, object?[] parameters);
}

/// <summary>Native channel over Beckhoff.TwinCAT.Ads.</summary>
internal sealed class NativeSymbolChannel : ISymbolChannel
{
    /// <summary>ADSIGRP_SYM_VERSION: the runtime's symbol table version byte.</summary>
    private const uint SymbolVersionIndexGroup = 0xF008;

    private readonly string _netId;
    private readonly int _port;
    private AdsClient _client;

    public NativeSymbolChannel(string netId, int port)
    {
        _netId = netId;
        _port = port;
        _client = Connect();
    }

    public void Reconnect()
    {
        _client.Dispose();
        _client = Connect();
    }

    public byte ReadSymbolVersion() => _client.ReadAny<byte>(SymbolVersionIndexGroup, 0);

    public uint CreateHandle(string path) => _client.CreateVariableHandle(path);

    public void ReleaseHandle(uint handle)
    {
        try
        {
            _client.DeleteVariableHandle(handle);
        }
#pragma warning disable CA1031 // Best-effort: the target (or the handle) may already be gone.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Nothing to release.
        }
    }

    public object ReadViaHandle(uint handle, Type clrType) => _client.ReadAny(handle, clrType);

    public byte[] ReadBytesViaHandle(uint handle, int count)
    {
        var result = _client.ReadAsResult(handle, count);
        result.ThrowOnError();
        return result.Data.ToArray();
    }

    public void WriteBytesViaHandle(uint handle, byte[] bytes) => _client.Write(handle, bytes.AsMemory());

    public SymbolShape DescribeSymbol(string path)
    {
        var symbol = (IAdsSymbol)_client.ReadSymbol(path);
        return new SymbolShape(symbol.ByteSize, EnumFieldsOf(symbol.DataType));
    }

    public string ReadRendered(string path) => Render(_client.ReadValue(path));

    public void WriteSymbolic(string path, object value) => _client.WriteValue(path, value);

    public TcRpcResult InvokeRpc(string symbolPath, string methodName, object?[] parameters)
    {
        var result = _client.InvokeRpcMethod(symbolPath, methodName, parameters!);
        return result is null
            ? new TcRpcResult(false, null, null)
            : new TcRpcResult(true, Render(result), result.GetType().Name);
    }

    public void Dispose() => _client.Dispose();

    private AdsClient Connect()
    {
        var client = new AdsClient();
        client.Connect(AmsNetId.Parse(_netId), _port);
        return client;
    }

    private static IReadOnlyDictionary<string, long>? EnumFieldsOf(IDataType? dataType)
    {
        if (dataType is not IEnumType enumType)
        {
            return null;
        }

        var fields = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in enumType.EnumValues)
        {
            fields[field.Name] = Convert.ToInt64(field.Value, CultureInfo.InvariantCulture);
        }

        return fields;
    }

    private static string Render(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "True" : "False",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
