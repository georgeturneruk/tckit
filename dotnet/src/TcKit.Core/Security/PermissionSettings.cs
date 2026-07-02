namespace TcKit.Core.Security;

/// <summary>
/// The machine-local safety stance: how much the server is allowed to do, and which live targets it
/// may act on. Loaded from <c>~/.tckit/permissions.json</c> (see <see cref="FilePermissionGate"/>).
/// Absence of the file means <see cref="Permissive"/> — the stance is opt-in, matching the earlier
/// Python config (all keys optional, blank == unrestricted).
/// </summary>
public sealed record PermissionSettings
{
    /// <summary>The highest operation class the server may perform. Defaults to <see cref="PermissionLevel.Execute"/>.</summary>
    public PermissionLevel Mode { get; init; } = PermissionLevel.Execute;

    /// <summary>
    /// If non-empty, an allowlist: execute-class calls are permitted only against a target whose AMS
    /// Net ID is listed here. Empty means any non-blocked target is permitted.
    /// </summary>
    public IReadOnlyList<string> AllowedNetIds { get; init; } = [];

    /// <summary>
    /// AMS Net IDs that can never be acted on from this machine. Blocking always wins over the
    /// allowlist. This is the hard "never touch production" guard and is not lifted by the runtime
    /// <c>SetPermissions</c> tool — only by editing the file.
    /// </summary>
    public IReadOnlyList<string> BlockedNetIds { get; init; } = [];

    /// <summary>The default stance when no file is present: full access, no target restrictions.</summary>
    public static PermissionSettings Permissive { get; } = new();
}
