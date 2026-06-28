using TcKit.Core.Models;

namespace TcKit.Core.Ports;

/// <summary>
/// Structural writes to a TwinCAT project through the Automation Interface (COM). Every write
/// targets the solution currently open in the attached XAE; <see cref="OpenProjectAsync"/> sets it.
/// PLC-scoped methods take an optional plcName (null = PLC_PROJECT_NAME env, then sole-PLC
/// auto-resolution). Never manipulate .TcPOU / .plcproj XML directly. See ADR-0005.
///
/// This is the "create" family of the authoring lane; update / delete / library verbs land next.
/// </summary>
public interface IProjectWriter
{
    /// <summary>Open (or confirm open) a TwinCAT solution in XAE. Idempotent.</summary>
    Task<Result> OpenProjectAsync(string solutionPath, CancellationToken cancellationToken);

    /// <summary>Add a new POU (FB, program, function, or interface) under the POUs tree.</summary>
    Task<Result> AddPouAsync(
        string name, PouType pouType, string code, string parentFolder, string? plcName,
        CancellationToken cancellationToken);

    /// <summary>Add a folder under a PLC project's source tree (defaults under POUs).</summary>
    Task<Result> AddFolderAsync(
        string name, string parentPath, string? plcName, CancellationToken cancellationToken);

    /// <summary>Add a Global Variable List (declaration-only) under the POUs tree.</summary>
    Task<Result> AddGvlAsync(
        string name, string code, string parentFolder, string? plcName, CancellationToken cancellationToken);

    /// <summary>Add a Data Unit Type (struct, enum, or union) under the DUTs tree.</summary>
    Task<Result> AddDutAsync(
        string name, string code, DutKind dutKind, string parentFolder, string? plcName,
        CancellationToken cancellationToken);

    /// <summary>Add a method to an existing POU (interface POUs get an interface method).</summary>
    Task<Result> AddMethodAsync(
        string pouName, string methodName, string code, string? plcName, CancellationToken cancellationToken);

    /// <summary>
    /// Add a property to an existing POU. Supply getterCode and/or setterCode (at least one);
    /// each is the accessor body, optionally preceded by a local VAR block (no PROPERTY header).
    /// </summary>
    Task<Result> AddPropertyAsync(
        string pouName, string propertyName, string returnType,
        string? getterCode, string? setterCode, string? plcName, CancellationToken cancellationToken);
}
