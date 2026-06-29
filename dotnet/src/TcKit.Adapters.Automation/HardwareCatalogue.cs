namespace TcKit.Adapters.Automation;

/// <summary>One process-data channel on a terminal: variable name, ST type, and direction (IN/OUT).</summary>
internal sealed record HardwareChannel(string Name, string StType, string Direction);

/// <summary>
/// Bundled catalogue of common Beckhoff EtherCAT terminal I/O channels, keyed by order number. A
/// lookup miss (unknown terminal) returns null; a known terminal with no process I/O (coupler / power
/// supply) returns an empty list. Ported from hardware_catalogue.py; covers the common digital/analog
/// terminals — for exhaustive I/O use the Beckhoff infosys docs.
/// </summary>
internal static class HardwareCatalogue
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<HardwareChannel>> Catalogue = Build();

    /// <summary>
    /// Return the channel list for <paramref name="orderNumber"/>, or null if unknown. The lookup is
    /// case-insensitive and ignores any suffix after the first space or hyphen (so "EL1008-0000" and
    /// "EL1008 0000" both resolve to "EL1008"). Returns an empty list (not null) for I/O-less terminals.
    /// </summary>
    public static IReadOnlyList<HardwareChannel>? Lookup(string orderNumber)
    {
        var key = orderNumber.Trim().ToUpperInvariant().Split(' ', '-')[0];
        return Catalogue.TryGetValue(key, out var channels) ? channels : null;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<HardwareChannel>> Build()
    {
        var map = new Dictionary<string, IReadOnlyList<HardwareChannel>>(StringComparer.Ordinal);

        // Couplers / power supplies (no process I/O).
        foreach (var coupler in new[]
        {
            "EK1100", "EK1101", "EK1110", "EK1122", "EK1501", "EK9000", "EK9300",
            "EL9505", "EL9410", "EL9560",
        })
        {
            map[coupler] = [];
        }

        // Digital inputs.
        map["EL1002"] = Bits("Input", "IN", 2);
        map["EL1004"] = Bits("Input", "IN", 4);
        map["EL1008"] = Bits("Input", "IN", 8);
        map["EL1012"] = Bits("Input", "IN", 2);
        map["EL1014"] = Bits("Input", "IN", 4);
        map["EL1018"] = Bits("Input", "IN", 8);
        map["EL1024"] = Bits("Input", "IN", 4);
        map["EL1034"] = Bits("Input", "IN", 4);
        map["EL1088"] = Bits("Input", "IN", 8);

        // Digital outputs.
        map["EL2002"] = Bits("Output", "OUT", 2);
        map["EL2004"] = Bits("Output", "OUT", 4);
        map["EL2008"] = Bits("Output", "OUT", 8);
        map["EL2024"] = Bits("Output", "OUT", 4);
        map["EL2034"] = Bits("Output", "OUT", 4);
        map["EL2088"] = Bits("Output", "OUT", 8);
        map["EL2124"] = Bits("Output", "OUT", 4);

        // Mixed digital I/O.
        map["EL1809"] = Bits("Input", "IN", 16);
        map["EL2809"] = Bits("Output", "OUT", 16);

        // Analog inputs.
        map["EL3001"] = Channels("Analog_Input", "INT", "IN", 1);
        map["EL3002"] = Channels("Analog_Input", "INT", "IN", 2);
        map["EL3004"] = Channels("Analog_Input", "INT", "IN", 4);
        map["EL3008"] = Channels("Analog_Input", "INT", "IN", 8);
        map["EL3102"] = Channels("Analog_Input", "INT", "IN", 2);
        map["EL3104"] = Channels("Analog_Input", "INT", "IN", 4);
        map["EL3112"] = Channels("Analog_Input", "INT", "IN", 2);
        map["EL3152"] = Channels("Analog_Input", "INT", "IN", 2);
        map["EL3162"] = Channels("Analog_Input", "INT", "IN", 2);

        // Analog outputs.
        map["EL4001"] = Channels("Analog_Output", "INT", "OUT", 1);
        map["EL4002"] = Channels("Analog_Output", "INT", "OUT", 2);
        map["EL4004"] = Channels("Analog_Output", "INT", "OUT", 4);
        map["EL4008"] = Channels("Analog_Output", "INT", "OUT", 8);
        map["EL4012"] = Channels("Analog_Output", "INT", "OUT", 2);
        map["EL4032"] = Channels("Analog_Output", "INT", "OUT", 2);
        map["EL4132"] = Channels("Analog_Output", "INT", "OUT", 2);

        // Encoder / position.
        map["EL5001"] = [new("Position", "DWORD", "IN"), new("Status", "WORD", "IN")];
        map["EL5002"] =
        [
            new("Position_1", "DWORD", "IN"), new("Status_1", "WORD", "IN"),
            new("Position_2", "DWORD", "IN"), new("Status_2", "WORD", "IN"),
        ];
        map["EL5101"] = [new("Position", "DWORD", "IN"), new("Velocity", "INT", "IN")];
        map["EL5151"] = [new("Position", "DWORD", "IN"), new("Velocity", "INT", "IN")];

        // Serial / fieldbus.
        map["EL6001"] = [new("Data_In", "BYTE", "IN"), new("Data_Out", "BYTE", "OUT")];
        map["EL6002"] = [new("Data_In_1", "BYTE", "IN"), new("Data_Out_1", "BYTE", "OUT")];

        // Safety.
        map["EL1904"] = Bits("Safety_Input", "IN", 4);
        map["EL2904"] = Bits("Safety_Output", "OUT", 4);

        return map;
    }

    private static IReadOnlyList<HardwareChannel> Bits(string prefix, string direction, int count)
        => Channels(prefix, "BOOL", direction, count);

    private static IReadOnlyList<HardwareChannel> Channels(string prefix, string stType, string direction, int count)
        => Enumerable.Range(1, count).Select(i => new HardwareChannel($"{prefix}_{i}", stType, direction)).ToList();
}
