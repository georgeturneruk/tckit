using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TcKit.Core.Ports;
using TcKit.Core.Serialization;

namespace TcKit.Server.Tools;

/// <summary>
/// Symbol-level ADS tools: read symbols, write symbols, and invoke RPC methods on a running runtime.
/// They target a runtime by AMS Net ID (no XAE). write_symbols and invoke_rpc mutate / execute live
/// PLC state, so they gate on confirmed=true (the Python safety-confirmation contract).
/// </summary>
[McpServerToolType]
public sealed class SymbolTools(ISymbolIo symbols)
{
    [McpServerTool(Name = "ReadSymbols")]
    [Description("Read PLC symbols by instance path on a running runtime (port 851). Best-effort: an "
        + "unreadable path maps to null in values rather than failing. The target must be in Run mode. "
        + "paths is the list of symbol instance paths (e.g. ['MAIN.nCounter', 'GVL.bEnable']).")]
    public Task<string> ReadSymbols(
        string targetAmsId, string[] paths, CancellationToken cancellationToken = default)
        => Run(async () =>
        {
            var values = await symbols.ReadSymbolsAsync(targetAmsId, paths ?? [], cancellationToken).ConfigureAwait(false);
            return new { success = true, values };
        });

    [McpServerTool(Name = "WriteSymbols")]
    [Description("Write PLC symbols by instance path on a running runtime. WARNING: modifies live PLC "
        + "state; requires confirmed=true. writesJson is a JSON object mapping path -> value (e.g. "
        + "'{\"MAIN.nSetpoint\": 42, \"GVL.bEnable\": true}'). Best-effort: per-symbol failures land in "
        + "details.errors; success is true only when every write succeeded.")]
    public Task<string> WriteSymbols(
        string targetAmsId, string writesJson, bool confirmed = false, CancellationToken cancellationToken = default)
        => Run(() =>
        {
            if (!confirmed)
            {
                return Task.FromResult<object>(Gate("write_symbols"));
            }

            var writes = ParseObject(writesJson);
            if (writes.Count == 0)
            {
                throw new ArgumentException("writesJson must be a non-empty JSON object of symbol path -> value.");
            }

            return Box(symbols.WriteSymbolsAsync(targetAmsId, writes, cancellationToken));
        });

    [McpServerTool(Name = "InvokeRpc")]
    [Description("Invoke a PLC method decorated with {attribute 'TcRpcEnable'} on an FB instance. "
        + "WARNING: executes code on a live PLC; requires confirmed=true. symbolPath is the FB instance "
        + "(e.g. 'MAIN.fbPid', or 'MAIN' for top-level methods); methodName is the declared method name; "
        + "paramsJson is a JSON array of positional parameters matching VAR_INPUT order (default '[]').")]
    public Task<string> InvokeRpc(
        string targetAmsId, string symbolPath, string methodName, string paramsJson = "[]",
        bool confirmed = false, CancellationToken cancellationToken = default)
        => Run(() =>
        {
            if (!confirmed)
            {
                return Task.FromResult<object>(Gate("invoke_rpc"));
            }

            var parameters = ParseArray(paramsJson);
            return Box(symbols.InvokeRpcAsync(targetAmsId, symbolPath, methodName, parameters, cancellationToken));
        });

    private static object Gate(string tool) => new
    {
        success = false,
        error = $"{tool} requires confirmed=true. Verify the target and values, then retry with confirmed=true.",
    };

    private static async Task<object> Box<T>(Task<T> task) where T : notnull => await task.ConfigureAwait(false);

    private static IReadOnlyDictionary<string, object?> ParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, object?>();
        }

        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
            ?? throw new ArgumentException("writesJson must be a JSON object of symbol path -> value.");
        return parsed.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);
    }

    private static IReadOnlyList<object?> ParseArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var parsed = JsonSerializer.Deserialize<List<JsonElement>>(json)
            ?? throw new ArgumentException("paramsJson must be a JSON array of positional parameters.");
        return parsed.Select(e => (object?)e).ToList();
    }

    private static async Task<string> Run(Func<Task<object>> call)
    {
        try
        {
            return TckitJson.Serialize(await call().ConfigureAwait(false));
        }
#pragma warning disable CA1031 // The tool boundary funnels every failure into the error contract.
        catch (Exception exc)
        {
            return TckitJson.Serialize(new { error = exc.Message });
        }
#pragma warning restore CA1031
    }
}
