using TcKit.Core.Models;

namespace TcKit.Adapters.Analysis;

/// <summary>The effective analysis configuration for a project.</summary>
public sealed record AnalysisSettings
{
    /// <summary>The naming profile in force. Defaults to <see cref="NamingProfiles.Hybrid"/>.</summary>
    public string Profile { get; init; } = NamingProfiles.Hybrid;

    /// <summary>Rules from the profile, plus any the project defined, most specific first.</summary>
    public IReadOnlyList<NamingRule> Rules { get; init; } = [];

    /// <summary>Per-rule-id severity overrides from <c>tckit_diagnostic.TCK1002.severity</c>.</summary>
    public IReadOnlyDictionary<string, DiagnosticSeverity> RuleSeverities { get; init; }
        = new Dictionary<string, DiagnosticSeverity>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-category severity overrides from <c>tckit_analyzer_diagnostic.category-naming.severity</c>.</summary>
    public IReadOnlyDictionary<string, DiagnosticSeverity> CategorySeverities { get; init; }
        = new Dictionary<string, DiagnosticSeverity>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Configuration that could not be applied, surfaced rather than silently dropped.</summary>
    public IReadOnlyList<string> ConfigWarnings { get; init; } = [];

    /// <summary>Resolve the severity for a finding, letting rule id beat category beat the rule's own default.</summary>
    public DiagnosticSeverity SeverityFor(string ruleId, string category, DiagnosticSeverity fallback)
    {
        if (RuleSeverities.TryGetValue(ruleId, out var byId))
        {
            return byId;
        }

        return CategorySeverities.TryGetValue(category, out var byCategory) ? byCategory : fallback;
    }
}
