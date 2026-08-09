using TcKit.Core.Models;

namespace TcKit.Adapters.Analysis;

/// <summary>
/// A recorded set of findings to ignore, so analysis can be gated in CI on an existing codebase.
/// TcOpen reports 1544 findings under <c>infer</c>; no team is going to fix those before turning
/// the check on, and a gate nobody can turn on is a gate that never runs. Record today's findings
/// as the baseline and the build fails only on new ones.
///
/// The fingerprint deliberately excludes the line number. Inserting a variable higher up a
/// declaration would otherwise invalidate every entry below it and fail a build that changed
/// nothing relevant.
/// </summary>
public static class AnalysisBaseline
{
    /// <summary>Stable identity for a finding: everything that says what it is, minus where it sits.</summary>
    public static string Fingerprint(AnalysisFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        return string.Join(
            '|', finding.PlcName, finding.ObjectName, finding.ItemName, finding.RuleId, finding.Symbol);
    }

    /// <summary>
    /// Read a baseline file. Blank lines and <c>#</c> comments are ignored, so a team can annotate
    /// entries with why they are still there. A missing file is an empty baseline, not an error:
    /// the first run on a new branch should not fail because nobody has recorded one yet.
    /// </summary>
    public static IReadOnlySet<string> Load(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return File.ReadLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Write the findings as a baseline, sorted so the file diffs and merges cleanly.</summary>
    public static void Save(string path, IEnumerable<AnalysisFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(findings);

        var lines = findings
            .Select(Fingerprint)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(line => line, StringComparer.Ordinal);

        File.WriteAllLines(
            path,
            new[]
            {
                "# TcKit analysis baseline. Findings listed here are not reported.",
                "# Regenerate with: tckit analyse <project> --write-baseline <this file>",
                "# Delete a line to start enforcing that finding again.",
            }.Concat(lines));
    }

    /// <summary>Drop the findings the baseline already records.</summary>
    public static List<AnalysisFinding> Filter(
        IEnumerable<AnalysisFinding> findings, IReadOnlySet<string> baseline)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(baseline);

        return baseline.Count == 0
            ? findings.ToList()
            : findings.Where(finding => !baseline.Contains(Fingerprint(finding))).ToList();
    }
}
