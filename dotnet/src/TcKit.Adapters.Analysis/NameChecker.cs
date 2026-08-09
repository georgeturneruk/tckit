using System.Text;

namespace TcKit.Adapters.Analysis;

/// <summary>
/// Decides whether an identifier conforms to a <see cref="NamingStyle"/>, and derives a
/// conforming alternative to offer alongside a finding.
///
/// Suggestions are advisory only. Nothing in v1 rewrites code (ADR-0017): a rename on a
/// referenced symbol is exactly what the tc-write-st rename guard reserves for the user.
/// </summary>
public static class NameChecker
{
    /// <summary>Whether <paramref name="name"/> satisfies <paramref name="style"/> exactly.</summary>
    public static bool Conforms(string name, NamingStyle style)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(style);

        if (style.RequiredPrefix.Length > 0
            && !name.StartsWith(style.RequiredPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (style.RequiredSuffix.Length > 0
            && !name.EndsWith(style.RequiredSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var core = name[style.RequiredPrefix.Length..];
        core = core[..(core.Length - style.RequiredSuffix.Length)];
        if (core.Length == 0)
        {
            return false;
        }

        // Underscores are only allowed inside the core when the style asks for them.
        if (style.WordSeparator.Length == 0
            && style.Capitalisation is Capitalisation.PascalCase or Capitalisation.CamelCase
            && core.Contains('_', StringComparison.Ordinal))
        {
            return false;
        }

        return style.Capitalisation switch
        {
            Capitalisation.PascalCase => char.IsUpper(core[0]),
            Capitalisation.FirstWordUpper => char.IsUpper(core[0]),
            Capitalisation.CamelCase => char.IsLower(core[0]),
            Capitalisation.AllUpper => !core.Any(char.IsLower),
            Capitalisation.AllLower => !core.Any(char.IsUpper),
            _ => true,
        };
    }

    /// <summary>
    /// Derive a name that would satisfy <paramref name="style"/>, preserving the words already in
    /// <paramref name="name"/>. Returns <paramref name="name"/> unchanged when nothing usable is left.
    /// <paramref name="typeClass"/> is the declared type's family, which gates type-prefix removal.
    /// </summary>
    public static string Suggest(string name, NamingStyle style, TypeClass typeClass = TypeClass.Unknown)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(style);

        var core = StripRequiredPrefix(name, style.RequiredPrefix);
        core = core.TrimStart('_');
        core = StripObjectPrefix(core);
        core = StripTypePrefix(core, typeClass);

        if (style.RequiredSuffix.Length > 0
            && core.EndsWith(style.RequiredSuffix, StringComparison.OrdinalIgnoreCase))
        {
            core = core[..^style.RequiredSuffix.Length];
        }

        var words = SplitWords(core);
        return words.Count == 0
            ? name
            : style.RequiredPrefix + Recase(words, style.Capitalisation, style.WordSeparator) + style.RequiredSuffix;
    }

    /// <summary>
    /// Remove the style's own prefix so it is not doubled up. An exact match always goes; a
    /// case-insensitive match only goes when a prefix boundary follows, since ST is
    /// case-insensitive but "Buffer" should not lose its B to a required prefix of "b".
    /// </summary>
    private static string StripRequiredPrefix(string name, string prefix)
    {
        if (prefix.Length == 0)
        {
            return name;
        }

        if (name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return name[prefix.Length..];
        }

        var boundaryFollows = name.Length > prefix.Length
            && (char.IsUpper(name[prefix.Length]) || name[prefix.Length] == '_');

        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && boundaryFollows
            ? name[prefix.Length..]
            : name;
    }

    /// <summary>
    /// Object kind prefixes. Stripped so a name moving between profiles loses the old prefix
    /// rather than absorbing it: under <c>dotnet</c>, FB_Motor should suggest Motor, not FBMotor.
    /// </summary>
    private static readonly string[] ObjectPrefixes =
        new[] { "GVL_", "PRG_", "FB_", "ST_", "I_", "E_", "U_", "F_", "T_" }
            .OrderByDescending(prefix => prefix.Length)
            .ToArray();

    private static string StripObjectPrefix(string value)
    {
        foreach (var prefix in ObjectPrefixes)
        {
            if (value.Length > prefix.Length
                && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return value[prefix.Length..];
            }
        }

        return value;
    }

    /// <summary>
    /// Remove a Hungarian type prefix, but only when it agrees with the declared type. Agreement is
    /// what makes this safe: "strSuite : FB_StringTests" keeps its "str", because the variable is a
    /// function block rather than a string, so those letters are part of the word and not a type
    /// tag. A type we could not classify never loses a prefix.
    /// </summary>
    /// <summary>
    /// Spellings a project might use for a type prefix beyond the one the hungarian profile
    /// requires, e.g. both "strName" and "sName" for a STRING. Only consulted when stripping for a
    /// suggestion, never when deciding conformance.
    /// </summary>
    private static readonly Dictionary<TypeClass, string[]> AlternatePrefixes = new()
    {
        [TypeClass.String] = ["str"],
        [TypeClass.Array] = ["arr"],
        [TypeClass.Pointer] = ["ptr"],
        [TypeClass.Reference] = ["r"],
        [TypeClass.Real] = ["lr", "r"],
        [TypeClass.Integer] = ["udi", "uli", "di", "ui", "li", "by", "dw", "i", "w", "u"],
    };

    /// <summary>
    /// The Hungarian type prefix <paramref name="value"/> carries for <paramref name="typeClass"/>,
    /// or empty when it carries none. Agreement is the whole test: "nCount" on an INT is a type
    /// prefix, while "next" on an INT is a word that happens to start with n.
    /// </summary>
    public static string TypePrefixOn(string value, TypeClass typeClass)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (typeClass is TypeClass.Unknown)
        {
            return "";
        }

        var canonical = NamingProfiles.TypePrefixes
            .FirstOrDefault(candidate => candidate.Type == typeClass).Prefix;

        var candidates = AlternatePrefixes.TryGetValue(typeClass, out var extras)
            ? extras.Append(canonical)
            : [canonical];

        foreach (var prefix in candidates.Where(p => p is { Length: > 0 }).OrderByDescending(p => p.Length))
        {
            if (value.Length > prefix.Length
                && value.StartsWith(prefix, StringComparison.Ordinal)
                && char.IsUpper(value[prefix.Length]))
            {
                return prefix;
            }
        }

        return "";
    }

    private static string StripTypePrefix(string value, TypeClass typeClass)
    {
        var prefix = TypePrefixOn(value, typeClass);
        return prefix.Length > 0 ? value[prefix.Length..] : value;
    }

    /// <summary>Split an identifier into words on underscores and on case boundaries, keeping acronyms whole.</summary>
    private static List<string> SplitWords(string value)
    {
        var words = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            if (character == '_')
            {
                Flush();
                continue;
            }

            if (char.IsUpper(character) && current.Length > 0)
            {
                var previousIsUpper = char.IsUpper(value[i - 1]);
                var nextIsLower = i + 1 < value.Length && char.IsLower(value[i + 1]);

                // Break at a lower-to-upper boundary, and at the tail of an acronym run so
                // "HTTPServer" splits into "HTTP" and "Server" rather than one word.
                if (!previousIsUpper || nextIsLower)
                {
                    Flush();
                }
            }

            current.Append(character);
        }

        Flush();
        return words;

        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
    }

    private static string Recase(List<string> words, Capitalisation capitalisation, string separator)
        => capitalisation switch
        {
            Capitalisation.AllUpper => string.Join(
                separator.Length > 0 ? separator : "_", words.Select(word => word.ToUpperInvariant())),
            Capitalisation.AllLower => string.Join(
                separator.Length > 0 ? separator : "_", words.Select(word => word.ToLowerInvariant())),
            Capitalisation.CamelCase => Lower(words[0])
                + string.Join(separator, words.Skip(1).Select(Upper)),
            Capitalisation.FirstWordUpper => string.Join(separator, words.Select((word, index) =>
                index == 0 ? Upper(word) : word)),
            _ => string.Join(separator, words.Select(Upper)),
        };

    // Only the first character is recased, so an acronym inside a word survives intact.
    private static string Upper(string word) => char.ToUpperInvariant(word[0]) + word[1..];

    private static string Lower(string word) => char.ToLowerInvariant(word[0]) + word[1..];
}
