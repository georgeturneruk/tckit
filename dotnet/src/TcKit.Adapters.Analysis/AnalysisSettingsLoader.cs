using System.Text.RegularExpressions;
using TcKit.Core.Analysis;
using TcKit.Core.Models;

namespace TcKit.Adapters.Analysis;

/// <summary>
/// Loads analysis configuration from the project's own <c>.editorconfig</c> (ADR-0017). Using the
/// real file rather than inventing a format buys ancestor-walk discovery, <c>root = true</c>
/// semantics and per-folder glob overrides for nothing, and the
/// <c>[*.{TcPOU,TcGVL,TcDUT}]</c> section keeps our keys away from .NET tooling.
///
/// The schema is Roslyn's three-part split, so a style is defined once and reused by any number
/// of rules:
/// <code>
/// tckit_analysis_profile = hybrid
/// tckit_naming_symbols.globals.applicable_kinds = variable
/// tckit_naming_symbols.globals.applicable_sections = var_global
/// tckit_naming_style.pascal.capitalization = pascal_case
/// tckit_naming_rule.globals_pascal.symbols = globals
/// tckit_naming_rule.globals_pascal.style = pascal
/// tckit_naming_rule.globals_pascal.severity = warning
/// </code>
/// </summary>
public static partial class AnalysisSettingsLoader
{
    private static readonly string[] TargetFiles = ["a.TcPOU", "a.TcGVL", "a.TcDUT"];

    /// <summary>
    /// Walk up from <paramref name="startDirectory"/> collecting <c>.editorconfig</c> files, nearest
    /// last so it wins, stopping after one that declares <c>root = true</c>.
    /// </summary>
    public static AnalysisSettings Load(string startDirectory)
    {
        ArgumentNullException.ThrowIfNull(startDirectory);

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();

        var directory = Directory.Exists(startDirectory)
            ? new DirectoryInfo(startDirectory)
            : new FileInfo(startDirectory).Directory;

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".editorconfig");
            if (File.Exists(candidate))
            {
                files.Add(candidate);
                if (DeclaresRoot(candidate))
                {
                    break;
                }
            }

            directory = directory.Parent;
        }

        // Collected nearest-first; apply outermost-first so the nearest file overrides.
        files.Reverse();
        foreach (var file in files)
        {
            foreach (var (key, value) in ReadProperties(file))
            {
                properties[key] = value;
            }
        }

        return FromProperties(properties);
    }

    /// <summary>Build settings from an already-flattened property set. The unit-testable seam.</summary>
    public static AnalysisSettings FromProperties(IReadOnlyDictionary<string, string> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        var warnings = new List<string>();
        var profile = properties.TryGetValue("tckit_analysis_profile", out var named)
            ? named.Trim().ToLowerInvariant()
            : NamingProfiles.Hybrid;

        if (!NamingProfiles.Names.Contains(profile))
        {
            warnings.Add(
                $"Unknown tckit_analysis_profile '{profile}'. Using '{NamingProfiles.Hybrid}'. "
                + $"Valid values: {string.Join(", ", NamingProfiles.Names)}.");
            profile = NamingProfiles.Hybrid;
        }

        var groups = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var styles = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var rules = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var ruleSeverities = new Dictionary<string, DiagnosticSeverity>(StringComparer.OrdinalIgnoreCase);
        var categorySeverities = new Dictionary<string, DiagnosticSeverity>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in properties)
        {
            var entity = EntityKey().Match(key);
            if (entity.Success)
            {
                var bucket = entity.Groups["kind"].Value.ToLowerInvariant() switch
                {
                    "tckit_naming_symbols" => groups,
                    "tckit_naming_style" => styles,
                    _ => rules,
                };

                if (!bucket.TryGetValue(entity.Groups["name"].Value, out var settings))
                {
                    settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    bucket[entity.Groups["name"].Value] = settings;
                }

                settings[entity.Groups["prop"].Value] = value.Trim();
                continue;
            }

            var diagnostic = DiagnosticKey().Match(key);
            if (diagnostic.Success && TryParseSeverity(value, out var severity))
            {
                ruleSeverities[diagnostic.Groups["id"].Value] = severity;
                continue;
            }

            var category = CategoryKey().Match(key);
            if (category.Success && TryParseSeverity(value, out var categorySeverity))
            {
                categorySeverities[category.Groups["name"].Value] = categorySeverity;
            }
        }

        var custom = BuildRules(groups, styles, rules, warnings);

        // Project rules come first so an equally specific project rule beats the shipped one.
        var combined = custom
            .Concat(NamingProfiles.For(profile))
            .OrderByDescending(rule => rule.Symbols.Specificity)
            .ToList();

        return new AnalysisSettings
        {
            Profile = profile,
            Rules = combined,
            RuleSeverities = ruleSeverities,
            CategorySeverities = categorySeverities,
            ConfigWarnings = warnings,
        };
    }

    private static List<NamingRule> BuildRules(
        Dictionary<string, Dictionary<string, string>> groups,
        Dictionary<string, Dictionary<string, string>> styles,
        Dictionary<string, Dictionary<string, string>> rules,
        List<string> warnings)
    {
        var result = new List<NamingRule>();

        foreach (var (name, settings) in rules)
        {
            if (!settings.TryGetValue("symbols", out var groupName)
                || !groups.TryGetValue(groupName, out var groupSettings))
            {
                warnings.Add($"tckit_naming_rule.{name} references undefined symbols group "
                    + $"'{settings.GetValueOrDefault("symbols", "")}'; rule skipped.");
                continue;
            }

            if (!settings.TryGetValue("style", out var styleName)
                || !styles.TryGetValue(styleName, out var styleSettings))
            {
                warnings.Add($"tckit_naming_rule.{name} references undefined style "
                    + $"'{settings.GetValueOrDefault("style", "")}'; rule skipped.");
                continue;
            }

            var severity = DiagnosticSeverity.Suggestion;
            if (settings.TryGetValue("severity", out var text) && !TryParseSeverity(text, out severity))
            {
                warnings.Add($"tckit_naming_rule.{name}.severity '{text}' is not a severity; using suggestion.");
                severity = DiagnosticSeverity.Suggestion;
            }

            result.Add(new NamingRule
            {
                Name = name,
                Symbols = BuildGroup(groupName, groupSettings),
                Style = BuildStyle(styleName, styleSettings),
                Severity = severity,
            });
        }

        return result;
    }

    private static SymbolGroup BuildGroup(string name, Dictionary<string, string> settings)
    {
        var sections = ParseList<VarSection>(settings.GetValueOrDefault("applicable_sections"));
        var modifiers = VarQualifiers.None;
        foreach (var qualifier in ParseList<VarQualifiers>(settings.GetValueOrDefault("required_modifiers")))
        {
            modifiers |= qualifier;
        }

        // "var_constant" is accepted as a section for readability; it means VAR plus the modifier.
        if ((settings.GetValueOrDefault("applicable_sections") ?? "")
            .Contains("var_constant", StringComparison.OrdinalIgnoreCase))
        {
            sections.Add(VarSection.Var);
            modifiers |= VarQualifiers.Constant;
        }

        return new SymbolGroup
        {
            Name = name,
            Kinds = ParseList<SymbolKind>(settings.GetValueOrDefault("applicable_kinds")),
            Sections = sections.Distinct().ToList(),
            Accessibilities = ParseList<StAccessibility>(settings.GetValueOrDefault("applicable_accessibilities")),
            Types = ParseList<TypeClass>(settings.GetValueOrDefault("applicable_types")),
            Scopes = ParseList<SymbolScope>(settings.GetValueOrDefault("applicable_scopes")),
            RequiredModifiers = modifiers,
        };
    }

    private static NamingStyle BuildStyle(string name, Dictionary<string, string> settings)
    {
        var capitalisation = Capitalisation.Any;
        if (settings.TryGetValue("capitalization", out var text))
        {
            capitalisation = ParseEnum<Capitalisation>(text) ?? Capitalisation.Any;
        }

        return new NamingStyle
        {
            Name = name,
            Capitalisation = capitalisation,
            RequiredPrefix = settings.GetValueOrDefault("required_prefix", ""),
            RequiredSuffix = settings.GetValueOrDefault("required_suffix", ""),
            WordSeparator = settings.GetValueOrDefault("word_separator", ""),
        };
    }

    private static List<T> ParseList<T>(string? value)
        where T : struct, Enum
    {
        var result = new List<T>();
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "*")
        {
            return result;
        }

        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ParseEnum<T>(token) is { } parsed)
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    // Config uses snake_case; the enums are PascalCase. Dropping underscores makes the
    // case-insensitive parse line up without a hand-written mapping per enum.
    private static T? ParseEnum<T>(string value)
        where T : struct, Enum
        => Enum.TryParse<T>(value.Replace("_", "", StringComparison.Ordinal), ignoreCase: true, out var parsed)
            ? parsed
            : null;

    private static bool TryParseSeverity(string value, out DiagnosticSeverity severity)
    {
        var parsed = ParseEnum<DiagnosticSeverity>(value.Trim());
        severity = parsed ?? DiagnosticSeverity.Suggestion;
        return parsed is not null;
    }

    private static bool DeclaresRoot(string path)
    {
        foreach (var line in SafeReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
            {
                // root only counts in the preamble, before the first section.
                return false;
            }

            if (RootDeclaration().IsMatch(trimmed))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Read the key/value pairs from sections that apply to TwinCAT source files.</summary>
    private static List<KeyValuePair<string, string>> ReadProperties(string path)
    {
        var result = new List<KeyValuePair<string, string>>();
        var applies = false;

        foreach (var line in SafeReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                applies = SectionApplies(trimmed[1..^1]);
                continue;
            }

            var separator = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (applies && separator > 0)
            {
                result.Add(new KeyValuePair<string, string>(
                    trimmed[..separator].Trim(), trimmed[(separator + 1)..].Trim()));
            }
        }

        return result;
    }

    private static bool SectionApplies(string glob)
    {
        var pattern = GlobToRegex(glob);
        return TargetFiles.Any(file => Regex.IsMatch(file, pattern, RegexOptions.IgnoreCase));
    }

    /// <summary>
    /// Translate the subset of editorconfig globs that matter here: <c>*</c>, <c>**</c>, <c>?</c>,
    /// and <c>{a,b}</c> alternation. Anything more exotic simply will not match, which fails safe.
    /// </summary>
    private static string GlobToRegex(string glob)
    {
        var builder = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var character = glob[i];
            switch (character)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    builder.Append(".*");
                    i++;
                    break;
                case '*':
                    builder.Append("[^/]*");
                    break;
                case '?':
                    builder.Append('.');
                    break;
                case '{':
                    builder.Append("(?:");
                    break;
                case '}':
                    builder.Append(')');
                    break;
                case ',':
                    builder.Append('|');
                    break;
                default:
                    builder.Append(Regex.Escape(character.ToString()));
                    break;
            }
        }

        return builder.Append('$').ToString();
    }

    private static string[] SafeReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    [GeneratedRegex(
        @"^(?<kind>tckit_naming_symbols|tckit_naming_style|tckit_naming_rule)\.(?<name>[^.]+)\.(?<prop>.+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex EntityKey();

    [GeneratedRegex(@"^tckit_diagnostic\.(?<id>[^.]+)\.severity$", RegexOptions.IgnoreCase)]
    private static partial Regex DiagnosticKey();

    [GeneratedRegex(@"^tckit_analyzer_diagnostic\.category-(?<name>[^.]+)\.severity$", RegexOptions.IgnoreCase)]
    private static partial Regex CategoryKey();

    [GeneratedRegex(@"^root\s*=\s*true$", RegexOptions.IgnoreCase)]
    private static partial Regex RootDeclaration();
}
