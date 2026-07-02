using TcKit.Core.Models;
using TcKit.Core.Ports;

namespace TcKit.Adapters.Automation;

/// <summary>
/// COM Automation Interface <see cref="IProjectWriter"/>. Thin shell: it marshals each verb onto the
/// STA worker thread, opens a COM-backed <see cref="ComTcSession"/>, and delegates the actual
/// authoring to <see cref="ProjectAuthor"/> (which is COM-agnostic and unit-tested against a fake).
/// Domain errors thrown by the author are mapped to the <see cref="Result"/> error contract.
/// </summary>
public sealed class AutomationProjectWriter : IProjectWriter, IDisposable
{
    private readonly StaExecutor _sta = new();

    public Task<Result> OpenProjectAsync(string solutionPath, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.OpenProject(session, solutionPath));

    public Task<Result> AddPouAsync(
        string name, PouType pouType, string code, string parentFolder, string? plcName,
        CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session =>
            ProjectAuthor.AddPou(session, name, pouType, code, parentFolder, plcName));

    public Task<Result> AddFolderAsync(
        string name, string parentPath, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.AddFolder(session, name, parentPath, plcName));

    public Task<Result> AddGvlAsync(
        string name, string code, string parentFolder, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.AddGvl(session, name, code, parentFolder, plcName));

    public Task<Result> AddDutAsync(
        string name, string code, DutKind dutKind, string parentFolder, string? plcName,
        CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session =>
            ProjectAuthor.AddDut(session, name, code, dutKind, parentFolder, plcName));

    public Task<Result> AddMethodAsync(
        string pouName, string methodName, string code, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.AddMethod(session, pouName, methodName, code, plcName));

    public Task<Result> AddPropertyAsync(
        string pouName, string propertyName, string returnType,
        string? getterCode, string? setterCode, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session =>
            ProjectAuthor.AddProperty(session, pouName, propertyName, returnType, getterCode, setterCode, plcName));

    public Task<Result> UpdatePouDeclarationAsync(
        string pouName, string code, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.UpdatePouDeclaration(session, pouName, code, plcName));

    public Task<Result> UpdatePouImplementationAsync(
        string pouName, string code, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.UpdatePouImplementation(session, pouName, code, plcName));

    public Task<Result> UpdateMethodBodyAsync(
        string pouName, string methodName, string code, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.UpdateMethodBody(session, pouName, methodName, code, plcName));

    public Task<Result> UpdatePouDeclarationPatchAsync(
        string pouName, string oldString, string newString, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session =>
            ProjectAuthor.UpdatePouDeclarationPatch(session, pouName, oldString, newString, plcName));

    public Task<Result> UpdatePouImplementationPatchAsync(
        string pouName, string oldString, string newString, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session =>
            ProjectAuthor.UpdatePouImplementationPatch(session, pouName, oldString, newString, plcName));

    public Task<Result> UpdateMethodBodyPatchAsync(
        string pouName, string methodName, string oldString, string newString, string? plcName,
        CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session =>
            ProjectAuthor.UpdateMethodBodyPatch(session, pouName, methodName, oldString, newString, plcName));

    public Task<Result> DeletePouAsync(string name, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.DeletePou(session, name, plcName));

    public Task<Result> DeleteMethodAsync(
        string pouName, string methodName, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.DeleteMethod(session, pouName, methodName, plcName));

    public Task<Result> DeletePropertyAsync(
        string pouName, string propertyName, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.DeleteProperty(session, pouName, propertyName, plcName));

    public Task<Result> DeleteGvlAsync(string name, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.DeleteGvl(session, name, plcName));

    public Task<Result> DeleteDutAsync(string name, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.DeleteDut(session, name, plcName));

    public Task<Result> DeleteFolderAsync(
        string name, string parentPath, bool recursive, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.DeleteFolder(session, name, parentPath, recursive, plcName));

    public Task<Result> AddVariableAsync(
        string pouName, string scope, string declaration, string? itemName, string? plcName,
        CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.AddVariable(session, pouName, scope, declaration, itemName, plcName));

    public Task<Result> DeleteVariableAsync(
        string pouName, string variableName, string? itemName, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.DeleteVariable(session, pouName, variableName, itemName, plcName));

    public Task<Result> CreateProjectAsync(string name, string path, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.CreateProject(session, name, path));

    public Task<Result> AddPlcProjectAsync(
        string solutionPath, string plcName, string projectType, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.AddPlcProject(session, solutionPath, plcName, projectType));

    public Task<Result> AddLibraryReferenceAsync(
        string? plcName, string libraryName, string version, string distributor, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session =>
            ProjectAuthor.AddLibraryReference(session, plcName, libraryName, version, distributor));

    public Task<Result> DeleteLibraryReferenceAsync(
        string? plcName, string libraryName, string version, string distributor, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session =>
            ProjectAuthor.DeleteLibraryReference(session, plcName, libraryName, version, distributor));

    public Task<Result> AddLibraryPlaceholderAsync(
        string? plcName, string placeholderName, string defaultLibrary, string version, string distributor,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? parameters, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.AddLibraryPlaceholder(
            session, plcName, placeholderName, defaultLibrary, version, distributor, parameters));

    public Task<Result> SetPlaceholderParametersAsync(
        string? plcName, string placeholderName,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> parameters, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session =>
            ProjectAuthor.SetPlaceholderParameters(session, plcName, placeholderName, parameters));

    public Task<Result> DeletePlaceholderAsync(string? plcName, string placeholderName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.DeletePlaceholder(session, plcName, placeholderName));

    public Task<Result> SavePlcAsLibraryAsync(
        string? plcName, string outputPath, bool install, string repository, bool overwrite,
        CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session =>
            ProjectAuthor.SavePlcAsLibrary(session, plcName, outputPath, install, repository, overwrite));

    private Task<Result> RunAsync(CancellationToken cancellationToken, Func<ITcSession, Result> author)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(_sta.Run(() =>
            {
                using var session = new ComTcSession();
                return author(session);
            }));
        }
#pragma warning disable CA1031 // The writer boundary funnels every failure into the Result error contract.
        catch (Exception ex)
        {
            return Task.FromResult(Result.Fail(ex.Message));
        }
#pragma warning restore CA1031
    }

    public void Dispose() => _sta.Dispose();
}
