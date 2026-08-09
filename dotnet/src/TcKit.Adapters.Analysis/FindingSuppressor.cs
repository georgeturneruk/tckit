using System.Text.RegularExpressions;
using TcKit.Core.Models;

namespace TcKit.Adapters.Analysis;

/// <summary>
/// Applies in-source suppression comments (ADR-0018). Every rule has legitimate exceptions:
/// TcOpen's <c>IsNearlyEqual</c> opens with an exact float comparison as a deliberate fast path,
/// which <c>TCK2002</c> is right to notice and the author is right to keep. Without a way to say
/// so in the code, the only options are to silence the whole rule or to live with the noise, and
/// both end with the analyser being ignored.
///
/// <code>
/// // tckit-disable-next-line TCK2002
/// IF Coordinate1 = Coordinate2 THEN
///
/// IF a = b THEN // tckit-disable-line TCK2002, TCK2004
/// </code>
///
/// Omitting the rule ids suppresses every rule on that line. Comments are read from the original
/// source rather than the masked copy, for the obvious reason.
/// </summary>
public static partial class FindingSuppressor
{
    /// <summary>Drop the findings that a suppression comment covers.</summary>
    public static List<AnalysisFinding> Apply(
        IReadOnlyList<AnalysisFinding> findings, AnalysedProject project)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(project);

        var blocks = SourceIndex.Build(project);
        var cache = new Dictionary<string, Dictionary<int, HashSet<string>>>(StringComparer.Ordinal);

        return findings.Where(finding =>
        {
            var key = SourceIndex.Key(
                finding.PlcName, finding.ObjectName, finding.ItemName, finding.Part);
            if (!blocks.TryGetValue(key, out var block))
            {
                return true;
            }

            if (!cache.TryGetValue(key, out var suppressions))
            {
                suppressions = Parse(block.Text);
                cache[key] = suppressions;
            }

            return !suppressions.TryGetValue(finding.Line, out var rules)
                || !(rules.Count == 0 || rules.Contains(finding.RuleId));
        }).ToList();
    }

    /// <summary>Line number to the rule ids suppressed on it; an empty set means every rule.</summary>
    private static Dictionary<int, HashSet<string>> Parse(string text)
    {
        var result = new Dictionary<int, HashSet<string>>();
        var lines = (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var match = SuppressionComment().Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            // Lines are 1-based, and "next-line" moves the target one further on.
            var target = index + 1
                + (match.Groups["scope"].Value.Equals("next-line", StringComparison.OrdinalIgnoreCase) ? 1 : 0);

            var rules = match.Groups["rules"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (result.TryGetValue(target, out var existing))
            {
                // A bare suppression already covers everything, so keep it that way.
                if (existing.Count > 0 && rules.Count > 0)
                {
                    existing.UnionWith(rules);
                }
                else
                {
                    existing.Clear();
                }
            }
            else
            {
                result[target] = rules;
            }
        }

        return result;
    }

    [GeneratedRegex(
        @"//\s*tckit-disable-(?<scope>next-line|line)\b(?<rules>[^\r\n]*)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SuppressionComment();
}
