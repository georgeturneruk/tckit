using TcKit.Core.Security;

namespace TcKit.Core.Ports;

/// <summary>
/// The safety gate every mutating tool consults before acting. Reads the machine-local stance
/// (hot-reloaded so an in-session edit takes effect on the next call) and answers whether a given
/// operation class + target is permitted.
/// </summary>
public interface IPermissionGate
{
    /// <summary>The current stance, re-read from the backing file when it has changed on disk.</summary>
    PermissionSettings Current();

    /// <summary>
    /// Returns <c>null</c> when a call at <paramref name="required"/> level against
    /// <paramref name="targetAmsId"/> is allowed; otherwise a human-readable denial reason. The
    /// target is only consulted for <see cref="PermissionLevel.Execute"/> calls (blocklist first,
    /// then allowlist).
    /// </summary>
    string? Deny(PermissionLevel required, string? targetAmsId = null);

    /// <summary>
    /// Persist a soft change to the stance and return the new settings. A null argument leaves that
    /// facet unchanged; a non-null <paramref name="allowedNetIds"/> replaces the allowlist (pass an
    /// empty list to clear it). <paramref name="addBlockedNetIds"/> only ever appends — a blocked
    /// NetId is never removed here, so the hard guard cannot be lifted by a tool call.
    /// </summary>
    PermissionSettings Apply(
        PermissionLevel? mode,
        IReadOnlyList<string>? allowedNetIds,
        IReadOnlyList<string>? addBlockedNetIds);
}
