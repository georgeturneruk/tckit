using System.ComponentModel;
using ModelContextProtocol.Server;
using TcKit.Core.Ports;
using TcKit.Core.Security;
using TcKit.Core.Serialization;

namespace TcKit.Server.Tools;

/// <summary>
/// Build + deploy tools (COM Automation Interface). They operate on the solution open in the
/// attached TcXaeShell. Build before deploy, always. PascalCase tool names + camelCase parameters;
/// each returns the snake_case data contract. Build is a write-class op (compiles the project);
/// Deploy is execute-class (acts on a live target) and is gated on the target NetId.
/// </summary>
[McpServerToolType]
public sealed class BuildTools(IBuildRunner builder, IPermissionGate gate)
{
    [McpServerTool(Name = "Build")]
    [Description("Build the open TwinCAT project (CheckAllObjects) and return structured errors, "
        + "warnings, and infos. forceLog also reads the Error List on a clean build (for warnings). "
        + "plcName scopes a multi-PLC solution.")]
    public Task<string> Build(bool forceLog = false, string plcName = "", CancellationToken cancellationToken = default)
        => Run(PermissionLevel.Write, null, () => builder.BuildAsync(Optional(plcName), forceLog, cancellationToken));

    [McpServerTool(Name = "Deploy")]
    [Description("Activate the configuration on a target runtime (build first). targetAmsId is the "
        + "target's AMS Net ID. bootAutostart (default true) regenerates the boot project with "
        + "autostart so the PLC actually runs and serves ADS symbols.")]
    public Task<string> Deploy(
        string targetAmsId, bool bootAutostart = true, string plcName = "", CancellationToken cancellationToken = default)
        => Run(PermissionLevel.Execute, targetAmsId, () => builder.DeployAsync(targetAmsId, Optional(plcName), bootAutostart, cancellationToken));

    private static string? Optional(string value) => string.IsNullOrEmpty(value) ? null : value;

    private async Task<string> Run<T>(PermissionLevel level, string? targetAmsId, Func<Task<T>> call)
    {
        var denied = gate.Deny(level, targetAmsId);
        if (denied is not null)
        {
            return TckitJson.Serialize(new { error = denied });
        }

        try
        {
            return TckitJson.Serialize(await call().ConfigureAwait(false));
        }
#pragma warning disable CA1031 // The tool boundary funnels every failure into the error contract.
        catch (Exception exc)
        {
            return TckitJson.Serialize(new { error = exc.Message });
        }
#pragma warning restore CA1031
    }
}
