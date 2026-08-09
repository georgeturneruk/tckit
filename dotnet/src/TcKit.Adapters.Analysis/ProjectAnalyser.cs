using TcKit.Core.Analysis;
using TcKit.Core.Models;
using TcKit.Core.Ports;

namespace TcKit.Adapters.Analysis;

/// <summary>
/// Offline <see cref="IProjectAnalyser"/> (ADR-0017). Reads exclusively through
/// <see cref="IProjectReader"/>, so this adapter holds no reference to a sibling adapter and needs
/// no XAE, no licence and no running runtime.
///
/// The project is parsed once into an <see cref="AnalysedProject"/> and both rule engines run over
/// that same model, because the cross-file rules need every POU in hand at once.
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

        var skipped = new List<string>();
        var pous = new List<AnalysedPou>();
        var gvls = new List<AnalysedGvl>();
        var duts = new List<AnalysedDut>();

        foreach (var (plcName, plc) in structure.Plcs)
        {
            foreach (var reference in plc.Pous.Where(item => Selected(item.Name, request.ObjectName)))
            {
                var source = await Read(
                    () => reader.GetPouSourceAsync(reference.Name, plcName, cancellationToken),
                    plcName, reference.Name, skipped).ConfigureAwait(false);

                if (source is not null)
                {
                    pous.Add(AnalysedProject.Analyse(source, plcName));
                }
            }

            foreach (var reference in plc.Gvls.Where(item => Selected(item.Name, request.ObjectName)))
            {
                var source = await Read(
                    () => reader.GetGvlAsync(reference.Name, plcName, cancellationToken),
                    plcName, reference.Name, skipped).ConfigureAwait(false);

                if (source is not null)
                {
                    gvls.Add(new AnalysedGvl
                    {
                        PlcName = plcName,
                        Source = source,
                        Declaration = DeclarationParser.Parse(source.Declaration),
                    });
                }
            }

            foreach (var reference in plc.Duts.Where(item => Selected(item.Name, request.ObjectName)))
            {
                var source = await Read(
                    () => reader.GetDutAsync(reference.Name, plcName, cancellationToken),
                    plcName, reference.Name, skipped).ConfigureAwait(false);

                if (source is not null)
                {
                    duts.Add(new AnalysedDut
                    {
                        PlcName = plcName,
                        Source = source,
                        Declaration = DeclarationParser.ParseType(source.Declaration),
                    });
                }
            }
        }

        var scoped = !string.IsNullOrEmpty(request.ObjectName);
        var project = new AnalysedProject
        {
            Structure = structure,
            Classifier = classifier,
            Pous = pous,
            Gvls = gvls,
            Duts = duts,
            IsWholeProject = !scoped,
        };

        var symbols = new List<NamedSymbol>();
        foreach (var pou in pous)
        {
            symbols.AddRange(SymbolCollector.FromPou(pou, classifier));
        }

        foreach (var gvl in gvls)
        {
            symbols.AddRange(SymbolCollector.FromGvl(gvl.Source, gvl.PlcName, classifier));
        }

        foreach (var dut in duts)
        {
            symbols.AddRange(SymbolCollector.FromDut(dut.Source, dut.PlcName, classifier));
        }

        var rulesNotRun = new List<string>();
        if (scoped)
        {
            rulesNotRun.Add(
                $"{string.Join(", ", CorrectnessRules.WholeProjectRules)}: these rules need the "
                + "whole solution, so they are skipped when objectName scopes the run. Analyse "
                + "without objectName to include them.");
        }

        if (!scoped && project.HasUnreadableBodies)
        {
            var languages = pous
                .Where(pou => pou.Source.HasUnreadableBody)
                .Select(pou => pou.Name)
                .Take(5)
                .ToList();

            rulesNotRun.Add(
                $"{CorrectnessRules.UnreachableObjectId}: this project contains POUs written in a "
                + "language other than ST, whose bodies are not stored as readable source, so a "
                + "call made from one is invisible. Reachability would be unreliable. Affected: "
                + string.Join(", ", languages) + (pous.Count(p => p.Source.HasUnreadableBody) > 5 ? ", ..." : ""));
        }

        if (settings.Profile == NamingProfiles.Infer)
        {
            if (scoped)
            {
                // Inferring a convention from a single object would be guessing, and a wrong
                // inference produces confidently wrong findings.
                rulesNotRun.Add(
                    "naming: the 'infer' profile derives the convention from the whole project, so "
                    + "naming rules are skipped when objectName scopes the run.");
            }
            else
            {
                settings = settings with
                {
                    Rules = settings.Rules
                        .Concat(ProfileInference.Infer(symbols))
                        .OrderByDescending(rule => rule.Symbols.Specificity)
                        .ToList(),
                };
            }
        }

        var raw = NamingRuleEngine.Run(symbols, settings)
            .Concat(CorrectnessRules.Run(project, settings))
            .ToList();

        var suppressed = FindingSuppressor.Apply(raw, project);

        // Located after suppression and before filtering, so the cost is paid once per finding that
        // actually survives to be reported.
        var findings = FindingLocator.Apply(suppressed, project)
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
            ObjectsAnalysed = pous.Count + gvls.Count + duts.Count,
            Findings = findings,
            Skipped = skipped,
            ConfigWarnings = settings.ConfigWarnings,
            RulesNotRun = rulesNotRun,
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
