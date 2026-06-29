using TcKit.Core.Models;
using TcKit.Core.Ports;

namespace TcKit.Adapters.Automation;

/// <summary>
/// COM Automation Interface <see cref="IHardwareConfigurer"/>. Mirrors the other automation adapters:
/// marshals each verb onto the STA worker, opens a COM-backed <see cref="ComTcSession"/>, and delegates
/// to <see cref="ProjectAuthor"/> (COM-agnostic, unit-tested against a fake).
/// </summary>
public sealed class AutomationHardwareConfigurer : IHardwareConfigurer, IDisposable
{
    private readonly StaExecutor _sta = new();

    public Task<Result> AddEtherCatMasterAsync(string deviceName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.AddEtherCatMaster(session, deviceName));

    public Task<Result> AddEtherCatBoxAsync(
        string parentName, string boxName, string orderNumber, string before, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session =>
            ProjectAuthor.AddEtherCatBox(session, parentName, boxName, orderNumber, before));

    public Task<Result> DeleteIoDeviceAsync(string name, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, session => ProjectAuthor.DeleteIoDevice(session, name));

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
#pragma warning disable CA1031 // The configurer boundary funnels every failure into the Result error contract.
        catch (Exception ex)
        {
            return Task.FromResult(Result.Fail(ex.Message));
        }
#pragma warning restore CA1031
    }

    public void Dispose() => _sta.Dispose();
}
