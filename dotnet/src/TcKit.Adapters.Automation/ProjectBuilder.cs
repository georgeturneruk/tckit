using System.Text.RegularExpressions;
using TcKit.Core.Models;

namespace TcKit.Adapters.Automation;

/// <summary>
/// Build + deploy logic against the <see cref="ITcSession"/> seam, COM-free so it runs against the
/// in-memory fake in CI. Build is a CheckAllObjects compile plus an Error List read for structured
/// diagnostics; deploy resolves a solution configuration, sets the target, optionally enables boot
/// autostart, and activates. The COM specifics live in the session implementation. Mirrors
/// Invoke-TcBuild.ps1 / Invoke-TcDeploy.ps1.
/// </summary>
internal static partial class ProjectBuilder
{
    public static BuildResult Build(ITcSession session, string? plcName, bool forceLog)
    {
        var start = DateTime.UtcNow;
        session.UseSolution("");
        var plc = ProjectAuthor.ResolvePlcName(session, plcName);
        var sm = ProjectAuthor.GetSysManager(session, plc);
        var plcProject = sm.LookupTreeItem($"TIPC^{plc}^{plc} Project");

        // Tier 1: fast binary signal. CheckAllObjects compiles the PLC and populates the Error List.
        var checkOk = TryCheckAllObjects(plcProject);

        var errors = new List<BuildError>();
        var warnings = new List<BuildError>();
        var infos = new List<BuildError>();

        // Tier 2: structured diagnostics when the build failed, or the caller asked for warnings.
        if (!checkOk || forceLog)
        {
            var rows = session.ReadErrorList();
            if (rows is not null)
            {
                foreach (var row in rows)
                {
                    var (mapped, severity) = MapError(row);
                    switch (severity)
                    {
                        case "warning": warnings.Add(mapped); break;
                        case "info": infos.Add(mapped); break;
                        default: errors.Add(mapped); break;
                    }
                }
            }
            else if (!checkOk)
            {
                errors.Add(new BuildError(
                    "", 0,
                    "PLC compile failed, but per-error detail couldn't be read from the IDE Error List "
                    + "(not exposed by this XAE edition). Open the solution in full TcXaeShell to see the errors."));
            }
        }

        var duration = Math.Round((DateTime.UtcNow - start).TotalSeconds, 2);
        return new BuildResult
        {
            Success = checkOk && errors.Count == 0,
            Errors = errors,
            Warnings = warnings,
            Infos = infos,
            DurationSeconds = duration,
            Details = new Dictionary<string, object?> { ["plc"] = plc, ["check_all_objects"] = checkOk },
        };
    }

    public static Result Deploy(ITcSession session, string targetAmsId, string? plcName, bool bootAutostart)
    {
        if (string.IsNullOrEmpty(targetAmsId))
        {
            throw new ArgumentException("TargetAmsId required.");
        }

        session.UseSolution("");
        var plc = ProjectAuthor.ResolvePlcName(session, plcName);
        var sm = ProjectAuthor.GetSysManager(session, plc);

        // A solution configuration must be active or ActivateConfiguration throws an opaque E_UNEXPECTED.
        session.ResolveSolutionConfiguration("Release");
        sm.SetTargetNetId(targetAmsId);

        // Autostart so the PLC actually runs (and serves ADS symbols) once the runtime reaches Run mode;
        // without it the application stays loaded-but-stopped and run_tests polls time out.
        if (bootAutostart)
        {
            var plcSysNode = sm.LookupTreeItem($"TIPC^{plc}");
            plcSysNode.BootProjectAutostart = true;
            plcSysNode.GenerateBootProject(true);
        }

        sm.ActivateConfiguration();
        return Result.Ok(new Dictionary<string, object?>
        {
            ["target"] = targetAmsId,
            ["plc"] = plc,
            ["autostart"] = bootAutostart,
        });
    }

    /// <summary>Map an Error List row to a BuildError, lifting the "C0046:"-style code off the message.</summary>
    private static (BuildError Error, string Severity) MapError(ComErrorItem row)
    {
        var severity = row.Level switch { 2 => "warning", 3 => "info", _ => "error" };
        var description = row.Description;
        var code = "";
        var match = DiagnosticCode().Match(description);
        if (match.Success)
        {
            code = match.Groups[1].Value;
            description = match.Groups[2].Value.Trim();
        }

        return (new BuildError(row.File, row.Line, description, severity, code, row.Project), severity);
    }

#pragma warning disable CA1031 // A failure inside CheckAllObjects itself is rare; treat it as a failed build.
    private static bool TryCheckAllObjects(ITcTreeItem plcProject)
    {
        try
        {
            return plcProject.CheckAllObjects();
        }
        catch (Exception)
        {
            return false;
        }
    }
#pragma warning restore CA1031

    [GeneratedRegex(@"^\s*([A-Za-z]\d{3,})\s*:\s*(.*)$")]
    private static partial Regex DiagnosticCode();
}
