using TcKit.Core.Models;
using TcKit.Core.Ports;

namespace TcKit.Adapters.Analysis;

/// <summary>
/// Offline <see cref="IProjectAnalyser"/> (ADR-0017). Reads exclusively through
/// <see cref="IProjectReader"/>, so this adapter holds no reference to a sibling adapter and needs
/// no XAE, no licence and no running runtime.
/// </summary>
public sealed class ProjectAnalyser(IProjectReader reader) : IProjectAnalyser
{
    public async Task<AnalysisResult> AnalyseAsync(
        AnalysisRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var structure = await reader
            .GetStructureAsync(request.ProjectPath, request.PlcName, cancellationToken)
            .ConfigureAwait(false);

        var settings = AnalysisSettingsLoader.Load(DirectoryOf(request.ProjectPath));
        var classifier = new TypeClassifier(SymbolCollector.BuildTypeIndex(structure));

        var symbols = new List<NamedSymbol>();
        var skipped = new List<string>();
        var analysed = 0;

        foreach (var (plcName, plc) in structure.Plcs)
        {
            foreach (var pou in plc.Pous.Where(item => Selected(item.Name, request.ObjectName)))
            {
                var source = await Read(
                    () => reader.GetPouSourceAsync(pou.Name, plcName, cancellationToken),
                    plcName, pou.Name, skipped).ConfigureAwait(false);

                if (source is not null)
                {
                    symbols.AddRange(SymbolCollector.FromPou(source, plcName, classifier));
                    analysed++;
                }
            }

            foreach (var gvl in plc.Gvls.Where(item => Selected(item.Name, request.ObjectName)))
            {
                var source = await Read(
                    () => reader.GetGvlAsync(gvl.Name, plcName, cancellationToken),
                    plcName, gvl.Name, skipped).ConfigureAwait(false);

                if (source is not null)
                {
                    symbols.AddRange(SymbolCollector.FromGvl(source, plcName, classifier));
                    analysed++;
                }
            }

            foreach (var dut in plc.Duts.Where(item => Selected(item.Name, request.ObjectName)))
            {
                var source = await Read(
                    () => reader.GetDutAsync(dut.Name, plcName, cancellationToken),
                    plcName, dut.Name, skipped).ConfigureAwait(false);

                if (source is not null)
                {
                    symbols.AddRange(SymbolCollector.FromDut(source, plcName, classifier));
                    analysed++;
                }
            }
        }

        var findings = NamingRuleEngine.Run(symbols, settings)
            .Where(finding => finding.Severity >= request.MinimumSeverity)
            .Where(finding => request.RuleIds.Count == 0
                || request.RuleIds.Contains(finding.RuleId, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(finding => finding.Severity)
            .ThenBy(finding => finding.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.Line)
            .ToList();

        return new AnalysisResult
        {
            ProjectPath = request.ProjectPath,
            Profile = settings.Profile,
            ObjectsAnalysed = analysed,
            Findings = findings,
            Skipped = skipped,
            ConfigWarnings = settings.ConfigWarnings,
        };
    }

    /// <summary>
    /// Read one object, recording rather than propagating a per-object failure. One unreadable POU
    /// must not abort the run, but it must not pass silently either, or a clean result would be
    /// mistaken for full coverage.
    /// </summary>
    private static async Task<T?> Read<T>(
        Func<Task<T>> read, string plcName, string objectName, List<string> skipped)
        where T : class
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception exc)
            when (exc is IOException or InvalidDataException or UnauthorizedAccessException
                or ArgumentException)
        {
            skipped.Add($"{plcName}.{objectName}: {exc.Message}");
            return null;
        }
    }

    private static string DirectoryOf(string projectPath)
        => File.Exists(projectPath)
            ? Path.GetDirectoryName(projectPath) ?? projectPath
            : projectPath;

    // ST identifiers are case-insensitive, so the scope filter is too.
    private static bool Selected(string name, string? filter)
        => string.IsNullOrEmpty(filter) || string.Equals(name, filter, StringComparison.OrdinalIgnoreCase);
}
