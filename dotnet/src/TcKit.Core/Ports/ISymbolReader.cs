using TcKit.Core.Models;

namespace TcKit.Core.Ports;

/// <summary>Reads PLC symbol values from a running TwinCAT runtime over ADS.</summary>
public interface ISymbolReader
{
    /// <summary>
    /// Read a single symbol by its instance path (e.g. <c>MAIN.fbMotor.nState</c>).
    /// </summary>
    Task<SymbolValue> ReadAsync(string instancePath, CancellationToken cancellationToken);
}
