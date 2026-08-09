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

    /// <summary>A Hungarian type prefix under a profile that does not ask for one.</summary>
    public const string RedundantTypePrefixId = "TCK1005";

    /// <summary>
    /// Names TwinCAT itself mandates. Flagging <c>MAIN</c> would be advising a rename that breaks
    /// the project, which is worse than saying nothing. Inference excludes them too, so a single
    /// reserved name cannot skew a small sample.
    /// </summary>
    private static readonly HashSet<string> ReservedNames =
        new(StringComparer.OrdinalIgnoreCase) { "MAIN" };

    /// <summary>
    /// Members whose name and whose parameter names TwinCAT fixes. <c>FB_init</c> is declared as
    /// <c>FB_init(bInitRetains : BOOL, bInCopyCode : BOOL)</c> and the compiler matches on those
    /// names, so a naming finding anywhere inside one is advice that breaks the build.
    /// </summary>
    private static readonly HashSet<string> ReservedMembers =
        new(StringComparer.OrdinalIgnoreCase) { "FB_init", "FB_exit", "FB_reinit" };

    /// <summary>Whether TwinCAT mandates this name, making any rule about it unactionable.</summary>
    public static bool IsReserved(string name) => ReservedNames.Contains(name);

    /// <summary>Whether this symbol sits in, or is, a member whose naming TwinCAT fixes.</summary>
    public static bool IsReservedMember(string itemName)
        => itemName.Length > 0 && ReservedMembers.Contains(itemName);

    /// <summary>Check every symbol and return one finding per non-conforming name.</summary>
    public static List<AnalysisFinding> Run(IEnumerable<NamedSymbol> symbols, AnalysisSettings settings)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(settings);

        var findings = new List<AnalysisFinding>();
        foreach (var symbol in symbols)
        {
            if (IsReserved(symbol.Name) || IsReservedMember(symbol.ItemName))
            {
                continue;
            }

            var rule = settings.Rules.FirstOrDefault(candidate => candidate.Symbols.Matches(symbol));
            if (rule is null)
            {
                continue;
            }

            if (NameChecker.Conforms(symbol.Name, rule.Style))
            {
                // Only a name that already satisfies the casing rule can be hiding a type prefix.
                // A name that fails it is reported once, by the rule below, with the same fix.
                AddRedundantTypePrefix(findings, settings, symbol, rule);
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

    /// <summary>
    /// TCK1005. A Hungarian type prefix is invisible to the casing rules, because "nCount" is
    /// already valid camelCase, so a profile that has dropped type prefixes would never notice one
    /// left behind. Firing only when the prefix agrees with the declared type is what keeps this
    /// precise: "nextValue" on an INT is a word, "nValue" on an INT is a tag.
    /// </summary>
    private static void AddRedundantTypePrefix(
        List<AnalysisFinding> findings, AnalysisSettings settings, NamedSymbol symbol, NamingRule rule)
    {
        if (symbol.Kind is not (SymbolKind.Variable or SymbolKind.StructMember)
            || symbol.TypeClass is TypeClass.Unknown)
        {
            return;
        }

        // The style's own prefix is stripped first, so "_nCount" is judged on "nCount".
        var core = symbol.Name.StartsWith(rule.Style.RequiredPrefix, StringComparison.Ordinal)
            ? symbol.Name[rule.Style.RequiredPrefix.Length..]
            : symbol.Name.TrimStart('_');

        var typePrefix = NameChecker.TypePrefixOn(core, symbol.TypeClass);

        // Under a profile that asks for the prefix, carrying it is the convention, not a defect.
        if (typePrefix.Length == 0
            || rule.Style.RequiredPrefix.EndsWith(typePrefix, StringComparison.Ordinal))
        {
            return;
        }

        var severity = settings.SeverityFor(RedundantTypePrefixId, Category, DiagnosticSeverity.Suggestion);
        if (severity is DiagnosticSeverity.None)
        {
            return;
        }

        var suggestion = NameChecker.Suggest(symbol.Name, rule.Style, symbol.TypeClass);
        findings.Add(new AnalysisFinding
        {
            RuleId = RedundantTypePrefixId,
            Category = Category,
            Severity = severity,
            Message = $"{Label(symbol.Kind)} '{symbol.Name}' carries the type prefix "
                + $"'{typePrefix}', which the '{rule.Name}' convention does not use. The type is "
                + $"already declared as {symbol.TypeExpression}.",
            PlcName = symbol.PlcName,
            ObjectName = symbol.ObjectName,
            ItemName = symbol.ItemName,
            Part = CodePart.Declaration,
            Line = symbol.Line,
            Symbol = symbol.Name,
            Suggestion = string.Equals(suggestion, symbol.Name, StringComparison.Ordinal) ? "" : suggestion,
        });
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
