using System.Globalization;
using System.Text.RegularExpressions;
using TcKit.Core.Models;

namespace TcKit.Adapters.Automation;

/// <summary>
/// Reads the configured EtherCAT topology from the open project's TIID (I/O Devices) tree, expressed
/// against the <see cref="ITcSession"/> seam so it runs against the in-memory fake in CI. The terminal
/// name parsing (order number, slot) and master detection are pure and unit-tested directly. Mirrors
/// Invoke-HardwareScan.ps1; does NOT trigger a physical bus scan.
/// </summary>
internal static partial class HardwareScan
{
    public static HardwareTopology Build(ITcSysManager sm)
    {
        // The I/O tree (TIID) lives at the project level; the caller resolved which project's sys manager.
        ITcTreeItem tiid;
        try
        {
            tiid = sm.LookupTreeItem("TIID");
        }
        catch (Exception exc)
        {
            throw new InvalidOperationException(
                $"Failed to access the I/O devices tree (TIID) of project '{sm.ProjectName}': {exc.Message}.");
        }

        var segments = new List<EtherCatSegment>();
        for (var d = 1; d <= tiid.ChildCount; d++)
        {
            var device = tiid.Child(d);
            var deviceName = device.Name;
            if (!IsEtherCatMaster(deviceName))
            {
                continue;
            }

            var terminals = new List<TerminalInfo>();
            var termCount = device.ChildCount;
            for (var t = 1; t <= termCount; t++)
            {
                var term = device.Child(t);
                var termName = term.Name;
                var slot = TerminalSlot(termName);
                terminals.Add(new TerminalInfo(slot == 0 ? t : slot, termName, OrderNumber(termName)));
            }

            segments.Add(new EtherCatSegment(deviceName, terminals));
        }

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return new HardwareTopology(segments, timestamp, sm.ProjectName);
    }

    /// <summary>True when a TIID device is an EtherCAT master (by name, like the bridge heuristic).</summary>
    public static bool IsEtherCatMaster(string deviceName)
        => deviceName.Contains("EtherCAT", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("EL6695", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("EK9300", StringComparison.OrdinalIgnoreCase);

    /// <summary>Extract the order number from a tree name like "Box 1 (EL1008)" -> "EL1008".</summary>
    public static string OrderNumber(string itemName)
    {
        var match = OrderNumberRegex().Match(itemName);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    /// <summary>Extract the slot from a tree name like "Box 3 (EL2008)" -> 3; 0 when not present.</summary>
    public static int TerminalSlot(string itemName)
    {
        var match = SlotRegex().Match(itemName);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    [GeneratedRegex(@"\(([^)]+)\)$")]
    private static partial Regex OrderNumberRegex();

    [GeneratedRegex(@"^(?:Box|Term|Drive|Module|Slot|Device)\s+(\d+)")]
    private static partial Regex SlotRegex();
}
