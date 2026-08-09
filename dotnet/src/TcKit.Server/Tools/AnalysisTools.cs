using System.ComponentModel;
using ModelContextProtocol.Server;
using TcKit.Core.Models;
using TcKit.Core.Ports;
using TcKit.Core.Serialization;

namespace TcKit.Server.Tools;

/// <summary>
/// Offline static analysis. Follows the same MCP contract as the other tool types: PascalCase tool
/// name, camelCase parameters, snake_case output via <see cref="TckitJson"/>, and failures returned
/// as the error object rather than thrown.
/// </summary>
[McpServerToolType]
public sealed class AnalysisTools(IProjectAnalyser analyser)
{
    [McpServerTool(Name = "AnalyseProject")]
    [Description("Check a TwinCAT project for problems the compiler does not catch, so a green "
        + "build is not evidence against a finding. Catches a function block instance declared on a "
        + "call stack (its state resets every call), floating-point equality, RETAIN that cannot "
        + "retain, unused locals, unread inputs, globals with more than one writer, POUs nothing "
        + "reaches, and naming that departs from the project's convention. Runs offline against the "
        + "project files: no XAE, no licence, no runtime, and far cheaper than Build, so run it "
        + "first. Pass objectName to check just the POU you have edited, which is the intended use "
        + "inside a write loop; cross-file rules are then skipped and listed in rules_not_run. "
        + "severity is error, warning or suggestion and defaults to suggestion, so pass 'warning' "
        + "for only what is actually wrong; ruleIds is a comma-separated allowlist such as "
        + "'TCK1002'. Check skipped and config_warnings before trusting a short finding list. "
        + "Suggestions are advisory: never rename a referenced symbol without asking the user.")]
    public async Task<string> AnalyseProject(
        string projectPath,
        string plcName = "",
        string objectName = "",
        string severity = "suggestion",
        string ruleIds = "",
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Enum.TryParse<DiagnosticSeverity>(severity, ignoreCase: true, out var minimum))
            {
                return TckitJson.Serialize(new
                {
                    error = $"Unknown severity '{severity}'. Use error, warning, suggestion, silent or none.",
                });
            }

            var request = new AnalysisRequest
            {
                ProjectPath = projectPath,
                PlcName = Optional(plcName),
                ObjectName = Optional(objectName),
                MinimumSeverity = minimum,
                RuleIds = ruleIds
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            };

            return TckitJson.Serialize(
                await analyser.AnalyseAsync(request, cancellationToken).ConfigureAwait(false));
        }
#pragma warning disable CA1031 // The tool boundary deliberately funnels every failure into the error contract.
        catch (Exception exc)
        {
            return TckitJson.Serialize(new { error = exc.Message });
        }
#pragma warning restore CA1031
    }

    private static string? Optional(string value) => string.IsNullOrEmpty(value) ? null : value;
}
