// TcKit CLI entry point. The init / config / doctor subcommands port here in a
// later phase via System.CommandLine. For now it exposes thin read verbs that
// share the reader + serialiser with the MCP tools, so the parity oracle can
// drive them without scripting the MCP stdio handshake. Each per-symbol verb is
// self-contained: it primes the index with get_structure, then reads.
using TcKit.Adapters.Reader;
using TcKit.Core.Serialization;

if (args.Length == 0)
{
    PrintUsage();
    return 0;
}

var reader = new XmlProjectReader();
var ct = CancellationToken.None;
var (pos, plc) = ParseArgs(args);

try
{
    switch (args[0])
    {
        case "get-structure" when pos.Length >= 1:
            return Emit(await reader.GetStructureAsync(pos[0], plc, ct).ConfigureAwait(false));

        case "get-pou-interface" when pos.Length >= 2:
            await reader.GetStructureAsync(pos[0], null, ct).ConfigureAwait(false);
            return Emit(await reader.GetPouInterfaceAsync(pos[1], plc, ct).ConfigureAwait(false));

        case "get-pou-declaration" when pos.Length >= 2:
            await reader.GetStructureAsync(pos[0], null, ct).ConfigureAwait(false);
            return Emit(await reader.GetPouDeclarationAsync(pos[1], plc, ct).ConfigureAwait(false));

        case "get-pou-item" when pos.Length >= 3:
            await reader.GetStructureAsync(pos[0], null, ct).ConfigureAwait(false);
            return Emit(await reader.GetPouItemAsync(pos[1], pos[2], plc, ct).ConfigureAwait(false));

        case "get-gvl" when pos.Length >= 2:
            await reader.GetStructureAsync(pos[0], null, ct).ConfigureAwait(false);
            return Emit(await reader.GetGvlAsync(pos[1], plc, ct).ConfigureAwait(false));

        case "get-dut" when pos.Length >= 2:
            await reader.GetStructureAsync(pos[0], null, ct).ConfigureAwait(false);
            return Emit(await reader.GetDutAsync(pos[1], plc, ct).ConfigureAwait(false));

        default:
            PrintUsage();
            return 2;
    }
}
#pragma warning disable CA1031 // The CLI boundary mirrors the tool: any failure becomes the error contract.
catch (Exception exc)
{
    Console.WriteLine(TckitJson.Serialize(new { error = exc.Message }));
    return 1;
}
#pragma warning restore CA1031

static int Emit<T>(T value)
{
    Console.WriteLine(TckitJson.Serialize(value));
    return 0;
}

static (string[] Positionals, string? Plc) ParseArgs(string[] args)
{
    var positionals = new List<string>();
    string? plc = null;
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == "--plc" && i + 1 < args.Length)
        {
            plc = args[++i];
        }
        else
        {
            positionals.Add(args[i]);
        }
    }

    return (positionals.ToArray(), plc);
}

static void PrintUsage()
{
    Console.WriteLine("tckit (C# rewrite) - CLI scaffold");
    Console.WriteLine("usage:");
    Console.WriteLine("  tckit get-structure        <path> [--plc <name>]");
    Console.WriteLine("  tckit get-pou-interface    <path> <pou>  [--plc <name>]");
    Console.WriteLine("  tckit get-pou-declaration  <path> <pou>  [--plc <name>]");
    Console.WriteLine("  tckit get-pou-item         <path> <pou> <item> [--plc <name>]");
    Console.WriteLine("  tckit get-gvl              <path> <gvl>  [--plc <name>]");
    Console.WriteLine("  tckit get-dut              <path> <dut>  [--plc <name>]");
}
