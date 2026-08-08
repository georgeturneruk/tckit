using TcKit.Core.Models;

namespace TcKit.Adapters.Analysis;

/// <summary>
/// Applies the configured naming rules to a symbol list. One rule wins per symbol: rules arrive
/// sorted most specific first, so the first match is the most constrained one that applies, which
/// is how Roslyn and typescript-eslint both remove config ordering from the picture.
/// </summary>
public static class NamingRuleEngine
{
    /// <summary>The naming category, used for <c>tckit_analyzer_diagnostic.category-naming.severity</c>.</summary>
    public const string Category = "naming";

    /// <summary>Object names: POUs, DUTs and GVLs.</summary>
    public const string ObjectRuleId = "TCK1001";

    /// <summary>Variable names in any VAR block.</summary>
    public const string VariableRuleId = "TCK1002";

    /// <summary>Method, property and action names.</summary>
    public const string MemberRuleId = "TCK1003";

    /// <summary>Struct fields and enumeration constants.</summary>
    public const string TypeMemberRuleId = "TCK1004";

    /// <summary>
    /// Names TwinCAT itself mandates. Flagging <c>MAIN</c> would be advising a rename that breaks
    /// the project, which is worse than saying nothing.
    /// </summary>
    private static readonly HashSet<string> ReservedNames =
        new(StringComparer.OrdinalIgnoreCase) { "MAIN" };

    /// <summary>Check every symbol and return one finding per non-conforming name.</summary>
    public static List<AnalysisFinding> Run(IEnumerable<NamedSymbol> symbols, AnalysisSettings settings)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(settings);

        var findings = new List<AnalysisFinding>();
        foreach (var symbol in symbols)
        {
            if (ReservedNames.Contains(symbol.Name))
            {
                continue;
            }

            var rule = settings.Rules.FirstOrDefault(candidate => candidate.Symbols.Matches(symbol));
            if (rule is null || NameChecker.Conforms(symbol.Name, rule.Style))
            {
                continue;
            }

            var ruleId = RuleIdFor(symbol.Kind);
            var severity = settings.SeverityFor(ruleId, Category, rule.Severity);
            if (severity is DiagnosticSeverity.None)
            {
                continue;
            }

            var suggestion = NameChecker.Suggest(symbol.Name, rule.Style, symbol.TypeClass);
            findings.Add(new AnalysisFinding
            {
                RuleId = ruleId,
                Category = Category,
                Severity = severity,
                Message = $"{Label(symbol.Kind)} '{symbol.Name}' does not follow the '{rule.Name}' "
                    + $"convention ({Describe(rule.Style)}).",
                PlcName = symbol.PlcName,
                ObjectName = symbol.ObjectName,
                ItemName = symbol.ItemName,
                Part = CodePart.Declaration,
                Line = symbol.Line,
                Symbol = symbol.Name,
                Suggestion = string.Equals(suggestion, symbol.Name, StringComparison.Ordinal) ? "" : suggestion,
            });
        }

        return findings;
    }

    private static string RuleIdFor(SymbolKind kind) => kind switch
    {
        SymbolKind.Variable => VariableRuleId,
        SymbolKind.Method or SymbolKind.Property or SymbolKind.Action => MemberRuleId,
        SymbolKind.StructMember or SymbolKind.EnumMember => TypeMemberRuleId,
        _ => ObjectRuleId,
    };

    private static string Label(SymbolKind kind) => kind switch
    {
        SymbolKind.FunctionBlock => "Function block",
        SymbolKind.Function => "Function",
        SymbolKind.Program => "Program",
        SymbolKind.Interface => "Interface",
        SymbolKind.Struct => "Struct",
        SymbolKind.Union => "Union",
        SymbolKind.Enum => "Enum",
        SymbolKind.Alias => "Alias",
        SymbolKind.Gvl => "GVL",
        SymbolKind.Method => "Method",
        SymbolKind.Property => "Property",
        SymbolKind.Action => "Action",
        SymbolKind.StructMember => "Struct member",
        SymbolKind.EnumMember => "Enum constant",
        _ => "Variable",
    };

    private static string Describe(NamingStyle style)
    {
        var casing = style.Capitalisation switch
        {
            Capitalisation.PascalCase => "PascalCase",
            Capitalisation.CamelCase => "camelCase",
            Capitalisation.AllUpper => "UPPER_CASE",
            Capitalisation.AllLower => "lower_case",
            Capitalisation.FirstWordUpper => "First word capitalised",
            _ => "any capitalisation",
        };

        var parts = new List<string> { casing };
        if (style.RequiredPrefix.Length > 0)
        {
            parts.Add($"prefix '{style.RequiredPrefix}'");
        }

        if (style.RequiredSuffix.Length > 0)
        {
            parts.Add($"suffix '{style.RequiredSuffix}'");
        }

        return string.Join(", ", parts);
    }
}
