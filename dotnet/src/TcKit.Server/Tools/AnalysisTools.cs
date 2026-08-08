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
    [Description("Check a TwinCAT project for problems the compiler does not catch: naming that "
        + "departs from the project's convention, and (as rules land) state-losing declarations and "
        + "unsafe patterns. Runs offline against the project files, so it needs no XAE and no "
        + "licence and is far cheaper than Build; run it before building. Pass objectName to check "
        + "just the POU you have edited, which is the intended use inside a write loop. severity is "
        + "one of error, warning, suggestion; ruleIds is a comma-separated allowlist such as "
        + "'TCK1002'. Suggestions are advisory: never rename a referenced symbol without asking.")]
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
