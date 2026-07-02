using System.Text.RegularExpressions;

namespace TcKit.Adapters.Automation;

/// <summary>
/// Splits combined ST source into the declaration and implementation halves that the Automation
/// Interface writes separately (<c>DeclarationText</c> / <c>ImplementationText</c>). Faithful port
/// of the bridge harness's <c>Split-TcCode</c>.
/// </summary>
internal static partial class StCode
{
    /// <summary>
    /// Split rules, in order: (1) if any END_VAR is present, the declaration is everything up to and
    /// including the last END_VAR; (2) else if a POU/member header keyword line is present, split
    /// after the last such line; (3) else the whole input is the implementation.
    /// </summary>
    public static (string Declaration, string Implementation) Split(string code)
    {
        code = code.Replace("\r\n", "\n", StringComparison.Ordinal);

        var endVar = EndVarBlock().Matches(code);
        if (endVar.Count > 0)
        {
            return CutAfter(code, endVar[^1]);
        }

        var header = HeaderLine().Matches(code);
        if (header.Count > 0)
        {
            return CutAfter(code, header[^1]);
        }

        return ("", code);
    }

    private static (string Declaration, string Implementation) CutAfter(string code, Match match)
    {
        var cut = match.Index + match.Length;
        var declaration = code[..cut];
        var implementation = cut < code.Length ? code[cut..].TrimStart('\n', '\r', ' ', '\t') : "";
        return (declaration, implementation);
    }

    [GeneratedRegex(@"^[ \t]*END_VAR[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex EndVarBlock();

    [GeneratedRegex(
        @"^[ \t]*(METHOD|FUNCTION_BLOCK|FUNCTION|PROGRAM|INTERFACE|PROPERTY|ACTION|VAR_GLOBAL)\b[^\n]*$",
        RegexOptions.Multiline)]
    private static partial Regex HeaderLine();
}
