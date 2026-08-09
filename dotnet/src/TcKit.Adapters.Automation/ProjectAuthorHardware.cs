using TcKit.Core.Models;

namespace TcKit.Adapters.Automation;

/// <summary>
/// The COM hardware verbs (read-only scan + scaffold + I/O authoring), expressed against the
/// <see cref="ITcSession"/> seam like the rest of <see cref="ProjectAuthor"/>. Every verb resolves an
/// explicit TwinCAT project (via <see cref="ResolveIoSysManager"/>) rather than silently acting on the
/// first one, and each write persists that project to disk immediately (<see cref="ITcSysManager.Save"/>).
/// </summary>
internal static partial class ProjectAuthor
{
    // Automation Interface CreateChild subtypes for the I/O tree (validated live on a 4026).
    private const int EtherCatMasterSubType = 111;
    private const int EtherCatBoxSubType = 9099;

    public static HardwareTopology ScanHardware(ITcSession session, string? projectName)
    {
        var sm = ResolveIoSysManager(session, projectName);
        return HardwareScan.Build(sm);
    }

    public static Result AddEtherCatMaster(ITcSession session, string deviceName, string? projectName)
    {
        if (string.IsNullOrEmpty(deviceName))
        {
            throw new ArgumentException("DeviceName required.");
        }

        var sm = ResolveIoSysManager(session, projectName);
        var device = sm.LookupTreeItem("TIID").CreateChild(deviceName, EtherCatMasterSubType, "", null);
        var path = device.PathName;
        sm.Save();
        return Ok(("project", sm.ProjectName), ("device", deviceName), ("path", path), ("kind", "ethercat_master"));
    }

    public static Result AddEtherCatBox(
        ITcSession session, string parentName, string boxName, string orderNumber, string before, string? projectName)
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

        var sm = ResolveIoSysManager(session, projectName);
        var tiid = sm.LookupTreeItem("TIID");
        var parentPath = ResolveUnique(tiid, parentName, sm.ProjectName, "parent").PathName;

        // Re-resolve the parent by path before CreateChild: the FindChild walk can stale the handle.
        var box = sm.LookupTreeItem(parentPath).CreateChild(boxName, EtherCatBoxSubType, before ?? "", orderNumber);
        var path = box.PathName;
        sm.Save();
        return Ok(
            ("project", sm.ProjectName), ("box", boxName), ("order_number", orderNumber),
            ("parent", parentName), ("path", path));
    }

    public static Result DeleteIoDevice(ITcSession session, string target, string? projectName, bool confirmed)
    {
        if (string.IsNullOrEmpty(target))
        {
            throw new ArgumentException("Name or path required.");
        }

        var sm = ResolveIoSysManager(session, projectName);
        var tiid = sm.LookupTreeItem("TIID");

        // Accept an exact '^'-path or a name unique under this project's TIID. Ambiguous names are
        // refused (they are how a bogus box named like a real terminal gets nuked by mistake).
        var item = target.Contains('^', StringComparison.Ordinal)
            ? sm.LookupTreeItem(target)
            : ResolveUnique(tiid, target, sm.ProjectName, "device or box");

        if (item.Name == tiid.Name)
        {
            throw new InvalidOperationException("Refusing to delete the I/O Devices (TIID) root.");
        }

        // Capture name/path/cascade as strings before any delete: Remove invalidates the item handle,
        // so reading item.Name afterwards throws "already deleted" (COM), which live validation caught.
        var name = item.Name;
        var path = item.PathName;
        var cascade = DescendantPaths(item);

        if (!confirmed)
        {
            var childNote = cascade.Count > 0 ? $" and cascade {cascade.Count} child item(s)" : "";
            return new Result
            {
                Success = false,
                Error = $"Delete requires confirmed=true. This removes '{path}' from project '{sm.ProjectName}'"
                    + $"{childNote}. Re-run with confirmed=true (or pass the exact ^-path shown).",
                Details = new Dictionary<string, object?>
                {
                    ["confirmation_required"] = true,
                    ["project"] = sm.ProjectName,
                    ["path"] = path,
                    ["cascade"] = cascade,
                },
            };
        }

        var parentPath = Remove(sm, path);
        sm.Save();
        return Ok(
            ("project", sm.ProjectName), ("name", name), ("path", path),
            ("parent_path", parentPath), ("cascade", cascade));
    }

    public static Result ScaffoldHardwareCode(
        ITcSession session, string gvlName, string parentFolder, string? plcName, string? projectName)
    {
        if (string.IsNullOrEmpty(gvlName))
        {
            throw new ArgumentException("GvlName required.");
        }

        var sm = ResolveIoSysManager(session, projectName);
        var topology = HardwareScan.Build(sm);
        var (code, scaffolded, unknown) = HardwareScaffold.GenerateGvl(topology);

        // AddGvl creates the GVL and saves the PLC project; it throws on failure (mapped to Result.Fail).
        AddGvl(session, gvlName, code, parentFolder, plcName);

        var message = $"Created GVL '{gvlName}' with {scaffolded} terminal(s) scaffolded from project "
            + $"'{sm.ProjectName}'."
            + (unknown.Count > 0 ? $" Unknown terminals (add manually): {string.Join(", ", unknown)}" : "");

        return Ok(
            ("project", sm.ProjectName),
            ("gvl_name", gvlName),
            ("plc_name", plcName),
            ("terminals_scaffolded", scaffolded),
            ("unknown_terminals", unknown),
            ("message", message));
    }

    /// <summary>
    /// The system manager that owns the target I/O tree. Mirrors
    /// <see cref="ResolvePlcName(ITcSession, string?)"/>: an explicit
    /// <paramref name="projectName"/> selects that project; a single-project solution needs no name; a
    /// multi-project solution with no name is refused (listing the projects) so I/O never lands in the
    /// wrong project silently.
    /// </summary>
    public static ITcSysManager ResolveIoSysManager(ITcSession session, string? projectName)
    {
        session.UseSolution("");
        var managers = session.GetSysManagers();
        if (managers.Count == 0)
        {
            throw new InvalidOperationException(
                "No TwinCAT System Manager found. Ensure XAE is open with a solution loaded.");
        }

        if (!string.IsNullOrEmpty(projectName))
        {
            return managers.FirstOrDefault(m => m.ProjectName == projectName)
                ?? throw new InvalidOperationException(
                    $"TwinCAT project '{projectName}' not found. Available: {ProjectList(managers)}.");
        }

        return managers.Count == 1
            ? managers[0]
            : throw new InvalidOperationException(
                $"Multiple TwinCAT projects in solution ({ProjectList(managers)}). "
                + "Pass project to target one; I/O authoring will not guess.");
    }

    private static string ProjectList(IReadOnlyList<ITcSysManager> managers)
        => string.Join(", ", managers.Select(m => m.ProjectName));

    /// <summary>Resolve a name to exactly one item under <paramref name="root"/>, refusing ambiguity.</summary>
    private static ITcTreeItem ResolveUnique(ITcTreeItem root, string name, string project, string label)
    {
        var matches = FindChildren(root, name);
        return matches.Count switch
        {
            0 => throw new InvalidOperationException(
                $"I/O {label} '{name}' not found under TIID in project '{project}'."),
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"'{name}' matches {matches.Count} items in project '{project}': "
                + $"{string.Join(", ", matches.Select(m => m.PathName))}. Pass the exact ^-path to disambiguate."),
        };
    }

    /// <summary>All items named <paramref name="name"/> in the subtree below <paramref name="root"/>
    /// (excluding the root itself, which is never a deletable target).</summary>
    private static IReadOnlyList<ITcTreeItem> FindChildren(ITcTreeItem root, string name)
    {
        var found = new List<ITcTreeItem>();
        void Walk(ITcTreeItem node)
        {
            if (node.Name == name)
            {
                found.Add(node);
            }

            for (var i = 1; i <= node.ChildCount; i++)
            {
                Walk(node.Child(i));
            }
        }

        for (var i = 1; i <= root.ChildCount; i++)
        {
            Walk(root.Child(i));
        }

        return found;
    }

    /// <summary>Every descendant path under <paramref name="item"/> (the cascade a delete would take).</summary>
    private static IReadOnlyList<string> DescendantPaths(ITcTreeItem item)
    {
        var paths = new List<string>();
        void Walk(ITcTreeItem node)
        {
            for (var i = 1; i <= node.ChildCount; i++)
            {
                var child = node.Child(i);
                paths.Add(child.PathName);
                Walk(child);
            }
        }

        Walk(item);
        return paths;
    }
}
