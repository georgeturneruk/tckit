using System.Text.RegularExpressions;

namespace TcKit.Core.Authoring;

/// <summary>
/// Pure text manipulation of a POU/method declaration's VAR scope blocks. Adds a single variable
/// line into the named scope (creating the block at its conventional rank if absent) and removes a
/// single variable line (refusing multi-name lists and line-continued declarations). Shared by
/// both writer backends. Faithful port of the bridge harness's Add-VariableToDeclaration /
/// Remove-VariableFromDeclaration.
/// </summary>
public static class VarBlock
{
    /// <summary>Insert <paramref name="variableLine"/> into the <paramref name="scopeName"/> block.</summary>
    public static string AddVariable(string declarationText, string scopeName, string variableLine)
    {
        var headerPattern = ScopePattern(scopeName); // throws on an unknown scope
        var insert = "    " + variableLine;
        var lines = new List<string>(declarationText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));

        var headerIdx = lines.FindIndex(l => Regex.IsMatch(l.Trim(), headerPattern));
        if (headerIdx >= 0)
        {
            var endIdx = lines.FindIndex(headerIdx + 1, l => l.Trim() == "END_VAR");
            if (endIdx < 0)
            {
                throw new InvalidOperationException(
                    $"Scope '{scopeName}' opened at line {headerIdx + 1} has no matching END_VAR.");
            }

            lines.Insert(endIdx, insert);
            return string.Join('\n', lines);
        }

        // No matching block: create one at the conventional position (ordered by scope rank).
        var headerText = scopeName.Trim().ToUpperInvariant();
        var newRank = ScopeRank(scopeName);

        var insertAt = -1;
        var k = 0;
        while (k < lines.Count)
        {
            var match = s_anyHeader.Match(lines[k].Trim());
            if (match.Success)
            {
                var existingType = Regex.Replace(match.Groups[1].Value, @"\s+", " ").ToUpperInvariant();
                if (ScopeRank(existingType) > newRank)
                {
                    insertAt = k;
                    break;
                }

                // Skip past this block's END_VAR so we don't re-match its body.
                var kEnd = lines.FindIndex(k + 1, l => l.Trim() == "END_VAR");
                if (kEnd >= 0)
                {
                    k = kEnd + 1;
                    continue;
                }
            }

            k++;
        }

        if (insertAt < 0)
        {
            while (lines.Count > 0 && lines[^1].Trim().Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            insertAt = lines.Count;
        }

        lines.InsertRange(insertAt, [headerText, insert, "END_VAR"]);
        return string.Join('\n', lines);
    }

    /// <summary>Remove the single declaration line for <paramref name="variableName"/>.</summary>
    public static string RemoveVariable(string declarationText, string variableName)
    {
        var lines = declarationText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var escaped = Regex.Escape(variableName);
        var singleName = new Regex($@"^\s*{escaped}\b\s*(AT\s+[^:]+)?\s*:");
        var multiName = new Regex($@"^\s*{escaped}\b\s*,");
        var listPrefix = new Regex($@"^\s*[A-Za-z_][A-Za-z0-9_]*(\s*,\s*[A-Za-z_][A-Za-z0-9_]*)*\s*,\s*{escaped}\b\s*[,:]");

        var matches = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (multiName.IsMatch(line) || listPrefix.IsMatch(line))
            {
                throw new InvalidOperationException(
                    $"Variable '{variableName}' is part of a multi-name declaration (line {i + 1}). "
                    + "Use update_pou_declaration_patch for partial edits.");
            }

            if (singleName.IsMatch(line))
            {
                if (!line.Contains(';', StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Variable '{variableName}' line {i + 1} doesn't terminate with ';' on the same line. "
                        + "Use update_pou_declaration_patch.");
                }

                matches.Add(i);
            }
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"Variable '{variableName}' not found in declaration.");
        }

        if (matches.Count > 1)
        {
            var lineNumbers = string.Join(", ", matches.Select(m => m + 1));
            throw new InvalidOperationException(
                $"Variable '{variableName}' appears on multiple lines ({lineNumbers}); cannot disambiguate.");
        }

        return string.Join('\n', lines.Where((_, index) => index != matches[0]));
    }

    private static readonly Regex s_anyHeader = new(
        @"^(VAR_INPUT|VAR_OUTPUT|VAR_IN_OUT|VAR_TEMP|VAR_PERSISTENT|VAR\s+CONSTANT|VAR)(\s|$)",
        RegexOptions.Compiled);

    private static string ScopePattern(string scopeName) => scopeName.Trim().ToUpperInvariant() switch
    {
        "VAR_INPUT" => "^VAR_INPUT$",
        "VAR_OUTPUT" => "^VAR_OUTPUT$",
        "VAR_IN_OUT" => "^VAR_IN_OUT$",
        "VAR_TEMP" => "^VAR_TEMP$",
        "VAR_PERSISTENT" => "^VAR_PERSISTENT$",
        "VAR" => "^VAR$",
        "VAR CONSTANT" => @"^VAR\s+CONSTANT$",
        _ => throw new ArgumentException(
            $"Unknown scope '{scopeName}'. Use VAR_INPUT, VAR_OUTPUT, VAR_IN_OUT, VAR, VAR_PERSISTENT, VAR_TEMP, or 'VAR CONSTANT'."),
    };

    private static int ScopeRank(string scopeName) => scopeName.Trim().ToUpperInvariant() switch
    {
        "VAR_INPUT" => 1,
        "VAR_OUTPUT" => 2,
        "VAR_IN_OUT" => 3,
        "VAR" => 4,
        "VAR CONSTANT" => 5,
        "VAR_PERSISTENT" => 6,
        "VAR_TEMP" => 7,
        _ => 99,
    };
}
