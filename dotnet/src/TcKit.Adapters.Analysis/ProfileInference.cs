using System.Text.RegularExpressions;
using TcKit.Core.Analysis;
using TcKit.Core.Models;

namespace TcKit.Adapters.Analysis;

/// <summary>
/// Derives a naming convention from the project's own declarations instead of imposing one
/// (the <c>infer</c> profile, ADR-0017). This is the honest answer for a brownfield project whose
/// house style matches none of the shipped profiles: it reports departures from what the project
/// already does, so adopting it does not produce thousands of findings on day one.
///
/// Nothing is inferred without enough evidence. Below the sample and agreement thresholds a slot
/// simply gets no rule, so the analyser stays quiet rather than enforcing a guess.
/// </summary>
public static partial class ProfileInference
{
    /// <summary>Fewest declarations in a slot before anything is inferred from it.</summary>
    public const int MinimumSamples = 3;

    /// <summary>Share of a slot that must agree before a candidate is adopted.</summary>
    public const double MinimumAgreement = 0.6;

    private sealed record Slot(string Name, SymbolGroup Group);

    /// <summary>Build rules from the observed declarations, most specific first.</summary>
    public static IReadOnlyList<NamingRule> Infer(IReadOnlyList<NamedSymbol> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        var candidates = symbols
            .Where(symbol => !NamingRuleEngine.IsReserved(symbol.Name))
            .ToList();

        var typePrefixes = InferTypePrefixes(candidates);
        var rules = new List<NamingRule>();

        foreach (var slot in ObjectSlots())
        {
            var names = candidates.Where(slot.Group.Matches).Select(symbol => symbol.Name).ToList();
            if (InferStyle(names, ObjectPrefix, slot.Name) is { } style)
            {
                rules.Add(new NamingRule
                {
                    Name = $"inferred_{slot.Name}",
                    Symbols = slot.Group,
                    Style = style,
                    Severity = DiagnosticSeverity.Suggestion,
                });
            }
        }

        foreach (var slot in VariableSlots())
        {
            var members = candidates.Where(slot.Group.Matches).ToList();
            if (members.Count == 0)
            {
                continue;
            }

            // Type prefixes are consistent across sections in a Hungarian project, while
            // capitalisation and any scope prefix vary by section. Inferring them separately stops
            // the sample being fragmented across the section-by-type cross product.
            var stripped = members
                .Select(symbol => StripInferred(symbol, typePrefixes))
                .ToList();

            if (InferStyle(stripped, ScopePrefix, slot.Name) is not { } style)
            {
                continue;
            }

            foreach (var typeClass in members.Select(symbol => symbol.TypeClass).Distinct())
            {
                var typePrefix = typePrefixes.GetValueOrDefault(typeClass, "");
                rules.Add(new NamingRule
                {
                    Name = $"inferred_{slot.Name}_{typeClass.ToString().ToLowerInvariant()}",
                    Symbols = slot.Group with
                    {
                        Name = $"{slot.Group.Name}_{typeClass}",
                        Types = [typeClass],
                    },
                    Style = style with { RequiredPrefix = style.RequiredPrefix + typePrefix },
                    Severity = DiagnosticSeverity.Suggestion,
                });
            }
        }

        return rules.OrderByDescending(rule => rule.Symbols.Specificity).ToList();
    }

    /// <summary>
    /// The dominant Hungarian prefix per type family, e.g. <c>b</c> for BOOL. Empty when the
    /// project does not use type prefixes, which is the normal case for a modern codebase.
    /// </summary>
    private static Dictionary<TypeClass, string> InferTypePrefixes(IReadOnlyList<NamedSymbol> symbols)
    {
        var result = new Dictionary<TypeClass, string>();
        var byType = symbols
            .Where(symbol => symbol.Kind is SymbolKind.Variable or SymbolKind.StructMember
                && symbol.TypeClass is not TypeClass.Unknown)
            .GroupBy(symbol => symbol.TypeClass);

        foreach (var group in byType)
        {
            var names = group.Select(symbol => symbol.Name.TrimStart('_')).ToList();
            var dominant = Dominant(names.Select(TypePrefix).ToList());
            if (dominant is { Length: > 0 })
            {
                result[group.Key] = dominant;
            }
        }

        return result;
    }

    private static string StripInferred(NamedSymbol symbol, Dictionary<TypeClass, string> typePrefixes)
    {
        var prefix = typePrefixes.GetValueOrDefault(symbol.TypeClass, "");
        var name = symbol.Name;
        var leading = name.Length - name.TrimStart('_').Length;

        return prefix.Length > 0
            && name.Length > leading + prefix.Length
            && name.AsSpan(leading).StartsWith(prefix, StringComparison.Ordinal)
                ? name[..leading] + name[(leading + prefix.Length)..]
                : name;
    }

    /// <summary>
    /// Infer a style from a set of names: the dominant prefix, then the dominant capitalisation of
    /// what remains. Returns null when the slot is too small, too inconsistent, or yields a style
    /// that nothing could fail.
    /// </summary>
    private static NamingStyle? InferStyle(
        IReadOnlyList<string> names, Func<string, string> prefixOf, string slotName)
    {
        if (names.Count < MinimumSamples)
        {
            return null;
        }

        var prefix = Dominant(names.Select(prefixOf).ToList()) ?? "";
        var cores = names
            .Select(name => name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name)
            .Where(core => core.Length > 0)
            .ToList();

        var casing = Dominant(cores.Select(Classify).ToList()) is { } dominant
            ? Enum.Parse<Capitalisation>(dominant)
            : Capitalisation.Any;

        // A style with no prefix and no capitalisation requirement cannot be violated, so emitting
        // it would only be noise in the rule list.
        return prefix.Length == 0 && casing is Capitalisation.Any
            ? null
            : new NamingStyle
            {
                Name = $"inferred_{slotName}",
                Capitalisation = casing,
                RequiredPrefix = prefix,
            };
    }

    /// <summary>The value shared by enough of the sample to be treated as the convention, or null.</summary>
    private static string? Dominant(IReadOnlyList<string> values)
    {
        if (values.Count < MinimumSamples)
        {
            return null;
        }

        var top = values
            .GroupBy(value => value, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .First();

        return top.Count() / (double)values.Count >= MinimumAgreement ? top.Key : null;
    }

    private static string Classify(string core)
    {
        if (core.All(character => !char.IsLower(character)) && core.Any(char.IsUpper))
        {
            return nameof(Capitalisation.AllUpper);
        }

        return char.IsUpper(core[0])
            ? nameof(Capitalisation.PascalCase)
            : nameof(Capitalisation.CamelCase);
    }

    private static string ObjectPrefix(string name)
    {
        var match = ObjectPrefixPattern().Match(name);
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string TypePrefix(string name)
    {
        var match = TypePrefixPattern().Match(name);
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string ScopePrefix(string name)
        => name.StartsWith('_') ? "_" : "";

    private static IEnumerable<Slot> ObjectSlots()
    {
        yield return Kind("function_block", SymbolKind.FunctionBlock);
        yield return Kind("function", SymbolKind.Function);
        yield return Kind("program", SymbolKind.Program);
        yield return Kind("interface", SymbolKind.Interface);
        yield return Kind("struct", SymbolKind.Struct);
        yield return Kind("union", SymbolKind.Union);
        yield return Kind("enum", SymbolKind.Enum);
        yield return Kind("gvl", SymbolKind.Gvl);
        yield return Kind("member", SymbolKind.Method, SymbolKind.Property, SymbolKind.Action);
        yield return Kind("struct_member", SymbolKind.StructMember);
        yield return Kind("enum_member", SymbolKind.EnumMember);
    }

    private static IEnumerable<Slot> VariableSlots()
    {
        yield return Variables("fb_interface", SymbolScope.Object,
            VarSection.VarInput, VarSection.VarOutput, VarSection.VarInOut);
        yield return Variables("parameter", SymbolScope.Member,
            VarSection.VarInput, VarSection.VarOutput, VarSection.VarInOut);
        yield return Variables("global", null, VarSection.VarGlobal);
        yield return Variables("instance_field", SymbolScope.Object, VarSection.Var, VarSection.VarStat);
        yield return Variables("local", SymbolScope.Member,
            VarSection.Var, VarSection.VarTemp, VarSection.VarInst);
    }

    private static Slot Kind(string name, params SymbolKind[] kinds)
        => new(name, new SymbolGroup { Name = name, Kinds = kinds });

    private static Slot Variables(string name, SymbolScope? scope, params VarSection[] sections)
        => new(name, new SymbolGroup
        {
            Name = name,
            Kinds = [SymbolKind.Variable],
            Sections = sections,
            Scopes = scope is null ? [] : [scope.Value],
        });

    [GeneratedRegex(@"^([A-Za-z]{1,4}_)")]
    private static partial Regex ObjectPrefixPattern();

    [GeneratedRegex(@"^([a-z]{1,4})(?=[A-Z])")]
    private static partial Regex TypePrefixPattern();
}
