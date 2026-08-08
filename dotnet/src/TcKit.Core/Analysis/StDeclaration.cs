namespace TcKit.Core.Analysis;

/// <summary>The VAR block a declaration belongs to.</summary>
public enum VarSection
{
    Var,
    VarInput,
    VarOutput,
    VarInOut,
    VarGlobal,
    VarStat,
    VarTemp,
    VarInst,
}

/// <summary>
/// Type qualifiers on a VAR block. TwinCAT 3 writes these as words after the section keyword
/// (<c>VAR CONSTANT</c>); the TwinCAT 2 style <c>VAR_PERSISTENT</c> maps to
/// <see cref="VarSection.Var"/> plus <see cref="Persistent"/>.
/// </summary>
[Flags]
public enum VarQualifiers
{
    None = 0,
    Constant = 1,
    Retain = 2,
    Persistent = 4,
}

/// <summary>
/// An access specifier. TwinCAT 3 allows these on methods, properties, actions, function blocks
/// and interfaces, but not on individual member variables; <see cref="Public"/> is the default
/// when none is written.
/// </summary>
public enum StAccessibility
{
    Public,
    Private,
    Protected,
    Internal,
}

/// <summary>One variable declared in a VAR block, with the position needed to report on it.</summary>
public sealed record StVariable
{
    public required string Name { get; init; }

    /// <summary>The declared type as written, e.g. <c>ARRAY [0..9] OF POINTER TO ST_Foo</c>.</summary>
    public required string TypeExpression { get; init; }

    public required VarSection Section { get; init; }

    public VarQualifiers Qualifiers { get; init; } = VarQualifiers.None;

    /// <summary>1-based line within the declaration text this variable was parsed from.</summary>
    public required int Line { get; init; }

    /// <summary>The <c>AT %I*</c> address when present, otherwise empty.</summary>
    public string Address { get; init; } = "";
}

/// <summary>The header line of a POU or member: <c>FUNCTION_BLOCK PUBLIC FB_Foo EXTENDS FB_Base</c>.</summary>
public sealed record StHeader
{
    /// <summary>FUNCTION_BLOCK, FUNCTION, PROGRAM, INTERFACE, METHOD, PROPERTY or ACTION. Empty for a GVL or accessor.</summary>
    public string Keyword { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>The declared return type, or empty when the construct has none.</summary>
    public string ReturnType { get; init; } = "";

    public StAccessibility Accessibility { get; init; } = StAccessibility.Public;

    public string Extends { get; init; } = "";

    public IReadOnlyList<string> Implements { get; init; } = [];

    public bool IsAbstract { get; init; }

    public bool IsFinal { get; init; }
}

/// <summary>A parsed declaration block: its header (when it has one) and every variable it declares.</summary>
public sealed record StDeclaration
{
    public required StHeader Header { get; init; }

    public IReadOnlyList<StVariable> Variables { get; init; } = [];
}

/// <summary>One member of a STRUCT or UNION, or one enumeration constant.</summary>
public sealed record StTypeMember
{
    public required string Name { get; init; }

    /// <summary>The declared type as written; empty for enumeration constants.</summary>
    public string TypeExpression { get; init; } = "";

    public required int Line { get; init; }
}

/// <summary>A parsed DUT declaration: the type name and its members.</summary>
public sealed record StTypeDeclaration
{
    public required string Name { get; init; }

    public IReadOnlyList<StTypeMember> Members { get; init; } = [];
}
