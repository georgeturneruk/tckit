using TcKit.Core.Models;
using TcKit.Core.Ports;

namespace TcKit.Adapters.Automation;

/// <summary>
/// COM Automation Interface <see cref="IHardwareScanner"/>. Mirrors <see cref="AutomationProjectWriter"/>:
/// marshals each operation onto the STA worker, opens a COM-backed <see cref="ComTcSession"/>, and
/// delegates to <see cref="ProjectAuthor"/> (COM-agnostic, unit-tested against a fake).
/// </summary>
public sealed class AutomationHardwareScanner : IHardwareScanner, IDisposable
{
    private readonly StaExecutor _sta = new();

    public Task<HardwareTopology> ScanHardwareAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Read errors propagate to the tool boundary (which renders the error contract); no Result here.
        return Task.FromResult(_sta.Run(() =>
        {
            using var session = new ComTcSession();
            return ProjectAuthor.ScanHardware(session);
        }));
    }

    public Task<Result> ScaffoldHardwareCodeAsync(
        string gvlName, string? plcName, string parentFolder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(_sta.Run(() =>
            {
                using var session = new ComTcSession();
                return ProjectAuthor.ScaffoldHardwareCode(session, gvlName, parentFolder, plcName);
            }));
        }
#pragma warning disable CA1031 // The scaffold boundary funnels every failure into the Result error contract.
        catch (Exception ex)
        {
            return Task.FromResult(Result.Fail(ex.Message));
        }
#pragma warning restore CA1031
    }

    public void Dispose() => _sta.Dispose();
}
