using TcKit.Core.Models;

namespace TcKit.Core.Ports;

/// <summary>
/// TwinCAT runtime mode control over ADS (no COM / XAE). Targets a runtime by AMS Net ID via the
/// system service. Distinct from the symbol-I/O runtime adapter; this is the run/config transition
/// that deploy + run_tests depend on.
/// </summary>
public interface IRuntimeControl
{
    /// <summary>Restart the target into Run mode (WriteControl on the system service), waiting until reached.</summary>
    Task<Result> StartRuntimeAsync(string targetAmsId, CancellationToken cancellationToken);
}
