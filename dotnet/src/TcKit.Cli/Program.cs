// TcKit CLI entry point. The init / config / doctor subcommands port here in a
// later phase via System.CommandLine. For now it exposes the read verbs (which
// share the reader + serialiser with the MCP tools) and the writer verbs (which
// drive the COM Automation lane), so the parity oracle and the writer smoke can
// exercise the whole surface without scripting the MCP stdio handshake.
//
// Read verbs are self-contained: they prime the symbol index with get_structure,
// then read. Write verbs target the solution open in the attached TcXaeShell;
// code-bearing args accept either a literal string or '@<path>' to read a file.
using TcKit.Adapters.Automation;
using TcKit.Adapters.Reader;
using TcKit.Core.Models;
using TcKit.Core.Serialization;

if (args.Length == 0)
{
    PrintUsage();
    return 0;
}

var reader = new XmlProjectReader();
var ct = CancellationToken.None;
var (pos, opt) = ParseArgs(args);

string? Opt(string key) => opt.TryGetValue(key, out var v) ? v : null;
string OptOr(string key, string fallback) => opt.TryGetValue(key, out var v) ? v : fallback;
bool Flag(string key) => opt.TryGetValue(key, out var v) && v != "false";
static string Code(string value) => value.StartsWith('@') ? File.ReadAllText(value[1..]) : value;

try
{
    switch (args[0])
    {
        case "get-structure" when pos.Length >= 1:
            return Emit(await reader.GetStructureAsync(pos[0], Opt("plc"), ct).ConfigureAwait(false));

        case "get-pou-interface" when pos.Length >= 2:
            await reader.GetStructureAsync(pos[0], null, ct).ConfigureAwait(false);
            return Emit(await reader.GetPouInterfaceAsync(pos[1], Opt("plc"), ct).ConfigureAwait(false));

        case "get-pou-declaration" when pos.Length >= 2:
            await reader.GetStructureAsync(pos[0], null, ct).ConfigureAwait(false);
            return Emit(await reader.GetPouDeclarationAsync(pos[1], Opt("plc"), ct).ConfigureAwait(false));

        case "get-pou-item" when pos.Length >= 3:
            await reader.GetStructureAsync(pos[0], null, ct).ConfigureAwait(false);
            return Emit(await reader.GetPouItemAsync(pos[1], pos[2], Opt("plc"), ct).ConfigureAwait(false));

        case "get-gvl" when pos.Length >= 2:
            await reader.GetStructureAsync(pos[0], null, ct).ConfigureAwait(false);
            return Emit(await reader.GetGvlAsync(pos[1], Opt("plc"), ct).ConfigureAwait(false));

        case "get-dut" when pos.Length >= 2:
            await reader.GetStructureAsync(pos[0], null, ct).ConfigureAwait(false);
            return Emit(await reader.GetDutAsync(pos[1], Opt("plc"), ct).ConfigureAwait(false));

        default:
            return await RunWriteVerb().ConfigureAwait(false);
    }
}
#pragma warning disable CA1031 // The CLI boundary mirrors the tool: any failure becomes the error contract.
catch (Exception exc)
{
    Console.WriteLine(TckitJson.Serialize(new { error = exc.Message }));
    return 1;
}
#pragma warning restore CA1031

async Task<int> RunWriteVerb()
{
    using var writer = new AutomationProjectWriter();
    switch (args[0])
    {
        case "open-project" when pos.Length >= 1:
            return EmitResult(await writer.OpenProjectAsync(pos[0], ct).ConfigureAwait(false));

        case "create-project" when pos.Length >= 2:
            return EmitResult(await writer.CreateProjectAsync(pos[0], pos[1], ct).ConfigureAwait(false));

        case "add-plc-project" when pos.Length >= 1:
            return EmitResult(await writer.AddPlcProjectAsync(
                OptOr("sln", ""), pos[0], OptOr("type", "standard"), ct).ConfigureAwait(false));

        case "add-folder" when pos.Length >= 1:
            return EmitResult(await writer.AddFolderAsync(
                pos[0], OptOr("parent", "POUs"), Opt("plc"), ct).ConfigureAwait(false));

        case "add-pou" when pos.Length >= 3:
            return EmitResult(await writer.AddPouAsync(
                pos[0], ParsePouType(pos[1]), Code(pos[2]), OptOr("parent", ""), Opt("plc"), ct).ConfigureAwait(false));

        case "add-gvl" when pos.Length >= 2:
            return EmitResult(await writer.AddGvlAsync(
                pos[0], Code(pos[1]), OptOr("parent", ""), Opt("plc"), ct).ConfigureAwait(false));

        case "add-dut" when pos.Length >= 2:
            return EmitResult(await writer.AddDutAsync(
                pos[0], Code(pos[1]), ParseDutKind(OptOr("kind", "struct")), OptOr("parent", ""), Opt("plc"), ct)
                .ConfigureAwait(false));

        case "add-method" when pos.Length >= 3:
            return EmitResult(await writer.AddMethodAsync(
                pos[0], pos[1], Code(pos[2]), Opt("plc"), ct).ConfigureAwait(false));

        case "add-property" when pos.Length >= 3:
            return EmitResult(await writer.AddPropertyAsync(
                pos[0], pos[1], pos[2],
                opt.TryGetValue("get", out var g) ? Code(g) : null,
                opt.TryGetValue("set", out var s) ? Code(s) : null,
                Opt("plc"), ct).ConfigureAwait(false));

        case "add-variable" when pos.Length >= 3:
            return EmitResult(await writer.AddVariableAsync(
                pos[0], pos[1], pos[2], Opt("item"), Opt("plc"), ct).ConfigureAwait(false));

        case "update-pou-declaration" when pos.Length >= 2:
            return EmitResult(await writer.UpdatePouDeclarationAsync(
                pos[0], Code(pos[1]), Opt("plc"), ct).ConfigureAwait(false));

        case "update-pou-implementation" when pos.Length >= 2:
            return EmitResult(await writer.UpdatePouImplementationAsync(
                pos[0], Code(pos[1]), Opt("plc"), ct).ConfigureAwait(false));

        case "update-method-body" when pos.Length >= 3:
            return EmitResult(await writer.UpdateMethodBodyAsync(
                pos[0], pos[1], Code(pos[2]), Opt("plc"), ct).ConfigureAwait(false));

        case "update-pou-declaration-patch" when pos.Length >= 3:
            return EmitResult(await writer.UpdatePouDeclarationPatchAsync(
                pos[0], Code(pos[1]), Code(pos[2]), Opt("plc"), ct).ConfigureAwait(false));

        case "update-pou-implementation-patch" when pos.Length >= 3:
            return EmitResult(await writer.UpdatePouImplementationPatchAsync(
                pos[0], Code(pos[1]), Code(pos[2]), Opt("plc"), ct).ConfigureAwait(false));

        case "update-method-body-patch" when pos.Length >= 4:
            return EmitResult(await writer.UpdateMethodBodyPatchAsync(
                pos[0], pos[1], Code(pos[2]), Code(pos[3]), Opt("plc"), ct).ConfigureAwait(false));

        case "delete-pou" when pos.Length >= 1:
            return EmitResult(await writer.DeletePouAsync(pos[0], Opt("plc"), ct).ConfigureAwait(false));

        case "delete-method" when pos.Length >= 2:
            return EmitResult(await writer.DeleteMethodAsync(pos[0], pos[1], Opt("plc"), ct).ConfigureAwait(false));

        case "delete-property" when pos.Length >= 2:
            return EmitResult(await writer.DeletePropertyAsync(pos[0], pos[1], Opt("plc"), ct).ConfigureAwait(false));

        case "delete-gvl" when pos.Length >= 1:
            return EmitResult(await writer.DeleteGvlAsync(pos[0], Opt("plc"), ct).ConfigureAwait(false));

        case "delete-dut" when pos.Length >= 1:
            return EmitResult(await writer.DeleteDutAsync(pos[0], Opt("plc"), ct).ConfigureAwait(false));

        case "delete-folder" when pos.Length >= 1:
            return EmitResult(await writer.DeleteFolderAsync(
                pos[0], OptOr("parent", ""), Flag("recursive"), Opt("plc"), ct).ConfigureAwait(false));

        case "delete-variable" when pos.Length >= 2:
            return EmitResult(await writer.DeleteVariableAsync(pos[0], pos[1], Opt("item"), Opt("plc"), ct).ConfigureAwait(false));

        case "add-library-reference" when pos.Length >= 1:
            return EmitResult(await writer.AddLibraryReferenceAsync(
                Opt("plc"), pos[0], OptOr("version", "*"), OptOr("distributor", "Tc3 Project"), ct).ConfigureAwait(false));

        case "delete-library-reference" when pos.Length >= 1:
            return EmitResult(await writer.DeleteLibraryReferenceAsync(
                Opt("plc"), pos[0], OptOr("version", "*"), OptOr("distributor", "Tc3 Project"), ct).ConfigureAwait(false));

        case "add-library-placeholder" when pos.Length >= 2:
            return EmitResult(await writer.AddLibraryPlaceholderAsync(
                Opt("plc"), pos[0], pos[1], OptOr("version", "*"), OptOr("distributor", ""),
                ParseParameters(Opt("params")), ct).ConfigureAwait(false));

        case "set-placeholder-parameters" when pos.Length >= 2:
            return EmitResult(await writer.SetPlaceholderParametersAsync(
                Opt("plc"), pos[0],
                ParseParameters(Code(pos[1])) ?? throw new ArgumentException("parameters required."), ct)
                .ConfigureAwait(false));

        case "delete-placeholder" when pos.Length >= 1:
            return EmitResult(await writer.DeletePlaceholderAsync(Opt("plc"), pos[0], ct).ConfigureAwait(false));

        case "save-plc-as-library" when pos.Length >= 1:
            return EmitResult(await writer.SavePlcAsLibraryAsync(
                Opt("plc"), pos[0], !Flag("no-install"), OptOr("repo", "System"), Flag("overwrite"), ct)
                .ConfigureAwait(false));

        default:
            PrintUsage();
            return 2;
    }
}

static int Emit<T>(T value)
{
    Console.WriteLine(TckitJson.Serialize(value));
    return 0;
}

static int EmitResult(Result result)
{
    Console.WriteLine(TckitJson.Serialize(result));
    return result.Success ? 0 : 1;
}

static (string[] Positionals, Dictionary<string, string> Options) ParseArgs(string[] args)
{
    var positionals = new List<string>();
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            var key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[key] = args[++i];
            }
            else
            {
                options[key] = "true";
            }
        }
        else
        {
            positionals.Add(args[i]);
        }
    }

    return (positionals.ToArray(), options);
}

static PouType ParsePouType(string value) => value.Trim().ToLowerInvariant() switch
{
    "function_block" or "functionblock" or "fb" => PouType.FunctionBlock,
    "function" => PouType.Function,
    "program" or "prg" => PouType.Program,
    "interface" or "itf" => PouType.Interface,
    _ => throw new ArgumentException($"Unknown pouType '{value}'."),
};

static DutKind ParseDutKind(string value) => value.Trim().ToLowerInvariant() switch
{
    "struct" => DutKind.Struct,
    "enum" => DutKind.Enum,
    "union" => DutKind.Union,
    _ => throw new ArgumentException($"Unknown dutKind '{value}'."),
};

static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? ParseParameters(string? json)
{
    if (string.IsNullOrWhiteSpace(json))
    {
        return null;
    }

    var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json)
        ?? throw new ArgumentException("params must be a JSON object of { list: { key: value } }.");
    return parsed.ToDictionary(
        list => list.Key, list => (IReadOnlyDictionary<string, string>)list.Value, StringComparer.Ordinal);
}

static void PrintUsage()
{
    Console.WriteLine("tckit (C# rewrite) - CLI scaffold");
    Console.WriteLine("read verbs:");
    Console.WriteLine("  get-structure <path> [--plc <name>]");
    Console.WriteLine("  get-pou-interface | get-pou-declaration <path> <pou> [--plc <name>]");
    Console.WriteLine("  get-pou-item <path> <pou> <item> [--plc <name>]");
    Console.WriteLine("  get-gvl | get-dut <path> <name> [--plc <name>]");
    Console.WriteLine("write verbs (target the open XAE solution; code args accept '@<file>'):");
    Console.WriteLine("  create-project <name> <path>");
    Console.WriteLine("  add-plc-project <plcName> [--sln <path>] [--type standard]");
    Console.WriteLine("  open-project <sln>");
    Console.WriteLine("  add-folder <name> [--parent POUs] [--plc <name>]");
    Console.WriteLine("  add-pou <name> <type> <code> [--parent <p>] [--plc <name>]");
    Console.WriteLine("  add-gvl <name> <code> [--parent <p>] [--plc <name>]");
    Console.WriteLine("  add-dut <name> <code> [--kind struct|enum|union] [--parent <p>] [--plc <name>]");
    Console.WriteLine("  add-method <pou> <method> <code> [--plc <name>]");
    Console.WriteLine("  add-property <pou> <prop> <returnType> [--get <code>] [--set <code>] [--plc <name>]");
    Console.WriteLine("  add-variable <pou> <scope> <decl> [--item <m>] [--plc <name>]");
    Console.WriteLine("  update-pou-declaration | update-pou-implementation <pou> <code> [--plc <name>]");
    Console.WriteLine("  update-method-body <pou> <method> <code> [--plc <name>]");
    Console.WriteLine("  update-pou-declaration-patch | update-pou-implementation-patch <pou> <old> <new> [--plc]");
    Console.WriteLine("  update-method-body-patch <pou> <method> <old> <new> [--plc <name>]");
    Console.WriteLine("  delete-pou | delete-gvl | delete-dut <name> [--plc <name>]");
    Console.WriteLine("  delete-method | delete-property <pou> <name> [--plc <name>]");
    Console.WriteLine("  delete-folder <name> [--parent <p>] [--recursive] [--plc <name>]");
    Console.WriteLine("  delete-variable <pou> <name> [--item <m>] [--plc <name>]");
    Console.WriteLine("  add-library-reference | delete-library-reference <lib> [--version *] [--distributor <d>] [--plc]");
    Console.WriteLine("  add-library-placeholder <name> <defaultLib> [--version *] [--distributor <d>] [--params <json>] [--plc]");
    Console.WriteLine("  set-placeholder-parameters <name> <paramsJson> [--plc <name>]");
    Console.WriteLine("  delete-placeholder <name> [--plc <name>]");
    Console.WriteLine("  save-plc-as-library <output> [--no-install] [--repo System] [--overwrite] [--plc <name>]");
}
