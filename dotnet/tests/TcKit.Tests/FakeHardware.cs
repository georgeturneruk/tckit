using TcKit.Adapters.Ads;

namespace TcKit.Tests;

/// <summary>
/// In-memory fake of the hardware source seam. Serves canned raw reads so the decoding and
/// orchestration in <c>TwinSharpHardwareInspector</c> are testable without a live TwinCAT system.
/// </summary>
internal sealed class FakeHardwareSource : IHardwareSource
{
    public List<EcMasterIdentityRaw> Masters { get; } = [];
    public EcStatusRaw Status { get; set; } = new(0, []);
    public IpcRaw Ipc { get; set; } = new(null, null, null, [], [], null);
    public List<AxisRaw> Axes { get; } = [];

    public string? RequestedMasterNetId { get; private set; }
    public uint? RequestedAxisId { get; private set; }

    public IReadOnlyList<EcMasterIdentityRaw> ListMasters(string targetAmsId) => Masters;

    public EcStatusRaw ReadEtherCatStatus(string targetAmsId, string masterNetId)
    {
        RequestedMasterNetId = masterNetId;
        return Status;
    }

    public IpcRaw ReadIpc(string targetAmsId) => Ipc;

    public IReadOnlyList<AxisRaw> ReadAxes(string targetAmsId, uint? axisId)
    {
        RequestedAxisId = axisId;
        var axes = axisId is null ? Axes : Axes.Where(a => a.Id == axisId.Value);
        return axes.ToList();
    }
}
