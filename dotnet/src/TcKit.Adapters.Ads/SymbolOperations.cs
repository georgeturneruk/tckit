using System.Globalization;
using System.Text.Json;
using TcKit.Core.Models;

namespace TcKit.Adapters.Ads;

/// <summary>
/// read_symbols / write_symbols / invoke_rpc orchestration against the <see cref="ISymbolSessionFactory"/>
/// seam, so the best-effort loops and the Result shaping are testable against a fake without a live
/// runtime. Mirrors Read-TcSymbol.ps1 / Write-TcSymbol.ps1 / Invoke-TcRpcMethod.ps1.
/// </summary>
internal static class SymbolOperations
{
    /// <summary>The standard first-PLC runtime port.</summary>
    public const int DefaultPlcPort = 851;

    public static IReadOnlyDictionary<string, string?> ReadSymbols(
        ISymbolSessionFactory factory, string targetAmsId, IReadOnlyList<string> paths)
    {
        Require(targetAmsId);

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (paths.Count == 0)
        {
            return values;
        }

        using var session = factory.Open(targetAmsId, DefaultPlcPort);
        foreach (var raw in paths)
        {
            var path = raw.Trim();
            if (path.Length == 0)
            {
                continue;
            }

            // Best-effort: an unresolvable symbol maps to null rather than failing the call.
            values[path] = TryRead(session, path);
        }

        return values;
    }

    public static Result WriteSymbols(
        ISymbolSessionFactory factory, string targetAmsId, IReadOnlyDictionary<string, object?> writes)
    {
        Require(targetAmsId);

        var written = new Dictionary<string, object?>(StringComparer.Ordinal);
        var errors = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (writes.Count == 0)
        {
            return WriteResult(written, errors);
        }

        using var session = factory.Open(targetAmsId, DefaultPlcPort);
        foreach (var (path, value) in writes)
        {
            try
            {
                var coerced = CoerceJson(value);
                session.WriteValue(path, coerced);
                written[path] = ToInvariantString(coerced);
            }
#pragma warning disable CA1031 // Best-effort: a per-symbol failure is recorded and does not abort the rest.
            catch (Exception exc)
            {
                errors[path] = exc.Message;
            }
#pragma warning restore CA1031
        }

        return WriteResult(written, errors);
    }

    public static Result InvokeRpc(
        ISymbolSessionFactory factory, string targetAmsId, string symbolPath, string methodName,
        IReadOnlyList<object?> parameters)
    {
        Require(targetAmsId);
        if (string.IsNullOrEmpty(symbolPath))
        {
            throw new ArgumentException("symbolPath is required (e.g. 'MAIN.fbPid' or 'MAIN' for top-level methods).");
        }

        if (string.IsNullOrEmpty(methodName))
        {
            throw new ArgumentException("methodName is required (e.g. 'M_Reset').");
        }

        var args = parameters.Select(CoerceJson).ToArray();

        using var session = factory.Open(targetAmsId, DefaultPlcPort);
        var outcome = session.InvokeRpc(symbolPath, methodName, args);

        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["symbol_path"] = symbolPath,
            ["method_name"] = methodName,
        };
        if (outcome.HasReturn)
        {
            details["return_value"] = outcome.Value;
            details["return_type"] = outcome.TypeName;
        }

        return Result.Ok(details);
    }

    private static string? TryRead(ISymbolSession session, string path)
    {
        try
        {
            return session.ReadValue(path);
        }
#pragma warning disable CA1031 // Best-effort read: an unresolvable symbol maps to null.
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    private static Result WriteResult(
        Dictionary<string, object?> written, Dictionary<string, object?> errors) => new()
    {
        Success = errors.Count == 0,
        Details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["written"] = written,
            ["errors"] = errors,
        },
    };

    private static void Require(string targetAmsId)
    {
        if (string.IsNullOrEmpty(targetAmsId))
        {
            throw new ArgumentException("TargetAmsId required.");
        }
    }

    /// <summary>
    /// Decode a JSON-sourced value (the MCP layer hands write/RPC values in as <see cref="JsonElement"/>)
    /// into the natural CLR primitive the ADS marshaller can coerce to the symbol's declared type.
    /// Non-JSON values pass through unchanged.
    /// </summary>
    internal static object? CoerceJson(object? value) => value switch
    {
        JsonElement element => CoerceElement(element),
        _ => value,
    };

    private static object? CoerceElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Null => null,
        JsonValueKind.Number => CoerceNumber(element),
        JsonValueKind.Array => element.EnumerateArray().Select(e => CoerceElement(e)).ToArray(),
        _ => element.ToString(),
    };

    private static object CoerceNumber(JsonElement element)
        => element.TryGetInt64(out var i) ? (object)i : element.GetDouble();

    private static string? ToInvariantString(object? value) => value switch
    {
        null => null,
        bool b => b ? "True" : "False",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
