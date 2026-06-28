using TcKit.Core.Models;

namespace TcKit.Core.Ports;

/// <summary>
/// Build and deploy a TwinCAT project through the Automation Interface (COM). Both operate on the
/// solution open in the attached XAE. Always build successfully before deploying. PLC-scoped methods
/// take an optional plcName (null = PLC_PROJECT_NAME env, then sole-PLC auto-resolution). See ADR-0005.
/// </summary>
public interface IBuildRunner
{
    /// <summary>
    /// Build (CheckAllObjects) the PLC project and return structured diagnostics. When the build
    /// fails, or forceLog is set, the IDE Error List is read for per-error file/line/code detail.
    /// </summary>
    Task<BuildResult> BuildAsync(string? plcName, bool forceLog, CancellationToken cancellationToken);

    /// <summary>
    /// Activate the configuration on a target runtime. When bootAutostart is true (default) the boot
    /// project is regenerated with autostart so the PLC actually runs (and serves ADS symbols) once
    /// the runtime reaches Run mode.
    /// </summary>
    Task<Result> DeployAsync(
        string targetAmsId, string? plcName, bool bootAutostart, CancellationToken cancellationToken);
}
