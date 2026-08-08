using System.Text.RegularExpressions;

namespace TcKit.Adapters.Analysis;

/// <summary>
/// Maps a declared type expression onto a <see cref="TypeClass"/>. Project-declared types resolve
/// through the index the caller supplies, which is what lets a rule tell <c>ST_Config</c> from
/// <c>FB_Motor</c> from <c>E_State</c>; TwinCAT identifiers are case-insensitive, so the lookup is too.
/// </summary>
public sealed partial class TypeClassifier
{
    private readonly IReadOnlyDictionary<string, TypeClass> _projectTypes;

    public TypeClassifier(IReadOnlyDictionary<string, TypeClass> projectTypes)
    {
        ArgumentNullException.ThrowIfNull(projectTypes);
        _projectTypes = projectTypes;
    }

    /// <summary>Classify a type expression such as <c>ARRAY [0..9] OF POINTER TO ST_Foo</c>.</summary>
    public TypeClass Classify(string typeExpression)
    {
        if (string.IsNullOrWhiteSpace(typeExpression))
        {
            return TypeClass.Unknown;
        }

        var text = typeExpression.Trim();

        // The outermost qualifier wins: an ARRAY OF POINTER is an array, and a naming rule that
        // wants the element type would need the recursive prefixes deferred out of v1.
        if (text.StartsWith("ARRAY", StringComparison.OrdinalIgnoreCase))
        {
            return TypeClass.Array;
        }

        if (text.StartsWith("POINTER TO", StringComparison.OrdinalIgnoreCase))
        {
            return TypeClass.Pointer;
        }

        if (text.StartsWith("REFERENCE TO", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("REF_TO", StringComparison.OrdinalIgnoreCase))
        {
            return TypeClass.Reference;
        }

        // STRING(80) and WSTRING(255) carry a length; the base name is what classifies.
        var baseName = TypeArguments().Replace(text, "").Trim();

        return baseName.ToUpperInvariant() switch
        {
            "BOOL" => TypeClass.Bool,
            "BYTE" or "WORD" or "DWORD" or "LWORD"
                or "SINT" or "USINT" or "INT" or "UINT"
                or "DINT" or "UDINT" or "LINT" or "ULINT" => TypeClass.Integer,
            "REAL" or "LREAL" => TypeClass.Real,
            "STRING" or "WSTRING" => TypeClass.String,
            "TIME" or "LTIME" or "TIME_OF_DAY" or "TOD" or "LTOD"
                or "DATE" or "LDATE" or "DATE_AND_TIME" or "DT" or "LDT" => TypeClass.Time,
            _ => _projectTypes.TryGetValue(baseName, out var known) ? known : TypeClass.Unknown,
        };
    }

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex TypeArguments();
}
