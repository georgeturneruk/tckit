using TcKit.Ads;

namespace TcKit.Tests;

/// <summary>
/// The stale-handle policy in TcKit.Ads.AdsSymbolSession against a fake channel that mimics the
/// real hazard: a target restart regenerates the symbol table, and reads through a pre-restart
/// handle return garbage rather than an error. The session must return fresh values or a typed
/// error — never a silently stale/garbage value. Plus enum-aware width-sized writes.
/// </summary>
public sealed class AdsSymbolSessionTests
{
    /// <summary>
    /// Fake channel with restart semantics. Each CreateHandle issues a handle stamped with the
    /// current generation; Restart() bumps the generation and the symbol version. An operation
    /// through an old-generation handle returns <see cref="Garbage"/> (the silent-staleness case)
    /// or throws when <see cref="StaleHandlesThrow"/> is set (the loud case).
    /// </summary>
    private sealed class FakeChannel : ISymbolChannel
    {
        private int _generation;
        private byte _version;
        private uint _nextHandle = 1;
        private readonly Dictionary<uint, (string Path, int Generation)> _issued = [];

        public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SymbolShape> Shapes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, byte[]> BytesWritten { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, object> SymbolicWrites { get; } = new(StringComparer.OrdinalIgnoreCase);
        public object Garbage { get; set; } = false;
        public bool StaleHandlesThrow { get; set; }
        public bool Unreachable { get; set; }
        public int ReconnectCount { get; private set; }
        public int HandlesCreated { get; private set; }

        /// <summary>A real restart: handles die and the symbol version regenerates.</summary>
        public void Restart()
        {
            _generation++;
            _version++;
        }

        /// <summary>Handles die but the version byte happens not to move (the worst case).</summary>
        public void DropHandlesQuietly() => _generation++;

        public void Reconnect()
        {
            if (Unreachable)
            {
                throw new InvalidOperationException("no route to target");
            }

            ReconnectCount++;
        }

        public byte ReadSymbolVersion()
            => Unreachable ? throw new InvalidOperationException("no route to target") : _version;

        public uint CreateHandle(string path)
        {
            if (!Values.ContainsKey(path))
            {
                throw new InvalidOperationException($"symbol '{path}' not found");
            }

            var handle = _nextHandle++;
            _issued[handle] = (path, _generation);
            HandlesCreated++;
            return handle;
        }

        public void ReleaseHandle(uint handle) => _issued.Remove(handle);

        public object ReadViaHandle(uint handle, Type clrType)
        {
            var (path, generation) = _issued[handle];
            if (generation != _generation)
            {
                return StaleHandlesThrow
                    ? throw new InvalidOperationException("invalid handle")
                    : Garbage; // the real hazard: wrong data, no error
            }

            return Values[path];
        }

        public byte[] ReadBytesViaHandle(uint handle, int count)
        {
            var (path, generation) = _issued[handle];
            if (generation != _generation)
            {
                return StaleHandlesThrow ? throw new InvalidOperationException("invalid handle") : new byte[count];
            }

            var value = Convert.ToInt64(Values[path], System.Globalization.CultureInfo.InvariantCulture);
            return BitConverter.GetBytes(value)[..count];
        }

        public void WriteBytesViaHandle(uint handle, byte[] bytes)
        {
            var (path, generation) = _issued[handle];
            if (generation != _generation)
            {
                throw new InvalidOperationException("invalid handle");
            }

            BytesWritten[path] = bytes;
        }

        public SymbolShape DescribeSymbol(string path)
            => Shapes.TryGetValue(path, out var shape) ? shape : new SymbolShape(8, null);

        public string ReadRendered(string path)
            => Values.TryGetValue(path, out var v)
                ? v.ToString() ?? ""
                : throw new InvalidOperationException($"symbol '{path}' not found");

        public void WriteSymbolic(string path, object value) => SymbolicWrites[path] = value;

        public TcRpcResult InvokeRpc(string symbolPath, string methodName, object?[] parameters)
            => new(true, $"{symbolPath}.{methodName}({parameters.Length})", "STRING");

        public void Dispose()
        {
        }
    }

    [Fact]
    public void Read_ReturnsValue_AndCachesHandleAcrossPolls()
    {
        var channel = new FakeChannel { Values = { ["GVL.bFlag"] = true } };
        using var session = new AdsSymbolSession(channel);

        Assert.True(session.Read<bool>("GVL.bFlag"));
        Assert.True(session.Read<bool>("GVL.bFlag"));
        Assert.True(session.Read<bool>("GVL.bFlag"));

        Assert.Equal(1, channel.HandlesCreated); // cached, not re-resolved per poll
    }

    [Fact]
    public void Read_AfterRestart_ReturnsFreshValue_NeverGarbage()
    {
        // The observed real-world failure: a BOOL polled as True before a restart silently read
        // False (garbage) through the stale handle afterwards.
        var channel = new FakeChannel { Values = { ["GVL.bFlag"] = true }, Garbage = false };
        using var session = new AdsSymbolSession(channel);

        Assert.True(session.Read<bool>("GVL.bFlag"));

        channel.Restart(); // symbol version bumps; old handles now yield garbage

        Assert.True(session.Read<bool>("GVL.bFlag")); // fresh handle, fresh value
        Assert.Equal(2, channel.HandlesCreated);
    }

    [Fact]
    public void Read_StaleHandleThatThrows_RecoversWithOneRetry()
    {
        var channel = new FakeChannel
        {
            Values = { ["GVL.nCount"] = 42 },
            StaleHandlesThrow = true,
        };
        using var session = new AdsSymbolSession(channel);

        Assert.Equal(42, session.Read<int>("GVL.nCount"));

        // A stale handle the version check cannot see (the version byte happens not to move)
        // still recovers: the failed read invalidates and re-resolves once.
        channel.DropHandlesQuietly();

        Assert.Equal(42, session.Read<int>("GVL.nCount"));
    }

    [Fact]
    public void Read_MissingSymbol_ThrowsTyped()
    {
        var channel = new FakeChannel();
        using var session = new AdsSymbolSession(channel);

        var ex = Assert.Throws<TcSymbolException>(() => session.Read<bool>("GVL.bAbsent"));
        Assert.Equal("GVL.bAbsent", ex.SymbolPath);
    }

    [Fact]
    public void Read_UnreachableTarget_ThrowsTyped_AfterReconnectAttempt()
    {
        var channel = new FakeChannel { Values = { ["GVL.bFlag"] = true }, Unreachable = true };
        using var session = new AdsSymbolSession(channel);

        var ex = Assert.Throws<TcSymbolException>(() => session.Read<bool>("GVL.bFlag"));
        Assert.Contains("unreachable", ex.Message);
    }

    [Fact]
    public void TryRead_FalseOnFailure_TrueOnSuccess()
    {
        var channel = new FakeChannel { Values = { ["GVL.bFlag"] = true } };
        using var session = new AdsSymbolSession(channel);

        Assert.True(session.TryRead<bool>("GVL.bFlag", out var value));
        Assert.True(value);
        Assert.False(session.TryRead<bool>("GVL.bAbsent", out _));
    }

    [Fact]
    public void ReadInteger_SizesFromSymbolTable()
    {
        var channel = new FakeChannel
        {
            Values = { ["GVL.eState"] = 3L },
            Shapes = { ["GVL.eState"] = new SymbolShape(2, null) }, // INT-width enum
        };
        using var session = new AdsSymbolSession(channel);

        Assert.Equal(3, session.ReadInteger("GVL.eState"));
    }

    [Fact]
    public void Write_EnumFieldName_WritesNumericAtDeclaredWidth()
    {
        var fields = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["Idle"] = 0,
            ["Running"] = 5,
        };
        var channel = new FakeChannel
        {
            Values = { ["GVL.eState"] = 0L },
            Shapes = { ["GVL.eState"] = new SymbolShape(2, fields) },
        };
        using var session = new AdsSymbolSession(channel);

        session.Write("GVL.eState", "Running");

        Assert.Equal(BitConverter.GetBytes((short)5), channel.BytesWritten["GVL.eState"]);
    }

    [Fact]
    public void Write_EnumInteger_WritesAtDeclaredWidth()
    {
        var channel = new FakeChannel
        {
            Values = { ["GVL.eState"] = 0L },
            Shapes = { ["GVL.eState"] = new SymbolShape(2, new Dictionary<string, long> { ["Idle"] = 0 }) },
        };
        using var session = new AdsSymbolSession(channel);

        session.Write("GVL.eState", 7);

        Assert.Equal(BitConverter.GetBytes((short)7), channel.BytesWritten["GVL.eState"]);
    }

    [Fact]
    public void Write_EnumUnknownField_ThrowsWithKnownFields()
    {
        var channel = new FakeChannel
        {
            Values = { ["GVL.eState"] = 0L },
            Shapes = { ["GVL.eState"] = new SymbolShape(2, new Dictionary<string, long> { ["Idle"] = 0 }) },
        };
        using var session = new AdsSymbolSession(channel);

        var ex = Assert.Throws<TcSymbolException>(() => session.Write("GVL.eState", "Bogus"));
        Assert.Contains("Idle", ex.Message);
    }

    [Fact]
    public void Write_NonEnum_GoesThroughSymbolicLane()
    {
        var channel = new FakeChannel { Values = { ["GVL.fSetpoint"] = 0.0 } };
        using var session = new AdsSymbolSession(channel);

        session.Write("GVL.fSetpoint", 3.5);

        Assert.Equal(3.5, channel.SymbolicWrites["GVL.fSetpoint"]);
    }

    [Fact]
    public void ReadRendered_And_InvokeRpc_WrapFailuresTyped()
    {
        var channel = new FakeChannel { Values = { ["GVL.sName"] = "pump" } };
        using var session = new AdsSymbolSession(channel);

        Assert.Equal("pump", session.ReadRendered("GVL.sName"));
        Assert.Throws<TcSymbolException>(() => session.ReadRendered("GVL.sAbsent"));

        var rpc = session.InvokeRpc("MAIN.fbPid", "Reset", 1, 2);
        Assert.True(rpc.HasReturn);
        Assert.Equal("MAIN.fbPid.Reset(2)", rpc.Value);
    }
}
