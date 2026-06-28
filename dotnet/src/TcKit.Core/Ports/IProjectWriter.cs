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

    /// <summary>Replace a POU's FB-level declaration block (VAR sections / signature only).</summary>
    Task<Result> UpdatePouDeclarationAsync(
        string pouName, string code, string? plcName, CancellationToken cancellationToken);

    /// <summary>Replace a POU's cyclic implementation body (ST statements only).</summary>
    Task<Result> UpdatePouImplementationAsync(
        string pouName, string code, string? plcName, CancellationToken cancellationToken);

    /// <summary>Replace the full body (declaration + implementation) of a method, action, or property.</summary>
    Task<Result> UpdateMethodBodyAsync(
        string pouName, string methodName, string code, string? plcName, CancellationToken cancellationToken);

    /// <summary>Anchored single-occurrence replacement on a POU's declaration block.</summary>
    Task<Result> UpdatePouDeclarationPatchAsync(
        string pouName, string oldString, string newString, string? plcName, CancellationToken cancellationToken);

    /// <summary>Anchored single-occurrence replacement on a POU's implementation block.</summary>
    Task<Result> UpdatePouImplementationPatchAsync(
        string pouName, string oldString, string newString, string? plcName, CancellationToken cancellationToken);

    /// <summary>Anchored single-occurrence replacement on a method/action/property's combined source.</summary>
    Task<Result> UpdateMethodBodyPatchAsync(
        string pouName, string methodName, string oldString, string newString, string? plcName,
        CancellationToken cancellationToken);

    /// <summary>Delete a POU; refuses a PROGRAM still bound to a task via a PouCall.</summary>
    Task<Result> DeletePouAsync(string name, string? plcName, CancellationToken cancellationToken);

    /// <summary>Delete a method or action from a POU.</summary>
    Task<Result> DeleteMethodAsync(string pouName, string methodName, string? plcName, CancellationToken cancellationToken);

    /// <summary>Delete a property (and its Get/Set accessors) from a POU.</summary>
    Task<Result> DeletePropertyAsync(string pouName, string propertyName, string? plcName, CancellationToken cancellationToken);

    /// <summary>Delete a GVL (validates the item really is a GVL).</summary>
    Task<Result> DeleteGvlAsync(string name, string? plcName, CancellationToken cancellationToken);

    /// <summary>Delete a DUT (struct, enum, union, or alias).</summary>
    Task<Result> DeleteDutAsync(string name, string? plcName, CancellationToken cancellationToken);

    /// <summary>Delete a folder; refuses a non-empty folder unless recursive.</summary>
    Task<Result> DeleteFolderAsync(
        string name, string parentPath, bool recursive, string? plcName, CancellationToken cancellationToken);
}
