using System.Text;
using TcKit.Core.Models;

namespace TcKit.Adapters.Analysis;

/// <summary>
/// Renders an analysis run as plain text for a build log. One finding per line in the
/// <c>location(line): severity code: message</c> shape that compilers use, because that is what
/// CI log parsers and editors already know how to turn into a clickable annotation.
/// </summary>
public static class AnalysisText
{
    /// <summary>Render the run, listing the diagnostics that limited it before the findings themselves.</summary>
    public static string Render(AnalysisResult result, IReadOnlyList<AnalysisFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(findings);

        var text = new StringBuilder();

        // Anything that narrowed the run goes first. A short finding list next to a long skipped
        // list means partial coverage, not a clean project, and that must not be easy to miss.
        foreach (var warning in result.ConfigWarnings)
        {
            text.AppendLine($"tckit: config warning: {warning}");
        }

        foreach (var note in result.RulesNotRun)
        {
            text.AppendLine($"tckit: rule not run: {note}");
        }

        foreach (var skipped in result.Skipped)
        {
            text.AppendLine($"tckit: skipped: {skipped}");
        }

        foreach (var finding in findings)
        {
            var location = finding.ItemName.Length > 0
                ? $"{finding.ObjectName}.{finding.ItemName}"
                : finding.ObjectName;

            var suggestion = finding.Suggestion.Length > 0
                ? $" Suggested: '{finding.Suggestion}'."
                : "";

            text.AppendLine(
                $"{finding.PlcName}/{location}({finding.Line}): "
                + $"{finding.Severity.ToString().ToLowerInvariant()} {finding.RuleId}: "
                + $"{finding.Message}{suggestion}");
        }

        var bySeverity = findings
            .GroupBy(finding => finding.Severity)
            .OrderByDescending(group => group.Key)
            .Select(group => $"{group.Count()} {group.Key.ToString().ToLowerInvariant()}");

        text.Append($"tckit: {result.ObjectsAnalysed} objects analysed, profile '{result.Profile}', ");
        text.Append(findings.Count == 0 ? "no findings" : string.Join(", ", bySeverity));

        if (result.Skipped.Count > 0)
        {
            text.Append($", {result.Skipped.Count} skipped");
        }

        return text.AppendLine(".").ToString();
    }
}
