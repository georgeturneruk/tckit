using System.Text.Json;
using System.Text.Json.Serialization;

namespace TcKit.Core.Serialization;

/// <summary>
/// The single JSON contract for tool output. snake_case property names and string
/// enum values keep the MCP surface consistent across every tool; nulls are emitted
/// (not omitted) so optional fields are explicit. Shared by the MCP server, the CLI,
/// and the parity-oracle cross-check so all three render identically.
/// </summary>
public static class TckitJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        // Dictionary keys (PLC-project names) are left verbatim: DictionaryKeyPolicy stays null.
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
