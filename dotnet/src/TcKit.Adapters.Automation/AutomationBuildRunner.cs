using TcKit.Core.Models;
using TcKit.Core.Ports;

namespace TcKit.Adapters.Automation;

/// <summary>
/// COM Automation Interface <see cref="IBuildRunner"/>. Thin shell: marshals each verb onto the STA
/// worker thread, opens a COM-backed <see cref="ComTcSession"/>, and delegates to <see cref="ProjectBuilder"/>
/// (COM-agnostic, unit-tested against a fake). Domain errors are mapped to the result contracts.
/// </summary>
public sealed class AutomationBuildRunner : IBuildRunner, IDisposable
{
    private readonly StaExecutor _sta = new();

    public Task<BuildResult> BuildAsync(string? plcName, bool forceLog, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectBuilder.Build(session, plcName, forceLog), BuildResult.Fail);

    public Task<Result> DeployAsync(
        string targetAmsId, string? plcName, bool bootAutostart, CancellationToken cancellationToken)
        => RunAsync(
            cancellationToken,
            session => ProjectBuilder.Deploy(session, targetAmsId, plcName, bootAutostart),
            Result.Fail);

    private Task<T> RunAsync<T>(CancellationToken cancellationToken, Func<ITcSession, T> op, Func<string, T> onError)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(_sta.Run(() =>
            {
                using var session = new ComTcSession();
                // The compile reads XAE's in-memory model, so spliced library-parameter blocks
                // must be on disk AND loaded before anything builds; restore any a save dropped.
                ParameterGuard.VerifyOrRestore(session);
                return op(session);
            }));
        }
#pragma warning disable CA1031 // The build boundary funnels every failure into the result error contract.
        catch (Exception ex)
        {
            return Task.FromResult(onError(ex.Message));
        }
#pragma warning restore CA1031
    }

    public void Dispose() => _sta.Dispose();
}
