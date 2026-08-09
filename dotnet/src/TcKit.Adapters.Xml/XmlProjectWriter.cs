using TcKit.Core.Models;
using TcKit.Core.Ports;

namespace TcKit.Adapters.Xml;

/// <summary>
/// The deterministic XML writer backend (ADR-0017): <see cref="IProjectWriter"/> implemented as
/// direct edits of the on-disk TwinCAT files. No COM, no running XAE, runs anywhere the files do.
/// Selected per session (never as a per-call fallback: an attached XAE would regenerate files
/// from its stale in-memory tree and silently revert interleaved edits). The "open solution" is
/// process state here: <see cref="OpenProjectAsync"/> sets it, the TCKIT_SOLUTION environment
/// variable seeds it for one-shot hosts like the CLI. Not supported by this backend:
/// CreateProject / AddPlcProject (needs the XAE template machinery, v1) and SavePlcAsLibrary
/// (needs the TwinCAT compiler).
/// </summary>
public sealed class XmlProjectWriter : IProjectWriter, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _solutionPath;

    public Task<Result> OpenProjectAsync(string solutionPath, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () =>
        {
            var full = Path.GetFullPath(solutionPath);
            if (!File.Exists(full))
            {
                throw new InvalidOperationException($"Solution file not found: {solutionPath}");
            }

            _solutionPath = full;
            return Result.Ok(new Dictionary<string, object?> { ["solution_path"] = full });
        });

    public Task<Result> AddPouAsync(
        string name, PouType pouType, string code, string parentFolder, string? plcName,
        CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.AddPou(Context(plcName), name, pouType, code, parentFolder));

    public Task<Result> AddFolderAsync(string name, string parentPath, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.AddFolder(Context(plcName), name, parentPath));

    public Task<Result> AddGvlAsync(
        string name, string code, string parentFolder, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.AddGvl(Context(plcName), name, code, parentFolder));

    public Task<Result> AddDutAsync(
        string name, string code, DutKind dutKind, string parentFolder, string? plcName,
        CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.AddDut(Context(plcName), name, code, dutKind, parentFolder));

    public Task<Result> AddMethodAsync(
        string pouName, string methodName, string code, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.AddMethod(Context(plcName), pouName, methodName, code));

    public Task<Result> AddPropertyAsync(
        string pouName, string propertyName, string returnType,
        string? getterCode, string? setterCode, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.AddProperty(
            Context(plcName), pouName, propertyName, returnType, getterCode, setterCode));

    public Task<Result> UpdatePouDeclarationAsync(
        string pouName, string code, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.UpdatePouDeclaration(Context(plcName), pouName, code));

    public Task<Result> UpdatePouImplementationAsync(
        string pouName, string code, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.UpdatePouImplementation(Context(plcName), pouName, code));

    public Task<Result> UpdateMethodBodyAsync(
        string pouName, string methodName, string code, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.UpdateMethodBody(Context(plcName), pouName, methodName, code));

    public Task<Result> UpdatePouDeclarationPatchAsync(
        string pouName, string oldString, string newString, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.UpdatePouDeclarationPatch(
            Context(plcName), pouName, oldString, newString));

    public Task<Result> UpdatePouImplementationPatchAsync(
        string pouName, string oldString, string newString, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.UpdatePouImplementationPatch(
            Context(plcName), pouName, oldString, newString));

    public Task<Result> UpdateMethodBodyPatchAsync(
        string pouName, string methodName, string oldString, string newString, string? plcName,
        CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.UpdateMethodBodyPatch(
            Context(plcName), pouName, methodName, oldString, newString));

    public Task<Result> DeletePouAsync(string name, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.DeletePou(Context(plcName), name));

    public Task<Result> DeleteMethodAsync(
        string pouName, string methodName, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.DeleteMethod(Context(plcName), pouName, methodName));

    public Task<Result> DeletePropertyAsync(
        string pouName, string propertyName, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.DeleteProperty(Context(plcName), pouName, propertyName));

    public Task<Result> DeleteGvlAsync(string name, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.DeleteGvl(Context(plcName), name));

    public Task<Result> DeleteDutAsync(string name, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.DeleteDut(Context(plcName), name));

    public Task<Result> DeleteFolderAsync(
        string name, string parentPath, bool recursive, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.DeleteFolder(Context(plcName), name, parentPath, recursive));

    public Task<Result> AddVariableAsync(
        string pouName, string scope, string declaration, string? itemName, string? plcName,
        CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.AddVariable(
            Context(plcName), pouName, scope, declaration, itemName));

    public Task<Result> DeleteVariableAsync(
        string pouName, string variableName, string? itemName, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.DeleteVariable(
            Context(plcName), pouName, variableName, itemName));

    // --- project scaffolding: not supported by this backend ----------------------

    public Task<Result> CreateProjectAsync(string name, string path, CancellationToken cancellationToken)
        => Task.FromResult(Result.Fail(
            "CreateProject is not supported by the xml writer backend; it needs XAE's project "
            + "templates. Use the automation backend (TCKIT_WRITER=automation on a Windows box with XAE)."));

    public Task<Result> AddPlcProjectAsync(
        string solutionPath, string plcName, string projectType, CancellationToken cancellationToken)
        => Task.FromResult(Result.Fail(
            "AddPlcProject is not supported by the xml writer backend; it needs XAE's project "
            + "templates. Use the automation backend (TCKIT_WRITER=automation on a Windows box with XAE)."));

    // --- library references / placeholders ---------------------------------------

    public Task<Result> AddLibraryReferenceAsync(
        string? plcName, string libraryName, string version, string distributor,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? parameters,
        CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.AddLibraryReference(
            Context(plcName), libraryName, version, distributor, parameters));

    public Task<Result> DeleteLibraryReferenceAsync(
        string? plcName, string libraryName, string version, string distributor, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.DeleteLibraryReference(
            Context(plcName), libraryName, version, distributor));

    public Task<Result> AddLibraryPlaceholderAsync(
        string? plcName, string placeholderName, string defaultLibrary, string version, string distributor,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? parameters,
        CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.AddLibraryPlaceholder(
            Context(plcName), placeholderName, defaultLibrary, version, distributor, parameters));

    public Task<Result> SetPlaceholderParametersAsync(
        string? plcName, string placeholderName,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> parameters,
        CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.SetPlaceholderParameters(
            Context(plcName), placeholderName, parameters));

    public Task<Result> DeletePlaceholderAsync(
        string? plcName, string placeholderName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () => XmlProjectAuthor.DeletePlaceholder(Context(plcName), placeholderName));

    public Task<Result> SavePlcAsLibraryAsync(
        string? plcName, string outputPath, bool install, string repository, bool overwrite,
        CancellationToken cancellationToken)
        => Task.FromResult(Result.Fail(
            "SavePlcAsLibrary is not supported by the xml writer backend; producing a .library "
            + "needs the TwinCAT compiler. Use the automation backend (TCKIT_WRITER=automation on "
            + "a Windows box with XAE)."));

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// The solution the verbs target: what OpenProject set, else the TCKIT_SOLUTION environment
    /// variable (one-shot hosts like the CLI run a fresh process per verb, so in-memory state
    /// alone would strand every verb after open-project).
    /// </summary>
    private SolutionContext Context(string? plcName)
    {
        var solution = _solutionPath;
        if (solution is null)
        {
            var env = Environment.GetEnvironmentVariable("TCKIT_SOLUTION")?.Trim();
            if (!string.IsNullOrEmpty(env))
            {
                var full = Path.GetFullPath(env);
                if (!File.Exists(full))
                {
                    throw new InvalidOperationException($"TCKIT_SOLUTION points at a missing file: {env}");
                }

                solution = full;
            }
        }

        if (solution is null)
        {
            throw new InvalidOperationException(
                "No solution is open. Call OpenProject (or set TCKIT_SOLUTION) first.");
        }

        return SolutionContext.Resolve(solution, plcName, Environment.GetEnvironmentVariable("PLC_PROJECT_NAME"));
    }

    private async Task<Result> RunAsync(CancellationToken cancellationToken, Func<Result> verb)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return verb();
        }
#pragma warning disable CA1031 // The writer boundary funnels every failure into the Result error contract.
        catch (Exception exc)
        {
            return Result.Fail(exc.Message);
        }
#pragma warning restore CA1031
        finally
        {
            _gate.Release();
        }
    }
}
