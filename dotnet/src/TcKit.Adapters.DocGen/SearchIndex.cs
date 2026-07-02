using System.Text.Json;
using System.Text.Json.Serialization;

namespace TcKit.Adapters.DocGen;

/// <summary>Builds the per-PLC lunr search index (one JSON array of object entries) the HTML search box loads.</summary>
internal static class SearchIndex
{
    private sealed record Entry(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("body")] string Body);

    private static readonly JsonSerializerOptions s_options = new() { WriteIndented = false };

    internal static string Build(IReadOnlyList<ObjectDoc> objects)
    {
        var entries = objects.Select(obj =>
        {
            var methodNames = string.Join(" ", obj.Methods.Select(m => m.Name));
            var propNames = string.Join(" ", obj.Properties.Select(p => p.Name));
            var varNames = string.Join(" ", obj.Inputs.Concat(obj.Outputs).Concat(obj.Inout).Concat(obj.Variables).Select(v => v.Name));
            var body = $"{methodNames} {propNames} {varNames}".Trim();
            return new Entry(obj.Name, obj.Name, obj.ObjType, obj.Comment.Description, body);
        }).ToList();

        return JsonSerializer.Serialize(entries, s_options);
    }
}
