using TcKit.Core.Models;

namespace TcKit.Adapters.Automation;

/// <summary>
/// The COM hardware verbs (read-only scan + scaffold), expressed against the <see cref="ITcSession"/>
/// seam like the rest of <see cref="ProjectAuthor"/>. scan reads the TIID I/O tree; scaffold scans then
/// reuses <see cref="AddGvl"/> to author the generated GVL atomically within the one session.
/// </summary>
internal static partial class ProjectAuthor
{
    public static HardwareTopology ScanHardware(ITcSession session) => HardwareScan.Build(session);

    public static Result ScaffoldHardwareCode(
        ITcSession session, string gvlName, string parentFolder, string? plcName)
    {
        if (string.IsNullOrEmpty(gvlName))
        {
            throw new ArgumentException("GvlName required.");
        }

        var topology = HardwareScan.Build(session);
        var (code, scaffolded, unknown) = HardwareScaffold.GenerateGvl(topology);

        // AddGvl creates the GVL and saves; it throws on failure (mapped to Result.Fail by the adapter).
        AddGvl(session, gvlName, code, parentFolder, plcName);

        var message = $"Created GVL '{gvlName}' with {scaffolded} terminal(s) scaffolded."
            + (unknown.Count > 0 ? $" Unknown terminals (add manually): {string.Join(", ", unknown)}" : "");

        return Ok(
            ("gvl_name", gvlName),
            ("plc_name", plcName),
            ("terminals_scaffolded", scaffolded),
            ("unknown_terminals", unknown),
            ("message", message));
    }
}
