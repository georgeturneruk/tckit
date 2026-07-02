using System.ComponentModel;
using ModelContextProtocol.Server;
using TcKit.Core.Ports;
using TcKit.Core.Serialization;

namespace TcKit.Server.Tools;

/// <summary>
/// Read-only hardware diagnostics over ADS (TwinSharp): EtherCAT master/slave status, IPC hardware, and
/// NC axis state. Each targets a running TwinCAT system by AMS Net ID; no XAE required. Read-only, so
/// no confirmation gate.
/// </summary>
[McpServerToolType]
public sealed class HardwareTools(IHardwareInspector hardware)
{
    [McpServerTool(Name = "ListEtherCatMasters")]
    [Description("List every EtherCAT master on a running TwinCAT system (probes AMS port 65535). Most "
        + "systems have exactly one. targetAmsId is the target's AMS Net ID; the runtime must be "
        + "reachable via ADS.")]
    public Task<string> ListEtherCatMasters(string targetAmsId, CancellationToken cancellationToken = default)
        => Run(async () =>
        {
            var masters = await hardware.ListEtherCatMastersAsync(targetAmsId, cancellationToken).ConfigureAwait(false);
            return new { success = true, masters };
        });

    [McpServerTool(Name = "GetEtherCatStatus")]
    [Description("Read the full EtherCAT status for one master: master diagnostic flags plus the slave "
        + "table (state machine, identity, link health, per-port CRC error counters). masterNetId "
        + "defaults to targetAmsId (the usual single-master layout).")]
    public Task<string> GetEtherCatStatus(
        string targetAmsId, string masterNetId = "", CancellationToken cancellationToken = default)
        => Run(async () => await hardware.GetEtherCatStatusAsync(targetAmsId, masterNetId, cancellationToken)
            .ConfigureAwait(false));

    [McpServerTool(Name = "GetIpcHardware")]
    [Description("Read IPC hardware diagnostics from a running TwinCAT system: TwinCAT version, CPU "
        + "(temperature / usage / frequency), memory, fans, network adapters, and UPS. Modules not "
        + "present are null or empty.")]
    public Task<string> GetIpcHardware(string targetAmsId, CancellationToken cancellationToken = default)
        => Run(async () => await hardware.GetIpcHardwareAsync(targetAmsId, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "ListAxes")]
    [Description("List all configured NC axes and their live state (name, error code, position, "
        + "velocity, lag error, derived state name). Empty when no NC axes are configured. The runtime "
        + "must be reachable via ADS.")]
    public Task<string> ListAxes(string targetAmsId, CancellationToken cancellationToken = default)
        => Run(async () =>
        {
            var axes = await hardware.ListAxesAsync(targetAmsId, cancellationToken).ConfigureAwait(false);
            return new { success = true, axes };
        });

    [McpServerTool(Name = "GetAxisState")]
    [Description("Read the live state of a single NC axis by ID (as returned by ListAxes). Returns the "
        + "same fields as ListAxes for one axis.")]
    public Task<string> GetAxisState(
        string targetAmsId, int axisId, CancellationToken cancellationToken = default)
        => Run(async () =>
        {
            var axis = await hardware.GetAxisStateAsync(targetAmsId, axisId, cancellationToken).ConfigureAwait(false);
            return new { success = true, axes = new[] { axis } };
        });

    private static async Task<string> Run<T>(Func<Task<T>> call)
    {
        try
        {
            return TckitJson.Serialize(await call().ConfigureAwait(false));
        }
#pragma warning disable CA1031 // The tool boundary funnels every failure into the error contract.
        catch (Exception exc)
        {
            return TckitJson.Serialize(new { error = exc.Message });
        }
#pragma warning restore CA1031
    }
}
