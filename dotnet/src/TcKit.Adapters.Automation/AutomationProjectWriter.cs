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
