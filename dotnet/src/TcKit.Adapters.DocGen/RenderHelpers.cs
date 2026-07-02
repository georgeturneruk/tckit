using System.Text;

namespace TcKit.Adapters.DocGen;

/// <summary>Rendering primitives shared by the HTML and Markdown renderers, matching the Jinja2 filters the Python templates relied on.</summary>
internal static class RenderHelpers
{
    /// <summary>HTML-escape a string with the same substitutions as markupsafe (Jinja2 autoescape).</summary>
    internal static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&#34;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Port of Jinja2's <c>truncate</c> filter (killwords=false, end="...", leeway=5).</summary>
    internal static string Truncate(string text, int length, string end = "...", int leeway = 5)
    {
        if (text.Length <= length + leeway)
        {
            return text;
        }

        var sub = text[..(length - end.Length)];
        var idx = sub.LastIndexOf(' ');
        var result = idx >= 0 ? sub[..idx] : sub;
        return result + end;
    }

    /// <summary>Port of Python's <c>str.title()</c>: capitalise the first letter of each word, lower-casing the rest.</summary>
    internal static string Title(string text)
    {
        var chars = text.ToCharArray();
        var startOfWord = true;
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsLetter(chars[i]))
            {
                chars[i] = startOfWord ? char.ToUpperInvariant(chars[i]) : char.ToLowerInvariant(chars[i]);
                startOfWord = false;
            }
            else
            {
                startOfWord = true;
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// Render a variable/return type as a cross-reference link when its base type is a known object,
    /// preserving the surrounding type expression. Returns ready-to-emit (already escaped) HTML.
    /// </summary>
    internal static string LinkType(string typeStr, IReadOnlySet<string> knownNames)
    {
        if (string.IsNullOrEmpty(typeStr))
        {
            return "";
        }

        var baseName = DocModel.BaseTypeName(typeStr);
        if (!knownNames.Contains(baseName))
        {
            return Escape(typeStr);
        }

        var escaped = Escape(typeStr);
        var escapedBase = Escape(baseName);
        var anchor = $"<a href=\"{baseName}.html\">{baseName}</a>";
        var pos = escaped.IndexOf(escapedBase, StringComparison.Ordinal);
        return pos < 0 ? escaped : escaped[..pos] + anchor + escaped[(pos + escapedBase.Length)..];
    }
}
