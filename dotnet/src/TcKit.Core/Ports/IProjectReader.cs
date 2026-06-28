using TcKit.Core.Models;

namespace TcKit.Core.Ports;

/// <summary>Read-only structural access to a TwinCAT project (offline XML parsing).</summary>
public interface IProjectReader
{
    /// <summary>
    /// Return the top-level map of POUs, GVLs, DUTs, libraries, and tasks per PLC project.
    /// </summary>
    /// <param name="projectPath">Absolute path to the solution root directory, or to a
    /// <c>.sln</c> file inside it (both forms accepted; a <c>.sln</c> path is shorthand for
    /// its parent directory).</param>
    /// <param name="plcName">When given, restrict the walk to a single PLC project; otherwise
    /// scan every <c>.plcproj</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ProjectStructure> GetStructureAsync(
        string projectPath, string? plcName, CancellationToken cancellationToken);

    /// <summary>Return declarations and method/property signatures for a POU, without bodies.</summary>
    Task<PouInterface> GetPouInterfaceAsync(
        string pouName, string? plcName, CancellationToken cancellationToken);

    /// <summary>Return only the FB-level declaration block of a POU (VAR sections, no methods).</summary>
    Task<PouDeclaration> GetPouDeclarationAsync(
        string pouName, string? plcName, CancellationToken cancellationToken);

    /// <summary>
    /// Return the declaration and body of a single method, action, or property accessor.
    /// <paramref name="itemName"/> accepts "Execute" (method/action), "Status" (property header),
    /// "Status.Get" / "Status.Set" (accessor declaration + body).
    /// </summary>
    Task<PouItem> GetPouItemAsync(
        string pouName, string itemName, string? plcName, CancellationToken cancellationToken);

    /// <summary>Return the declaration block of a Global Variable List.</summary>
    Task<Gvl> GetGvlAsync(string gvlName, string? plcName, CancellationToken cancellationToken);

    /// <summary>Return the declaration block of a Data Unit Type (struct, enum, union, alias).</summary>
    Task<Dut> GetDutAsync(string dutName, string? plcName, CancellationToken cancellationToken);
}
