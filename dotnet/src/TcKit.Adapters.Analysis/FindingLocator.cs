using TcKit.Core.Models;

namespace TcKit.Adapters.Analysis;

/// <summary>
/// Turns a finding's location into a file and a line of that file.
///
/// Rules report where a TwinCAT developer edits: object, item, and line within that item. That is
/// the right thing for a person reading the output, but it is not a location anything else can act
/// on, because "line 4 of <c>Execute</c>" is line 4 of one CDATA block inside a <c>.TcPOU</c>, not
/// line 4 of any file. This resolves the one into the other, so the rules stay ignorant of how the
/// project is stored and every output format gets a real location without asking for it.
/// </summary>
public static class FindingLocator
{
    /// <summary>Return the findings with <c>FilePath</c> and <c>FileLine</c> filled in where they can be.</summary>
    public static List<AnalysisFinding> Apply(
        IReadOnlyList<AnalysisFinding> findings, AnalysedProject project)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(project);

        var blocks = SourceIndex.Build(project);
        return findings.Select(finding => Locate(finding, blocks)).ToList();
    }

    private static AnalysisFinding Locate(
        AnalysisFinding finding, IReadOnlyDictionary<string, SourceBlock> blocks)
    {
        var block = SourceIndex.For(blocks, finding);
        if (block is null || block.Line <= 0)
        {
            // An unresolvable location is left empty rather than guessed at. A finding pointing at
            // the wrong line is worse than one pointing nowhere: it sends a reader to innocent code
            // and, in a SARIF upload, annotates it.
            return finding;
        }

        return finding with
        {
            FilePath = block.Path,
            FileLine = block.Line + Math.Max(finding.Line, 1) - 1,
        };
    }
}
