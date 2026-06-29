using TcKit.Adapters.Automation;

namespace TcKit.Tests;

/// <summary>
/// I/O-authoring verbs against the fake seam: adding an EtherCAT master (subtype 111) under TIID,
/// adding boxes/terminals (subtype 9099, order number as vInfo) under a named parent, and deleting
/// I/O items by name.
/// </summary>
public sealed class HardwareConfigTests
{
    private const int EtherCatMasterSubType = 111;
    private const int EtherCatBoxSubType = 9099;

    [Fact]
    public void AddEtherCatMaster_CreatesDeviceUnderTiid()
    {
        var (session, tiid) = BuildSession();

        var result = ProjectAuthor.AddEtherCatMaster(session, "Device 1 (EtherCAT)");

        Assert.True(result.Success);
        var device = tiid.FindDirect("Device 1 (EtherCAT)");
        Assert.NotNull(device);
        Assert.Equal(EtherCatMasterSubType, device!.Kind);
        Assert.Equal("TIID^Device 1 (EtherCAT)", result.Details["path"]);
        Assert.True(session.SaveCount > 0);
    }

    [Fact]
    public void AddEtherCatBox_NestsUnderNamedParent_WithOrderNumberVInfo()
    {
        var (session, tiid) = BuildSession();
        var master = tiid.Add(new FakeTreeItem("Device 1 (EtherCAT)", EtherCatMasterSubType));
        var coupler = master.Add(new FakeTreeItem("Box 1 (EK1100)", EtherCatBoxSubType));

        var result = ProjectAuthor.AddEtherCatBox(session, "Box 1 (EK1100)", "Term 2 (EL1008)", "EL1008", "");

        Assert.True(result.Success);
        var box = coupler.FindDirect("Term 2 (EL1008)");
        Assert.NotNull(box);
        Assert.Equal(EtherCatBoxSubType, box!.Kind);
        Assert.Equal("EL1008", box.VInfo); // order number passed as vInfo
        Assert.Equal("EL1008", result.Details["order_number"]);
    }

    [Fact]
    public void AddEtherCatBox_ParentNotFound_Throws()
    {
        var (session, _) = BuildSession();

        Assert.Throws<InvalidOperationException>(
            () => ProjectAuthor.AddEtherCatBox(session, "No Such Box", "Term 2 (EL1008)", "EL1008", ""));
    }

    [Fact]
    public void AddEtherCatBox_MissingOrderNumber_Throws()
    {
        var (session, tiid) = BuildSession();
        tiid.Add(new FakeTreeItem("Device 1 (EtherCAT)", EtherCatMasterSubType));

        Assert.Throws<ArgumentException>(
            () => ProjectAuthor.AddEtherCatBox(session, "Device 1 (EtherCAT)", "Box 1", "", ""));
    }

    [Fact]
    public void DeleteIoDevice_RemovesNamedItem()
    {
        var (session, tiid) = BuildSession();
        tiid.Add(new FakeTreeItem("Device 1 (EtherCAT)", EtherCatMasterSubType));

        var result = ProjectAuthor.DeleteIoDevice(session, "Device 1 (EtherCAT)");

        Assert.True(result.Success);
        Assert.Null(tiid.FindDirect("Device 1 (EtherCAT)"));
        Assert.Equal("TIID", result.Details["parent_path"]);
    }

    [Fact]
    public void DeleteIoDevice_NestedBox_RemovesFromCoupler()
    {
        var (session, tiid) = BuildSession();
        var coupler = tiid
            .Add(new FakeTreeItem("Device 1 (EtherCAT)", EtherCatMasterSubType))
            .Add(new FakeTreeItem("Box 1 (EK1100)", EtherCatBoxSubType));
        coupler.Add(new FakeTreeItem("Term 2 (EL1008)", EtherCatBoxSubType));

        var result = ProjectAuthor.DeleteIoDevice(session, "Term 2 (EL1008)");

        Assert.True(result.Success);
        Assert.Null(coupler.FindDirect("Term 2 (EL1008)"));
        Assert.Equal("TIID^Device 1 (EtherCAT)^Box 1 (EK1100)", result.Details["parent_path"]);
    }

    [Fact]
    public void DeleteIoDevice_NotFound_Throws()
    {
        var (session, _) = BuildSession();

        Assert.Throws<InvalidOperationException>(() => ProjectAuthor.DeleteIoDevice(session, "Ghost"));
    }

    [Fact]
    public void AddEtherCatMaster_EmptyName_Throws()
    {
        var (session, _) = BuildSession();

        Assert.Throws<ArgumentException>(() => ProjectAuthor.AddEtherCatMaster(session, ""));
    }

    /// <summary>A fake session whose first sysmanager exposes an (initially empty) TIID I/O tree.</summary>
    private static (FakeSession Session, FakeTreeItem Tiid) BuildSession()
    {
        var tiid = new FakeTreeItem("TIID");
        var session = new FakeSession(new FakeSysManager(new FakeTreeItem("TIPC"), tiid));
        return (session, tiid);
    }
}
