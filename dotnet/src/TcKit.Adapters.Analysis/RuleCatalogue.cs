using TcKit.Core.Models;

namespace TcKit.Adapters.Analysis;

/// <summary>What one rule is, independently of the engine that happens to implement it.</summary>
public sealed record RuleDescriptor
{
    /// <summary>The permanent id, e.g. <c>TCK1002</c>. Never reused for a different rule.</summary>
    public required string Id { get; init; }

    /// <summary><c>naming</c>, <c>correctness</c> or <c>structure</c>. Configurable as a group.</summary>
    public required string Category { get; init; }

    /// <summary>Severity when nothing in the configuration says otherwise.</summary>
    public required DiagnosticSeverity DefaultSeverity { get; init; }

    /// <summary>One line, in the shape of a heading rather than a sentence.</summary>
    public required string Title { get; init; }

    /// <summary>What the rule catches and why that matters, for a reader who has not met it before.</summary>
    public required string Description { get; init; }

    /// <summary>The anchor of this rule's row on the analysis documentation page.</summary>
    public required string Anchor { get; init; }

    /// <summary>Where to read more. GitHub renders this as the "View rule" link beside a code-scanning alert.</summary>
    public string HelpUri => $"{RuleCatalogue.DocsPage}#{Anchor}";
}

/// <summary>
/// Every rule TcKit ships, in one place.
///
/// The engines each declare the ids they implement, because code that raises a finding should name
/// the rule it is raising, but the metadata around an id lives here alone. SARIF needs a
/// <c>tool.driver.rules[]</c> entry per rule, and building that from consts scattered over two
/// engines would mean a parallel list that silently falls behind the rules themselves. A test pins
/// the two together, so adding a rule without cataloguing it fails rather than shipping an
/// uncatalogued finding.
/// </summary>
public static class RuleCatalogue
{
    /// <summary>The documentation page every <c>helpUri</c> points into.</summary>
    public const string DocsPage = "https://tckit.org/capabilities/analysis/overview/";

    public const string NamingCategory = "naming";

    public const string CorrectnessCategory = "correctness";

    public const string StructureCategory = "structure";

    /// <summary>Every rule, in id order.</summary>
    public static IReadOnlyList<RuleDescriptor> All { get; } =
    [
        new RuleDescriptor
        {
            Id = NamingRuleEngine.ObjectRuleId,
            Category = NamingCategory,
            DefaultSeverity = DiagnosticSeverity.Suggestion,
            Title = "Object name does not follow the convention",
            Description =
                "POUs, DUTs and GVLs share one flat namespace, so the convention in force decides "
                + "how they are spelled and whether they carry a kind prefix such as FB_ or ST_.",
            Anchor = "naming",
        },
        new RuleDescriptor
        {
            Id = NamingRuleEngine.VariableRuleId,
            Category = NamingCategory,
            DefaultSeverity = DiagnosticSeverity.Suggestion,
            Title = "Variable name does not follow the convention",
            Description =
                "Variables are checked per VAR block, because the convention in force may case an "
                + "input differently from a local or a constant.",
            Anchor = "naming",
        },
        new RuleDescriptor
        {
            Id = NamingRuleEngine.MemberRuleId,
            Category = NamingCategory,
            DefaultSeverity = DiagnosticSeverity.Suggestion,
            Title = "Method, property or action name does not follow the convention",
            Description =
                "Names TwinCAT matches by name are never reported: FB_init, FB_exit and FB_reinit, "
                + "and their parameters.",
            Anchor = "naming",
        },
        new RuleDescriptor
        {
            Id = NamingRuleEngine.TypeMemberRuleId,
            Category = NamingCategory,
            DefaultSeverity = DiagnosticSeverity.Suggestion,
            Title = "Struct field or enumeration constant does not follow the convention",
            Description =
                "Members of a DUT are checked separately from variables, since a project that "
                + "cases fields one way often cases enumeration constants another.",
            Anchor = "naming",
        },
        new RuleDescriptor
        {
            Id = NamingRuleEngine.RedundantTypePrefixId,
            Category = NamingCategory,
            DefaultSeverity = DiagnosticSeverity.Suggestion,
            Title = "Type prefix left over from a convention that does not use one",
            Description =
                "A casing rule structurally cannot see this: nCount is already valid camelCase, so "
                + "nothing notices the n. The prefix must agree with the declared type, so "
                + "nCount : INT is reported and nextValue : INT is not, and a variable named after "
                + "its own type is left alone.",
            Anchor = "naming",
        },
        new RuleDescriptor
        {
            Id = CorrectnessRules.StatelessInstanceId,
            Category = CorrectnessCategory,
            DefaultSeverity = DiagnosticSeverity.Warning,
            Title = "Stateful function block instance declared on a call stack",
            Description =
                "A function block that must persist between calls, declared in a method's VAR or in "
                + "a FUNCTION, is rebuilt every call and never advances. Use VAR_INST, or declare "
                + "it on the function block.",
            Anchor = "correctness",
        },
        new RuleDescriptor
        {
            Id = CorrectnessRules.RealEqualityId,
            Category = CorrectnessCategory,
            DefaultSeverity = DiagnosticSeverity.Warning,
            Title = "Floating-point value compared for exact equality",
            Description =
                "REAL or LREAL compared with = or <>. This usually appears to work until a value is "
                + "not exactly representable, at which point it fails without any code changing.",
            Anchor = "correctness",
        },
        new RuleDescriptor
        {
            Id = CorrectnessRules.MisplacedRetainId,
            Category = CorrectnessCategory,
            DefaultSeverity = DiagnosticSeverity.Warning,
            Title = "RETAIN or PERSISTENT where it cannot survive a restart",
            Description =
                "The qualifier is on a local, which is rebuilt on every call, so it buys nothing "
                + "and implies a persistence the code does not have.",
            Anchor = "correctness",
        },
        new RuleDescriptor
        {
            Id = CorrectnessRules.UnusedLocalId,
            Category = CorrectnessCategory,
            DefaultSeverity = DiagnosticSeverity.Warning,
            Title = "Local declared and never used",
            Description =
                "Locals only. TwinCAT 3 leaves a function block's VAR members reachable from "
                + "outside, so an apparently unused one may be part of its API.",
            Anchor = "correctness",
        },
        new RuleDescriptor
        {
            Id = CorrectnessRules.UnreadInputId,
            Category = CorrectnessCategory,
            DefaultSeverity = DiagnosticSeverity.Warning,
            Title = "Function block input that nothing reads",
            Description =
                "An input the body never reads normally means a wiring mistake. Skipped when "
                + "anything extends the function block, since a child may be the reader.",
            Anchor = "correctness",
        },
        new RuleDescriptor
        {
            Id = CorrectnessRules.MultiWriterGlobalId,
            Category = StructureCategory,
            DefaultSeverity = DiagnosticSeverity.Suggestion,
            Title = "Global written from more than one POU",
            Description =
                "On separate tasks that is a race; on one task the last writer in scan order "
                + "silently wins. Qualified writes only, so it under-reports rather than guessing.",
            Anchor = "structure",
        },
        new RuleDescriptor
        {
            Id = CorrectnessRules.UnreachableObjectId,
            Category = StructureCategory,
            DefaultSeverity = DiagnosticSeverity.Suggestion,
            Title = "POU nothing instantiates, calls, or binds to a task",
            Description =
                "Searched across every PLC project in the solution, counting namespace-qualified "
                + "references. It cannot tell dead code from a library meant for consumers outside "
                + "the solution, which is why it is a suggestion.",
            Anchor = "structure",
        },
    ];

    private static readonly Dictionary<string, RuleDescriptor> s_byId =
        All.ToDictionary(rule => rule.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The descriptor for an id, or null when the id is not one of ours.</summary>
    public static RuleDescriptor? Find(string ruleId) => s_byId.GetValueOrDefault(ruleId);

    /// <summary>The descriptor for an id the caller knows exists.</summary>
    public static RuleDescriptor Require(string ruleId)
        => Find(ruleId) ?? throw new ArgumentOutOfRangeException(
            nameof(ruleId), ruleId, "No such rule in the catalogue.");

    /// <summary>The descriptors covering a set of findings, in id order, for a SARIF rules array.</summary>
    public static List<RuleDescriptor> For(IEnumerable<AnalysisFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return findings
            .Select(finding => finding.RuleId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Find)
            .OfType<RuleDescriptor>()
            .OrderBy(rule => rule.Id, StringComparer.Ordinal)
            .ToList();
    }
}
