using System.ComponentModel;
using ModelContextProtocol.Server;
using TcKit.Core.Ports;
using TcKit.Core.Security;
using TcKit.Core.Serialization;

namespace TcKit.Server.Tools;

/// <summary>
/// Inspect and adjust the machine-local safety stance (mode + allowed/blocked target NetIds). These
/// are always callable regardless of the current mode, so the user can raise or lower permissions
/// mid-session; the change persists to <c>~/.tckit/permissions.json</c> and is picked up by every
/// other tool on its next call. SetPermissions can add a blocked NetId but never remove one — the
/// "never touch production" guard is lifted only by editing the file.
/// </summary>
[McpServerToolType]
public sealed class PermissionsTool(IPermissionGate gate)
{
    [McpServerTool(Name = "GetPermissions")]
    [Description("Show the current safety stance: the permission mode (read | write | execute) and the "
        + "allowed / blocked target AMS Net ID lists. read allows inspection only; write also allows "
        + "authoring the project on disk; execute also allows acting on a live target (deploy, start "
        + "runtime, run tests, symbol writes, RPC).")]
    public string GetPermissions() => TckitJson.Serialize(Snapshot(gate.Current()));

    [McpServerTool(Name = "SetPermissions")]
    [Description("Change the safety stance and persist it. mode (read | write | execute) sets the "
        + "permission tier; empty leaves it unchanged. allowedNetIds is a comma-separated allowlist of "
        + "target AMS Net IDs that replaces the current list (empty leaves it unchanged; the literal "
        + "'none' clears it so any non-blocked target is allowed). blockNetIds is a comma-separated list "
        + "of AMS Net IDs to APPEND to the permanent blocklist. Blocked NetIds cannot be removed here — "
        + "edit ~/.tckit/permissions.json to unblock a target.")]
    public string SetPermissions(string mode = "", string allowedNetIds = "", string blockNetIds = "")
    {
        try
        {
            var next = gate.Apply(ParseMode(mode), ParseAllowed(allowedNetIds), Split(blockNetIds));
            return TckitJson.Serialize(new { success = true, permissions = Snapshot(next) });
        }
#pragma warning disable CA1031 // The tool boundary funnels every failure into the error contract.
        catch (Exception exc)
        {
            return TckitJson.Serialize(new { error = exc.Message });
        }
#pragma warning restore CA1031
    }

    private static object Snapshot(PermissionSettings settings) => new
    {
        mode = settings.Mode.ToString().ToLowerInvariant(),
        allowed_net_ids = settings.AllowedNetIds,
        blocked_net_ids = settings.BlockedNetIds,
    };

    private static PermissionLevel? ParseMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "" => null,
        "read" => PermissionLevel.Read,
        "write" => PermissionLevel.Write,
        "execute" => PermissionLevel.Execute,
        _ => throw new ArgumentException($"Unknown mode '{value}'. Use read | write | execute."),
    };

    // Empty means "leave unchanged" (null); the literal "none" clears the allowlist (empty list).
    private static IReadOnlyList<string>? ParseAllowed(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase) ? [] : Split(value);
    }

    private static IReadOnlyList<string> Split(string csv) => csv
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
