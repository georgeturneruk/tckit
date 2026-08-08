namespace TcKit.Core.Analysis;

/// <summary>
/// Position-preserving masking of Structured Text source. Comments, string literals and
/// pragmas are replaced by spaces of equal length, with newlines kept, so a parser can match
/// against the mask while still slicing identifiers out of the original text and computing
/// line numbers that agree with what the user sees in the editor.
///
/// This is the substitute for a full token stream: the naming and correctness rules only need
/// to know which spans are code, not what every token is.
/// </summary>
public static class StSource
{
    /// <summary>
    /// Return a copy of <paramref name="source"/> with comments, string literals and pragmas
    /// blanked to spaces. The result has the same length and the same newline positions.
    /// Unterminated constructs blank to the end of input rather than throwing, because the
    /// analyser has to stay useful on half-written code.
    /// </summary>
    public static string Mask(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var buffer = new char[source.Length];
        var i = 0;
        while (i < source.Length)
        {
            var current = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (current == '/' && next == '/')
            {
                i = BlankToLineEnd(source, buffer, i);
            }
            else if (current == '(' && next == '*')
            {
                // IEC 61131-3 block comments nest, so this cannot be a plain scan for "*)".
                i = BlankNested(source, buffer, i, "(*", "*)");
            }
            else if (current == '/' && next == '*')
            {
                i = BlankNested(source, buffer, i, "/*", "*/");
            }
            else if (current is '\'' or '"')
            {
                i = BlankString(source, buffer, i, current);
            }
            else if (current == '{')
            {
                // Braces are pragma/attribute syntax only; ST uses brackets for arrays and
                // parentheses for struct initialisers, so this cannot swallow real code.
                i = BlankNested(source, buffer, i, "{", "}");
            }
            else
            {
                buffer[i] = current;
                i++;
            }
        }

        return new string(buffer);
    }

    /// <summary>Return the 1-based line number of <paramref name="index"/> within <paramref name="source"/>.</summary>
    public static int LineAt(string source, int index)
    {
        ArgumentNullException.ThrowIfNull(source);

        var line = 1;
        var limit = Math.Min(index, source.Length);
        for (var i = 0; i < limit; i++)
        {
            if (source[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static int BlankToLineEnd(string source, char[] buffer, int start)
    {
        var i = start;
        while (i < source.Length && source[i] != '\n')
        {
            buffer[i] = Blanked(source[i]);
            i++;
        }

        return i;
    }

    private static int BlankNested(string source, char[] buffer, int start, string open, string close)
    {
        var depth = 0;
        var i = start;
        while (i < source.Length)
        {
            if (Matches(source, i, open))
            {
                depth++;
                BlankRun(source, buffer, i, open.Length);
                i += open.Length;
                continue;
            }

            if (Matches(source, i, close))
            {
                depth--;
                BlankRun(source, buffer, i, close.Length);
                i += close.Length;
                if (depth <= 0)
                {
                    return i;
                }

                continue;
            }

            buffer[i] = Blanked(source[i]);
            i++;
        }

        return i;
    }

    private static int BlankString(string source, char[] buffer, int start, char quote)
    {
        var i = start;
        buffer[i] = ' ';
        i++;
        while (i < source.Length)
        {
            var current = source[i];
            buffer[i] = Blanked(current);
            i++;

            // ST escapes with '$', not a backslash, so "$'" must not close the literal.
            if (current == '$' && i < source.Length)
            {
                buffer[i] = Blanked(source[i]);
                i++;
                continue;
            }

            if (current == quote)
            {
                return i;
            }
        }

        return i;
    }

    private static void BlankRun(string source, char[] buffer, int start, int length)
    {
        for (var offset = 0; offset < length && start + offset < source.Length; offset++)
        {
            buffer[start + offset] = Blanked(source[start + offset]);
        }
    }

    private static bool Matches(string source, int index, string token)
        => index + token.Length <= source.Length
            && string.CompareOrdinal(source, index, token, 0, token.Length) == 0;

    private static char Blanked(char value) => value is '\n' or '\r' ? value : ' ';
}
