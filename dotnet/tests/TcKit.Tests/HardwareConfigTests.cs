using TcKit.Adapters.Automation;

namespace TcKit.Tests;

/// <summary>
/// I/O-authoring verbs against the fake seam: project targeting (never guessing in a multi-project
/// solution), master/box creation under a named TwinCAT project's TIID with a per-project save, and the
/// safe delete (project-scoped, ambiguity-refusing, confirmed-gated with a preview).
/// </summary>
public sealed class HardwareConfigTests
{
    private const int EtherCatMasterSubType = 111;
    private const int EtherCatBoxSubType = 9099;

    [Fact]
    public void AddEtherCatMaster_CreatesDeviceUnderTiid_AndSavesProject()
    {
        var (session, sm, tiid) = BuildSession();

        var result = ProjectAuthor.AddEtherCatMaster(session, "Device 1 (EtherCAT)", null);

        Assert.True(result.Success);
        var device = tiid.FindDirect("Device 1 (EtherCAT)");
        Assert.NotNull(device);
        Assert.Equal(EtherCatMasterSubType, device!.Kind);
        Assert.Equal("TIID^Device 1 (EtherCAT)", result.Details["path"]);
        Assert.True(sm.SaveCount > 0);          // per-project Project.Save(), not solution File.SaveAll
        Assert.Equal(0, session.SaveCount);
    }

    [Fact]
    public void AddEtherCatBox_NestsUnderNamedParent_WithOrderNumberVInfo()
    {
        var (session, _, tiid) = BuildSession();
        var master = tiid.Add(new FakeTreeItem("Device 1 (EtherCAT)", EtherCatMasterSubType));
        var coupler = master.Add(new FakeTreeItem("Box 1 (EK1100)", EtherCatBoxSubType));

        var result = ProjectAuthor.AddEtherCatBox(session, "Box 1 (EK1100)", "Term 2 (EL1008)", "EL1008", "", null);

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
        var (session, _, _) = BuildSession();

        Assert.Throws<InvalidOperationException>(
            () => ProjectAuthor.AddEtherCatBox(session, "No Such Box", "Term 2 (EL1008)", "EL1008", "", null));
    }

    [Fact]
    public void AddEtherCatBox_MissingOrderNumber_Throws()
    {
        var (session, _, tiid) = BuildSession();
        tiid.Add(new FakeTreeItem("Device 1 (EtherCAT)", EtherCatMasterSubType));

        Assert.Throws<ArgumentException>(
            () => ProjectAuthor.AddEtherCatBox(session, "Device 1 (EtherCAT)", "Box 1", "", "", null));
    }

    [Fact]
    public void AddEtherCatMaster_EmptyName_Throws()
    {
        var (session, _, _) = BuildSession();

        Assert.Throws<ArgumentException>(() => ProjectAuthor.AddEtherCatMaster(session, "", null));
    }

    // --- project targeting: never guess in a multi-project solution ----------

    [Fact]
    public void MultiProject_NoProjectName_Refuses_ListingProjects()
    {
        var session = BuildMultiProject(out _, out _);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectAuthor.AddEtherCatMaster(session, "Device 1 (EtherCAT)", null));
        Assert.Contains("GLR_Hardware", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Motion", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiProject_TargetsTheNamedProjectOnly()
    {
        var session = BuildMultiProject(out var glrTiid, out var motionTiid);

        var result = ProjectAuthor.AddEtherCatMaster(session, "Device 1 (EtherCAT)", "Motion");

        Assert.True(result.Success);
        Assert.Equal("Motion", result.Details["project"]);
        Assert.NotNull(motionTiid.FindDirect("Device 1 (EtherCAT)")); // landed in the named project
        Assert.Null(glrTiid.FindDirect("Device 1 (EtherCAT)"));       // NOT the first project
    }

    [Fact]
    public void UnknownProjectName_Throws()
    {
        var session = BuildMultiProject(out _, out _);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectAuthor.AddEtherCatMaster(session, "Device 1 (EtherCAT)", "Nope"));
        Assert.Contains("not found", ex.Message, StringComparison.Ordinal);
    }

    // --- safe delete: gate + preview + ambiguity + path ----------------------

    [Fact]
    public void DeleteIoDevice_Unconfirmed_PreviewsWithoutDeleting()
    {
        var (session, sm, tiid) = BuildSession();
        var master = tiid.Add(new FakeTreeItem("Device 1 (EtherCAT)", EtherCatMasterSubType));
        master.Add(new FakeTreeItem("Box 1 (EK1100)", EtherCatBoxSubType));

        var result = ProjectAuthor.DeleteIoDevice(session, "Device 1 (EtherCAT)", null, confirmed: false);

        Assert.False(result.Success);
        Assert.Equal(true, result.Details["confirmation_required"]);
        Assert.Equal("TIID^Device 1 (EtherCAT)", result.Details["path"]);
        Assert.NotNull(tiid.FindDirect("Device 1 (EtherCAT)")); // nothing deleted
        Assert.Equal(0, sm.SaveCount);
        var cascade = Assert.IsAssignableFrom<IReadOnlyList<string>>(result.Details["cascade"]);
        Assert.Contains("TIID^Device 1 (EtherCAT)^Box 1 (EK1100)", cascade);
    }

    [Fact]
    public void DeleteIoDevice_Confirmed_RemovesAndSaves()
    {
        var (session, sm, tiid) = BuildSession();
        tiid.Add(new FakeTreeItem("Device 1 (EtherCAT)", EtherCatMasterSubType));

        var result = ProjectAuthor.DeleteIoDevice(session, "Device 1 (EtherCAT)", null, confirmed: true);

        Assert.True(result.Success);
        Assert.Null(tiid.FindDirect("Device 1 (EtherCAT)"));
        Assert.Equal("TIID", result.Details["parent_path"]);
        Assert.True(sm.SaveCount > 0);
    }

    [Fact]
    public void DeleteIoDevice_NestedBox_RemovesFromCoupler()
    {
        var (session, _, tiid) = BuildSession();
        var coupler = tiid
            .Add(new FakeTreeItem("Device 1 (EtherCAT)", EtherCatMasterSubType))
            .Add(new FakeTreeItem("Box 1 (EK1100)", EtherCatBoxSubType));
        coupler.Add(new FakeTreeItem("Term 2 (EL1008)", EtherCatBoxSubType));

        var result = ProjectAuthor.DeleteIoDevice(session, "Term 2 (EL1008)", null, confirmed: true);

        Assert.True(result.Success);
        Assert.Null(coupler.FindDirect("Term 2 (EL1008)"));
        Assert.Equal("TIID^Device 1 (EtherCAT)^Box 1 (EK1100)", result.Details["parent_path"]);
    }

    [Fact]
    public void DeleteIoDevice_AmbiguousName_Refuses_ListingCandidatePaths()
    {
        var (session, _, tiid) = BuildSession();
        // Same display name under two masters — the cross-collision hazard that risked nuking originals.
        tiid.Add(new FakeTreeItem("Device 1 (EtherCAT)", EtherCatMasterSubType))
            .Add(new FakeTreeItem("Term 1 (EK1200)", EtherCatBoxSubType));
        tiid.Add(new FakeTreeItem("Device 2 (EtherCAT)", EtherCatMasterSubType))
            .Add(new FakeTreeItem("Term 1 (EK1200)", EtherCatBoxSubType));

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectAuthor.DeleteIoDevice(session, "Term 1 (EK1200)", null, confirmed: true));
        Assert.Contains("matches 2 items", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteIoDevice_ByExactPath_DisambiguatesAndRemoves()
    {
        var (session, _, tiid) = BuildSession();
        tiid.Add(new FakeTreeItem("Device 1 (EtherCAT)", EtherCatMasterSubType))
            .Add(new FakeTreeItem("Term 1 (EK1200)", EtherCatBoxSubType));
        var second = tiid.Add(new FakeTreeItem("Device 2 (EtherCAT)", EtherCatMasterSubType));
        second.Add(new FakeTreeItem("Term 1 (EK1200)", EtherCatBoxSubType));

        var result = ProjectAuthor.DeleteIoDevice(
            session, "TIID^Device 2 (EtherCAT)^Term 1 (EK1200)", null, confirmed: true);

        Assert.True(result.Success);
        Assert.Null(second.FindDirect("Term 1 (EK1200)"));
    }

    [Fact]
    public void DeleteIoDevice_NotFound_Throws()
    {
        var (session, _, _) = BuildSession();

        Assert.Throws<InvalidOperationException>(
            () => ProjectAuthor.DeleteIoDevice(session, "Ghost", null, confirmed: true));
    }

    // --- helpers -------------------------------------------------------------

    /// <summary>A single-project fake session whose sys manager exposes an (empty) TIID I/O tree.</summary>
    private static (FakeSession Session, FakeSysManager Sm, FakeTreeItem Tiid) BuildSession()
    {
        var tiid = new FakeTreeItem("TIID");
        var sm = new FakeSysManager("TwinCAT Project", new FakeTreeItem("TIPC"), tiid);
        return (new FakeSession(sm), sm, tiid);
    }

    /// <summary>Two named TwinCAT projects, each with its own TIID (the GLR_Hardware scenario).</summary>
    private static FakeSession BuildMultiProject(out FakeTreeItem glrTiid, out FakeTreeItem motionTiid)
    {
        glrTiid = new FakeTreeItem("TIID");
        motionTiid = new FakeTreeItem("TIID");
        return new FakeSession(
            new FakeSysManager("GLR_Hardware", new FakeTreeItem("TIPC"), glrTiid),
            new FakeSysManager("Motion", new FakeTreeItem("TIPC"), motionTiid));
    }
}
