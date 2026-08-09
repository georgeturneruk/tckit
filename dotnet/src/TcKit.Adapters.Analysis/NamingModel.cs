using TcKit.Core.Analysis;
using TcKit.Core.Models;

namespace TcKit.Adapters.Analysis;

/// <summary>
/// The broad type family a declared type belongs to. This is what a naming rule selects on when
/// a convention keys off the type rather than the scope (the <c>hungarian</c> profile), and it is
/// the analogue of typescript-eslint's <c>types</c> selector.
/// </summary>
public enum TypeClass
{
    Unknown,
    Bool,
    Integer,
    Real,
    String,
    Time,
    Pointer,
    Reference,
    Array,
    FbInstance,
    Struct,
    Enum,
    Interface,
}

/// <summary>
/// Whether a symbol is declared on the object itself or inside one of its members. The VAR
/// keyword is the same in both places, but an FB's <c>VAR</c> is instance state while a method's
/// <c>VAR</c> is a local, and .NET names those differently.
/// </summary>
public enum SymbolScope
{
    Object,
    Member,
}

/// <summary>Capitalisation styles, mirroring the .NET <c>dotnet_naming_style.capitalization</c> values.</summary>
public enum Capitalisation
{
    Any,
    PascalCase,
    CamelCase,
    AllUpper,
    AllLower,
    FirstWordUpper,
}

/// <summary>
/// A set of constraints selecting the symbols a rule applies to. Every populated constraint must
/// match. Mirrors <c>dotnet_naming_symbols</c>, with <see cref="Sections"/> and
/// <see cref="Types"/> added for Structured Text.
/// </summary>
public sealed record SymbolGroup
{
    public required string Name { get; init; }

    public IReadOnlyList<SymbolKind> Kinds { get; init; } = [];

    /// <summary>VAR blocks this group covers. Selects variables only.</summary>
    public IReadOnlyList<VarSection> Sections { get; init; } = [];

    /// <summary>Access specifiers this group covers. Selects methods, properties and actions only,
    /// since TwinCAT 3 has no per-variable access modifier.</summary>
    public IReadOnlyList<StAccessibility> Accessibilities { get; init; } = [];

    public IReadOnlyList<TypeClass> Types { get; init; } = [];

    /// <summary>Whether the symbol is declared on the object or inside a member.</summary>
    public IReadOnlyList<SymbolScope> Scopes { get; init; } = [];

    /// <summary>Qualifiers a symbol must carry in full to match, e.g. <see cref="VarQualifiers.Constant"/>.</summary>
    public VarQualifiers RequiredModifiers { get; init; } = VarQualifiers.None;

    /// <summary>
    /// How constrained this group is. Rules sort by this descending so the most specific match wins,
    /// which is how both Roslyn and typescript-eslint remove the ordering footgun from config.
    /// </summary>
    public int Specificity =>
        (Types.Count > 0 ? 32 : 0)
        + (RequiredModifiers != VarQualifiers.None ? 16 : 0)
        + (Accessibilities.Count > 0 ? 8 : 0)
        + (Sections.Count > 0 ? 4 : 0)
        + (Scopes.Count > 0 ? 2 : 0)
        + (Kinds.Count > 0 ? 1 : 0);

    /// <summary>Whether <paramref name="symbol"/> satisfies every populated constraint.</summary>
    public bool Matches(NamedSymbol symbol)
    {
        if (Kinds.Count > 0 && !Kinds.Contains(symbol.Kind))
        {
            return false;
        }

        if (Sections.Count > 0 && (symbol.Section is null || !Sections.Contains(symbol.Section.Value)))
        {
            return false;
        }

        if (Accessibilities.Count > 0 && !Accessibilities.Contains(symbol.Accessibility))
        {
            return false;
        }

        if (Types.Count > 0 && !Types.Contains(symbol.TypeClass))
        {
            return false;
        }

        if (Scopes.Count > 0 && !Scopes.Contains(symbol.Scope))
        {
            return false;
        }

        return RequiredModifiers == VarQualifiers.None
            || (symbol.Qualifiers & RequiredModifiers) == RequiredModifiers;
    }
}

/// <summary>A naming style: what a conforming name looks like. Mirrors <c>dotnet_naming_style</c>.</summary>
public sealed record NamingStyle
{
    public required string Name { get; init; }

    public Capitalisation Capitalisation { get; init; } = Capitalisation.Any;

    public string RequiredPrefix { get; init; } = "";

    public string RequiredSuffix { get; init; } = "";

    public string WordSeparator { get; init; } = "";
}

/// <summary>The binding of a symbol group to a style at a severity. Mirrors <c>dotnet_naming_rule</c>.</summary>
public sealed record NamingRule
{
    public required string Name { get; init; }

    public required SymbolGroup Symbols { get; init; }

    public required NamingStyle Style { get; init; }

    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Suggestion;
}

/// <summary>A named thing the analyser can report on, flattened from the reader's output.</summary>
public sealed record NamedSymbol
{
    public required string Name { get; init; }

    public required SymbolKind Kind { get; init; }

    public required string PlcName { get; init; }

    /// <summary>The POU, GVL or DUT this symbol lives in (or is).</summary>
    public required string ObjectName { get; init; }

    /// <summary>The method, action or property, or empty when the symbol sits at object level.</summary>
    public string ItemName { get; init; } = "";

    public required int Line { get; init; }

    /// <summary>The VAR block, for variables; null for everything else.</summary>
    public VarSection? Section { get; init; }

    public SymbolScope Scope { get; init; } = SymbolScope.Object;

    public StAccessibility Accessibility { get; init; } = StAccessibility.Public;

    public VarQualifiers Qualifiers { get; init; } = VarQualifiers.None;

    public TypeClass TypeClass { get; init; } = TypeClass.Unknown;

    public string TypeExpression { get; init; } = "";
}
