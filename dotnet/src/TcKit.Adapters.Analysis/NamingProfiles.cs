using TcKit.Core.Analysis;
using TcKit.Core.Models;

namespace TcKit.Adapters.Analysis;

/// <summary>
/// The naming conventions TcKit ships (ADR-0017). All three express the same schema with different
/// knobs, which is the point: a project picks a house style rather than a different mechanism.
///
/// <c>hybrid</c> is the default. It keeps kind prefixes on program objects, because POUs, DUTs and
/// GVLs share one flat namespace and a bare <c>Config</c> does not say whether it is a struct, an
/// enum or an FB, and it drops type prefixes on variables, which only restate what the declaration
/// already says. <c>dotnet</c> drops the object prefixes too. <c>hungarian</c> is the
/// Beckhoff/CODESYS convention in full.
/// </summary>
public static class NamingProfiles
{
    public const string Hybrid = "hybrid";
    public const string Dotnet = "dotnet";
    public const string Hungarian = "hungarian";
    public const string Infer = "infer";
    public const string None = "none";

    /// <summary>Every profile name accepted by <c>tckit_analysis_profile</c>.</summary>
    public static IReadOnlyList<string> Names { get; } = [Hybrid, Dotnet, Hungarian, Infer, None];

    /// <summary>
    /// The rules for a shipped profile, most specific first so the first match wins.
    /// <see cref="Infer"/> and <see cref="None"/> return empty.
    /// </summary>
    public static IReadOnlyList<NamingRule> For(string profile)
    {
        var rules = profile?.ToLowerInvariant() switch
        {
            Dotnet => Build(objectPrefixes: false, interfacePrefix: "I", hungarianVariables: false),
            Hungarian => Build(objectPrefixes: true, interfacePrefix: "I_", hungarianVariables: true),
            Hybrid => Build(objectPrefixes: true, interfacePrefix: "I_", hungarianVariables: false),
            _ => [],
        };

        return rules.OrderByDescending(rule => rule.Symbols.Specificity).ToList();
    }

    /// <summary>
    /// Hungarian type prefixes. <see cref="TypeClass.Unknown"/> is deliberately absent: a type we
    /// cannot resolve is never flagged, which keeps precision over recall. Also consulted by
    /// <see cref="NameChecker"/>, which only strips a prefix that agrees with the declared type.
    /// </summary>
    public static readonly (TypeClass Type, string Prefix)[] TypePrefixes =
    [
        (TypeClass.Bool, "b"),
        (TypeClass.Integer, "n"),
        (TypeClass.Real, "f"),
        (TypeClass.String, "s"),
        (TypeClass.Time, "t"),
        (TypeClass.Pointer, "p"),
        (TypeClass.Reference, "ref"),
        (TypeClass.Array, "a"),
        (TypeClass.FbInstance, "fb"),
        (TypeClass.Struct, "st"),
        (TypeClass.Enum, "e"),
        (TypeClass.Interface, "i"),
    ];

    private static List<NamingRule> Build(bool objectPrefixes, string interfacePrefix, bool hungarianVariables)
    {
        var rules = new List<NamingRule>
        {
            Rule("function_block", Kinds(SymbolKind.FunctionBlock), Pascal(objectPrefixes ? "FB_" : "")),
            Rule("function", Kinds(SymbolKind.Function), Pascal(objectPrefixes ? "F_" : "")),
            Rule("program", Kinds(SymbolKind.Program), Pascal(objectPrefixes ? "PRG_" : "")),
            Rule("interface", Kinds(SymbolKind.Interface), Pascal(interfacePrefix)),
            Rule("struct", Kinds(SymbolKind.Struct), Pascal(objectPrefixes ? "ST_" : "")),
            Rule("union", Kinds(SymbolKind.Union), Pascal(objectPrefixes ? "U_" : "")),
            Rule("enum", Kinds(SymbolKind.Enum), Pascal(objectPrefixes ? "E_" : "")),
            Rule("gvl", Kinds(SymbolKind.Gvl), Pascal(objectPrefixes ? "GVL_" : "")),
            Rule("member", Kinds(SymbolKind.Method, SymbolKind.Property, SymbolKind.Action), Pascal()),
            Rule("enum_member", Kinds(SymbolKind.EnumMember), Pascal()),
        };

        if (hungarianVariables)
        {
            foreach (var (type, prefix) in TypePrefixes)
            {
                rules.Add(Rule(
                    $"variable_{type.ToString().ToLowerInvariant()}",
                    new SymbolGroup
                    {
                        Name = $"variable_{type.ToString().ToLowerInvariant()}",
                        Kinds = [SymbolKind.Variable, SymbolKind.StructMember],
                        Types = [type],
                    },
                    Pascal(prefix)));
            }

            return rules;
        }

        rules.AddRange(
        [
            Rule("struct_member", Kinds(SymbolKind.StructMember), Pascal()),

            // An FB's VAR_INPUT/OUTPUT/IN_OUT is its public surface; a method's is a parameter list,
            // and .NET names those differently.
            Rule(
                "fb_interface",
                Variables(SymbolScope.Object, VarSection.VarInput, VarSection.VarOutput, VarSection.VarInOut),
                Pascal()),
            Rule(
                "parameter",
                Variables(SymbolScope.Member, VarSection.VarInput, VarSection.VarOutput, VarSection.VarInOut),
                Camel()),

            Rule("global", Variables(null, VarSection.VarGlobal), Pascal()),
            Rule("instance_field", Variables(SymbolScope.Object, VarSection.Var, VarSection.VarStat), Camel("_")),
            Rule(
                "local",
                Variables(SymbolScope.Member, VarSection.Var, VarSection.VarTemp, VarSection.VarInst),
                Camel()),

            // Constants are PascalCase in .NET, not SCREAMING_SNAKE. The modifier constraint
            // outranks every section rule, so this wins wherever a constant is declared.
            Rule(
                "constant",
                new SymbolGroup
                {
                    Name = "constant",
                    Kinds = [SymbolKind.Variable],
                    RequiredModifiers = VarQualifiers.Constant,
                },
                Pascal()),
        ]);

        return rules;
    }

    private static NamingRule Rule(string name, SymbolGroup symbols, NamingStyle style)
        => new() { Name = name, Symbols = symbols, Style = style, Severity = DiagnosticSeverity.Suggestion };

    private static SymbolGroup Kinds(params SymbolKind[] kinds)
        => new() { Name = string.Join('_', kinds).ToLowerInvariant(), Kinds = kinds };

    private static SymbolGroup Variables(SymbolScope? scope, params VarSection[] sections)
        => new()
        {
            Name = $"variable_{string.Join('_', sections).ToLowerInvariant()}",
            Kinds = [SymbolKind.Variable],
            Sections = sections,
            Scopes = scope is null ? [] : [scope.Value],
        };

    private static NamingStyle Pascal(string prefix = "")
        => new() { Name = $"pascal_{prefix}", Capitalisation = Capitalisation.PascalCase, RequiredPrefix = prefix };

    private static NamingStyle Camel(string prefix = "")
        => new() { Name = $"camel_{prefix}", Capitalisation = Capitalisation.CamelCase, RequiredPrefix = prefix };
}
