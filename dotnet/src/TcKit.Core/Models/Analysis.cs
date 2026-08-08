namespace TcKit.Core.Models;

/// <summary>
/// Severity of an analysis finding, mirroring the Roslyn ladder so an <c>.editorconfig</c>
/// severity value maps straight across.
/// </summary>
public enum DiagnosticSeverity
{
    None,
    Silent,
    Suggestion,
    Warning,
    Error,
}

/// <summary>The kind of thing a rule can name, spanning program objects and the symbols inside them.</summary>
public enum SymbolKind
{
    FunctionBlock,
    Function,
    Program,
    Interface,
    Struct,
    Union,
    Enum,
    Alias,
    Gvl,
    Method,
    Property,
    Action,
    Variable,
    StructMember,
    EnumMember,
}

/// <summary>Which half of an object a finding sits in.</summary>
public enum CodePart
{
    Declaration,
    Implementation,
}

/// <summary>
/// One analysis finding. Located by object plus item plus line-within-item, which is how a
/// TwinCAT object is actually edited; there is no meaningful whole-file line number for a
/// declaration that lives in its own XML element.
/// </summary>
public sealed record AnalysisFinding
{
    /// <summary>The permanent rule id, e.g. <c>TCK1002</c>.</summary>
    public required string RuleId { get; init; }

    public required string Category { get; init; }

    public required DiagnosticSeverity Severity { get; init; }

    public required string Message { get; init; }

    public required string PlcName { get; init; }

    /// <summary>The POU, GVL or DUT the finding sits in.</summary>
    public required string ObjectName { get; init; }

    /// <summary>The method, action or property, or empty for an object-level finding.</summary>
    public string ItemName { get; init; } = "";

    public CodePart Part { get; init; } = CodePart.Declaration;

    /// <summary>1-based line within the item's declaration or implementation.</summary>
    public required int Line { get; init; }

    /// <summary>The identifier the finding is about.</summary>
    public required string Symbol { get; init; }

    /// <summary>A conforming name when the rule can derive one, otherwise empty.</summary>
    public string Suggestion { get; init; } = "";
}

/// <summary>What to analyse and how much of it to report.</summary>
public sealed record AnalysisRequest
{
    /// <summary>Solution root directory, or a <c>.sln</c> inside it.</summary>
    public required string ProjectPath { get; init; }

    public string? PlcName { get; init; }

    /// <summary>Restrict analysis to one POU, GVL or DUT. The write-loop entry point.</summary>
    public string? ObjectName { get; init; }

    public DiagnosticSeverity MinimumSeverity { get; init; } = DiagnosticSeverity.Suggestion;

    /// <summary>Restrict to these rule ids; empty means every enabled rule.</summary>
    public IReadOnlyList<string> RuleIds { get; init; } = [];
}

/// <summary>The outcome of an analysis run.</summary>
public sealed record AnalysisResult
{
    public required string ProjectPath { get; init; }

    /// <summary>The naming profile in force, e.g. <c>hybrid</c>.</summary>
    public required string Profile { get; init; }

    public int ObjectsAnalysed { get; init; }

    public IReadOnlyList<AnalysisFinding> Findings { get; init; } = [];

    /// <summary>Objects that could not be read or parsed, so a clean run is never mistaken for full coverage.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    /// <summary>Configuration that could not be applied, surfaced rather than silently ignored.</summary>
    public IReadOnlyList<string> ConfigWarnings { get; init; } = [];
}
