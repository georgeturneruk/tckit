using TcKit.Core.Models;
using TcKit.Core.Ports;

namespace TcKit.Adapters.Ads;

/// <summary>
/// TwinSharp-backed <see cref="IHardwareInspector"/>. The TwinSharp calls live behind the
/// <see cref="IHardwareSource"/> seam; this class owns the orchestration (master selection, axis lookup)
/// and maps raw reads to the Core models via <see cref="HardwareDecode"/>, so the logic is unit-tested
/// against a fake source without a live system.
/// </summary>
public sealed class TwinSharpHardwareInspector : IHardwareInspector
{
    private const int EtherCatMasterPort = 65535; // 0xFFFF

    private readonly IHardwareSource _source;

    public TwinSharpHardwareInspector()
        : this(new TwinSharpHardwareSource())
    {
    }

    internal TwinSharpHardwareInspector(IHardwareSource source) => _source = source;

    public Task<IReadOnlyList<EtherCatMasterInfo>> ListEtherCatMastersAsync(
        string targetAmsId, CancellationToken cancellationToken)
        => Run(() =>
        {
            RequireTarget(targetAmsId);
            IReadOnlyList<EtherCatMasterInfo> masters = _source.ListMasters(targetAmsId)
                .Select(m => new EtherCatMasterInfo(m.NetId, m.Name, EtherCatMasterPort))
                .ToList();
            return masters;
        }, cancellationToken);

    public Task<EtherCatStatus> GetEtherCatStatusAsync(
        string targetAmsId, string masterNetId, CancellationToken cancellationToken)
        => Run(() =>
        {
            RequireTarget(targetAmsId);
            var raw = _source.ReadEtherCatStatus(targetAmsId, string.IsNullOrEmpty(masterNetId) ? targetAmsId : masterNetId);
            return new EtherCatStatus(
                HardwareDecode.MasterState(raw.MasterDevState),
                raw.Slaves.Select(HardwareDecode.ToSlaveInfo).ToList());
        }, cancellationToken);

    public Task<IpcHardware> GetIpcHardwareAsync(string targetAmsId, CancellationToken cancellationToken)
        => Run(() =>
        {
            RequireTarget(targetAmsId);
            return HardwareDecode.ToIpcHardware(_source.ReadIpc(targetAmsId));
        }, cancellationToken);

    public Task<IReadOnlyList<AxisState>> ListAxesAsync(string targetAmsId, CancellationToken cancellationToken)
        => Run(() =>
        {
            RequireTarget(targetAmsId);
            IReadOnlyList<AxisState> axes = _source.ReadAxes(targetAmsId, axisId: null)
                .Select(HardwareDecode.ToAxisState)
                .ToList();
            return axes;
        }, cancellationToken);

    public Task<AxisState> GetAxisStateAsync(string targetAmsId, int axisId, CancellationToken cancellationToken)
        => Run(() =>
        {
            RequireTarget(targetAmsId);
            if (axisId <= 0)
            {
                throw new ArgumentException("axisId is required.");
            }

            var axis = _source.ReadAxes(targetAmsId, (uint)axisId).FirstOrDefault()
                ?? throw new InvalidOperationException($"Axis {axisId} not found or not reachable.");
            return HardwareDecode.ToAxisState(axis);
        }, cancellationToken);

    private static void RequireTarget(string targetAmsId)
    {
        if (string.IsNullOrEmpty(targetAmsId))
        {
            throw new ArgumentException("TargetAmsId required.");
        }
    }

    private static Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken)
        => Task.Run(work, cancellationToken);
}
