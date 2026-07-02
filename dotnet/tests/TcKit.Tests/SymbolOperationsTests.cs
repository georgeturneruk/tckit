using System.Text.Json;
using TcKit.Adapters.Ads;

namespace TcKit.Tests;

/// <summary>
/// read_symbols / write_symbols / invoke_rpc orchestration against the fake symbol seam: the
/// best-effort loops (null on unreadable, per-path write errors), JSON value coercion, the
/// success-only-when-all-written rule, and the RPC return shaping.
/// </summary>
public sealed class SymbolOperationsTests
{
    private const string Target = "1.2.3.4.1.1";

    [Fact]
    public void ReadSymbols_ReturnsValues_AndNullForUnreadable()
    {
        var session = new FakeSymbolSession();
        session.Readable["MAIN.nCounter"] = "42";
        var factory = new FakeSymbolSessionFactory(session);

        var values = SymbolOperations.ReadSymbols(factory, Target, ["MAIN.nCounter", "MAIN.missing"]);

        Assert.Equal("42", values["MAIN.nCounter"]);
        Assert.Null(values["MAIN.missing"]);
        Assert.Equal(SymbolOperations.DefaultPlcPort, factory.OpenedPort);
        Assert.True(session.Disposed);
    }

    [Fact]
    public void ReadSymbols_EmptyPaths_ReturnsEmpty_WithoutOpening()
    {
        var factory = new FakeSymbolSessionFactory();

        var values = SymbolOperations.ReadSymbols(factory, Target, []);

        Assert.Empty(values);
        Assert.Null(factory.OpenedNetId);
    }

    [Fact]
    public void ReadSymbols_EmptyTarget_Throws()
        => Assert.Throws<ArgumentException>(() => SymbolOperations.ReadSymbols(new FakeSymbolSessionFactory(), "", ["x"]));

    [Fact]
    public void WriteSymbols_AllSucceed_ReportsWritten_AndCoercesJson()
    {
        var session = new FakeSymbolSession();
        var factory = new FakeSymbolSessionFactory(session);
        var writes = JsonWrites("""{ "MAIN.nSetpoint": 42, "GVL.bEnable": true }""");

        var result = SymbolOperations.WriteSymbols(factory, Target, writes);

        Assert.True(result.Success);
        Assert.Equal(42L, session.Written["MAIN.nSetpoint"]);
        Assert.Equal(true, session.Written["GVL.bEnable"]);
        var written = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.Details["written"]);
        Assert.Equal("42", written["MAIN.nSetpoint"]);
    }

    [Fact]
    public void WriteSymbols_PartialFailure_ReportsErrors_AndIsNotSuccess()
    {
        var session = new FakeSymbolSession();
        session.FailWrites.Add("MAIN.bad");
        var factory = new FakeSymbolSessionFactory(session);
        var writes = JsonWrites("""{ "MAIN.good": 1, "MAIN.bad": 2 }""");

        var result = SymbolOperations.WriteSymbols(factory, Target, writes);

        Assert.False(result.Success);
        var written = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.Details["written"]);
        var errors = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.Details["errors"]);
        Assert.True(written.ContainsKey("MAIN.good"));
        Assert.True(errors.ContainsKey("MAIN.bad"));
    }

    [Fact]
    public void WriteSymbols_Empty_IsSuccess()
    {
        var result = SymbolOperations.WriteSymbols(new FakeSymbolSessionFactory(), Target, new Dictionary<string, object?>());

        Assert.True(result.Success);
    }

    [Fact]
    public void InvokeRpc_WithReturn_PopulatesReturnDetails()
    {
        var session = new FakeSymbolSession();
        session.RpcReturns["M_Add"] = new RpcOutcome(true, "7", "Int32");
        var factory = new FakeSymbolSessionFactory(session);

        var result = SymbolOperations.InvokeRpc(factory, Target, "MAIN.fbCalc", "M_Add", JsonParams("[3, 4]"));

        Assert.True(result.Success);
        Assert.Equal("7", result.Details["return_value"]);
        Assert.Equal("Int32", result.Details["return_type"]);
        Assert.Equal([3L, 4L], session.RpcCalls[0]);
    }

    [Fact]
    public void InvokeRpc_VoidMethod_OmitsReturnDetails()
    {
        var factory = new FakeSymbolSessionFactory();

        var result = SymbolOperations.InvokeRpc(factory, Target, "MAIN.fbPid", "M_Reset", []);

        Assert.True(result.Success);
        Assert.False(result.Details.ContainsKey("return_value"));
    }

    [Fact]
    public void InvokeRpc_MissingSymbolPath_Throws()
        => Assert.Throws<ArgumentException>(
            () => SymbolOperations.InvokeRpc(new FakeSymbolSessionFactory(), Target, "", "M_Reset", []));

    private static IReadOnlyDictionary<string, object?> JsonWrites(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
            .ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);

    private static IReadOnlyList<object?> JsonParams(string json)
        => JsonSerializer.Deserialize<List<JsonElement>>(json)!.Select(e => (object?)e).ToList();
}
