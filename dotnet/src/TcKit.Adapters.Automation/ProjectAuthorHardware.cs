using TcKit.Core.Models;

namespace TcKit.Adapters.Automation;

/// <summary>
/// The COM hardware verbs (read-only scan + scaffold), expressed against the <see cref="ITcSession"/>
/// seam like the rest of <see cref="ProjectAuthor"/>. scan reads the TIID I/O tree; scaffold scans then
/// reuses <see cref="AddGvl"/> to author the generated GVL atomically within the one session.
/// </summary>
internal static partial class ProjectAuthor
{
    // Automation Interface CreateChild subtypes for the I/O tree (validated live on a 4026).
    private const int EtherCatMasterSubType = 111;
    private const int EtherCatBoxSubType = 9099;

    public static HardwareTopology ScanHardware(ITcSession session) => HardwareScan.Build(session);

    public static Result AddEtherCatMaster(ITcSession session, string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
        {
            throw new ArgumentException("DeviceName required.");
        }

        var sm = FirstIoSysManager(session);
        var device = sm.LookupTreeItem("TIID").CreateChild(deviceName, EtherCatMasterSubType, "", null);
        var path = device.PathName;
        session.Save();
        return Ok(("device", deviceName), ("path", path), ("kind", "ethercat_master"));
    }

    public static Result AddEtherCatBox(
        ITcSession session, string parentName, string boxName, string orderNumber, string before)
    {
        if (string.IsNullOrEmpty(parentName))
        {
            throw new ArgumentException("ParentName required.");
        }

        if (string.IsNullOrEmpty(boxName))
        {
            throw new ArgumentException("BoxName required.");
        }

        if (string.IsNullOrEmpty(orderNumber))
        {
            throw new ArgumentException("OrderNumber required.");
        }

        var sm = FirstIoSysManager(session);
        var tiid = sm.LookupTreeItem("TIID");
        var parentPath = (FindChild(tiid, parentName)
            ?? throw new InvalidOperationException($"I/O device or box '{parentName}' not found under TIID.")).PathName;

        // Re-resolve the parent by path before CreateChild: the FindChild walk can stale the handle.
        var box = sm.LookupTreeItem(parentPath).CreateChild(boxName, EtherCatBoxSubType, before ?? "", orderNumber);
        var path = box.PathName;
        session.Save();
        return Ok(("box", boxName), ("order_number", orderNumber), ("parent", parentName), ("path", path));
    }

    public static Result DeleteIoDevice(ITcSession session, string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Name required.");
        }

        var sm = FirstIoSysManager(session);
        var tiid = sm.LookupTreeItem("TIID");
        var item = FindChild(tiid, name)
            ?? throw new InvalidOperationException($"I/O device or box '{name}' not found under TIID.");
        if (item.Name == tiid.Name)
        {
            throw new InvalidOperationException("Refusing to delete the I/O Devices (TIID) root.");
        }

        var parentPath = Remove(sm, item.PathName);
        session.Save();
        return Ok(("name", name), ("parent_path", parentPath));
    }

    /// <summary>The system manager that owns the I/O tree. Mirrors the scan: the first TwinCAT project.</summary>
    private static ITcSysManager FirstIoSysManager(ITcSession session)
    {
        session.UseSolution("");
        var managers = session.GetSysManagers();
        if (managers.Count == 0)
        {
            throw new InvalidOperationException(
                "No TwinCAT System Manager found. Ensure XAE is open with a solution loaded.");
        }

        return managers[0];
    }

    public static Result ScaffoldHardwareCode(
        ITcSession session, string gvlName, string parentFolder, string? plcName)
    {
        if (string.IsNullOrEmpty(gvlName))
        {
            throw new ArgumentException("GvlName required.");
        }

        var topology = HardwareScan.Build(session);
        var (code, scaffolded, unknown) = HardwareScaffold.GenerateGvl(topology);

        // AddGvl creates the GVL and saves; it throws on failure (mapped to Result.Fail by the adapter).
        AddGvl(session, gvlName, code, parentFolder, plcName);

        var message = $"Created GVL '{gvlName}' with {scaffolded} terminal(s) scaffolded."
            + (unknown.Count > 0 ? $" Unknown terminals (add manually): {string.Join(", ", unknown)}" : "");

        return Ok(
            ("gvl_name", gvlName),
            ("plc_name", plcName),
            ("terminals_scaffolded", scaffolded),
            ("unknown_terminals", unknown),
            ("message", message));
    }
}
