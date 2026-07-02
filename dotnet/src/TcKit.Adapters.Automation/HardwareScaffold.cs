using TcKit.Core.Models;

namespace TcKit.Adapters.Automation;

/// <summary>
/// Generates a GVL of <c>VAR_GLOBAL</c> I/O declarations from a scanned topology, looking each terminal
/// up in <see cref="HardwareCatalogue"/>. Variables are named <c>Slot{N}_{OrderNumber}_{Channel}</c>;
/// unknown terminals get a comment placeholder. Pure; unit-tested directly. Mirrors scaffold_hardware_code.
/// </summary>
internal static class HardwareScaffold
{
    public static (string Code, int Scaffolded, IReadOnlyList<string> Unknown) GenerateGvl(HardwareTopology topology)
    {
        var lines = new List<string> { "{attribute 'qualified_only'}", "VAR_GLOBAL" };
        var scaffolded = 0;
        var unknown = new List<string>();

        foreach (var segment in topology.Segments)
        {
            lines.Add($"\t// ---- {segment.MasterName} ----");
            foreach (var terminal in segment.Terminals)
            {
                var channels = HardwareCatalogue.Lookup(terminal.OrderNumber);
                if (channels is null)
                {
                    lines.Add($"\t// {terminal.Name} - unknown terminal; add variables manually");
                    if (!string.IsNullOrEmpty(terminal.OrderNumber))
                    {
                        unknown.Add(terminal.OrderNumber);
                    }

                    continue;
                }

                if (channels.Count == 0)
                {
                    lines.Add($"\t// {terminal.Name} - no process I/O");
                    continue;
                }

                lines.Add($"\t// {terminal.Name}");
                var prefix = $"Slot{terminal.Slot}_{terminal.OrderNumber.Replace("-", "_", StringComparison.Ordinal)}";
                foreach (var channel in channels)
                {
                    lines.Add($"\t{prefix}_{channel.Name} : {channel.StType};");
                }

                scaffolded++;
            }
        }

        lines.Add("END_VAR");
        return (string.Join("\n", lines), scaffolded, unknown);
    }
}
