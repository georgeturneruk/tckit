// TcKit CLI entry point. The init / config / doctor subcommands port here in a
// later phase via System.CommandLine. For now it exposes the read verbs (which
// share the reader + serialiser with the MCP tools) and the writer verbs (which
// drive the COM Automation lane), so the parity oracle and the writer smoke can
// exercise the whole surface without scripting the MCP stdio handshake.
//
// Read verbs are self-contained: they prime the symbol index with get_structure,
// then read. Write verbs target the solution open in the attached TcXaeShell;
// code-bearing args accept either a literal string or '@<path>' to read a file.
using TcKit.Adapters.Ads;
using TcKit.Adapters.Automation;
using TcKit.Adapters.DocGen;
using TcKit.Adapters.Docs;
using TcKit.Adapters.Reader;
using TcKit.Core.Models;
using TcKit.Core.Ports;
using TcKit.Core.Serialization;

if (args.Length == 0)
{
    PrintUsage();
    return 0;
}

if (args[0] is "--version" or "version")
{
    Console.WriteLine(VersionString());
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

        case "find-fb" when pos.Length >= 1:
            return Emit(await new BeckhoffInfosysSearcher().FindFbAsync(pos[0], ct).ConfigureAwait(false));

        case "find-hardware" when pos.Length >= 1:
            return Emit(await new BeckhoffInfosysSearcher().FindHardwareAsync(pos[0], ct).ConfigureAwait(false));

        case "search-docs" when pos.Length >= 1:
            return Emit(await new BeckhoffInfosysSearcher().SearchAsync(pos[0], Opt("section"), ct).ConfigureAwait(false));

        case "get-doc-page" when pos.Length >= 1:
            return Emit(await new BeckhoffInfosysSearcher().GetPageAsync(pos[0], ct).ConfigureAwait(false));

        case "generate-docs" when pos.Length >= 2:
            return EmitResult(await new DocGenerator()
                .GenerateAsync(pos[0], pos[1], ParseDocFormat(OptOr("format", "html")), ct).ConfigureAwait(false));

        default:
            return await RunBuildTestVerb().ConfigureAwait(false);
    }
}
#pragma warning disable CA1031 // The CLI boundary mirrors the tool: any failure becomes the error contract.
catch (Exception exc)
{
    Console.WriteLine(TckitJson.Serialize(new { error = exc.Message }));
    return 1;
}
#pragma warning restore CA1031

async Task<int> RunBuildTestVerb()
{
    switch (args[0])
    {
        case "build":
        {
            using var runner = new AutomationBuildRunner();
            var result = await runner.BuildAsync(Opt("plc"), Flag("force-log"), ct).ConfigureAwait(false);
            return EmitObj(result, result.Success);
        }

        case "deploy" when pos.Length >= 1:
        {
            using var runner = new AutomationBuildRunner();
            var result = await runner.DeployAsync(pos[0], Opt("plc"), !Flag("no-autostart"), ct).ConfigureAwait(false);
            return EmitResult(result);
        }

        case "start-runtime" when pos.Length >= 1:
        {
            var result = await new AdsRuntimeControl().StartRuntimeAsync(pos[0], ct).ConfigureAwait(false);
            return EmitResult(result);
        }

        case "test" when pos.Length >= 1:
        {
            var target = Opt("target");
            if (string.IsNullOrEmpty(target))
            {
                Console.WriteLine(TckitJson.Serialize(new { error = "test requires --target <netid>." }));
                return 2;
            }

            var timeout = int.Parse(OptOr("timeout", "120"), System.Globalization.CultureInfo.InvariantCulture);
            using var testWriter = new AutomationProjectWriter();
            using var buildRunner = new AutomationBuildRunner();
            var result = await TcKit.Core.Workflows.TestWorkflow.RunAsync(
                testWriter, buildRunner, new TcUnitTestRunner(),
                pos[0], Opt("plc"), target, timeout, Opt("junit"), ct).ConfigureAwait(false);
            return EmitTestOutcome(result, result.Success, result.TestsPassed);
        }

        case "run-tests" when pos.Length >= 1:
        {
            var timeout = int.Parse(OptOr("timeout", "120"), System.Globalization.CultureInfo.InvariantCulture);
            var result = await new TcUnitTestRunner()
                .RunTestsAsync(pos[0], Opt("plc"), !Flag("no-wait"), timeout, ct)
                .ConfigureAwait(false);
            return EmitTestOutcome(result, result.Success, result.TestsPassed);
        }

        case "get-test-results" when pos.Length >= 1:
        {
            var result = await new TcUnitTestRunner()
                .GetResultsAsync(pos[0], Opt("plc"), Opt("xml"), ct)
                .ConfigureAwait(false);
            return EmitTestOutcome(result, result.Success, result.TestsPassed);
        }

        case "read-symbols" when pos.Length >= 1:
        {
            var values = await new AdsSymbolIo()
                .ReadSymbolsAsync(pos[0], pos[1..], ct)
                .ConfigureAwait(false);
            return Emit(new { success = true, values });
        }

        case "write-symbols" when pos.Length >= 2:
        {
            var result = await new AdsSymbolIo()
                .WriteSymbolsAsync(pos[0], ParseWrites(Code(pos[1])), ct)
                .ConfigureAwait(false);
            return EmitResult(result);
        }

        case "invoke-rpc" when pos.Length >= 3:
        {
            var result = await new AdsSymbolIo()
                .InvokeRpcAsync(pos[0], pos[1], pos[2], ParseParams(OptOr("params", "[]")), ct)
                .ConfigureAwait(false);
            return EmitResult(result);
        }

        case "list-ethercat-masters" when pos.Length >= 1:
        {
            var masters = await new TwinSharpHardwareInspector()
                .ListEtherCatMastersAsync(pos[0], ct).ConfigureAwait(false);
            return Emit(new { success = true, masters });
        }

        case "get-ethercat-status" when pos.Length >= 1:
        {
            var status = await new TwinSharpHardwareInspector()
                .GetEtherCatStatusAsync(pos[0], OptOr("master", ""), ct).ConfigureAwait(false);
            return Emit(status);
        }

        case "get-ipc-hardware" when pos.Length >= 1:
        {
            var ipc = await new TwinSharpHardwareInspector()
                .GetIpcHardwareAsync(pos[0], ct).ConfigureAwait(false);
            return Emit(ipc);
        }

        case "list-axes" when pos.Length >= 1:
        {
            var axes = await new TwinSharpHardwareInspector()
                .ListAxesAsync(pos[0], ct).ConfigureAwait(false);
            return Emit(new { success = true, axes });
        }

        case "get-axis-state" when pos.Length >= 2:
        {
            var axisId = int.Parse(pos[1], System.Globalization.CultureInfo.InvariantCulture);
            var axis = await new TwinSharpHardwareInspector()
                .GetAxisStateAsync(pos[0], axisId, ct).ConfigureAwait(false);
            return Emit(new { success = true, axes = new[] { axis } });
        }

        default:
            return await RunWriteVerb().ConfigureAwait(false);
    }
}

async Task<int> RunWriteVerb()
{
    switch (args[0])
    {
        case "scan-hardware":
        {
            using var scanner = new AutomationHardwareScanner();
            return Emit(await scanner.ScanHardwareAsync(Opt("project"), ct).ConfigureAwait(false));
        }

        case "scaffold-hardware-code":
        {
            using var scanner = new AutomationHardwareScanner();
            var gvl = pos.Length >= 1 ? pos[0] : "HardwareIO";
            return EmitResult(await scanner
                .ScaffoldHardwareCodeAsync(gvl, Opt("plc"), OptOr("parent", ""), Opt("project"), ct)
                .ConfigureAwait(false));
        }

        case "add-ethercat-master":
        {
            using var hw = new AutomationHardwareConfigurer();
            var name = pos.Length >= 1 ? pos[0] : "Device 1 (EtherCAT)";
            return EmitResult(await hw.AddEtherCatMasterAsync(name, Opt("project"), ct).ConfigureAwait(false));
        }

        case "add-ethercat-box" when pos.Length >= 3:
        {
            using var hw = new AutomationHardwareConfigurer();
            return EmitResult(await hw
                .AddEtherCatBoxAsync(pos[0], pos[1], pos[2], OptOr("before", ""), Opt("project"), ct)
                .ConfigureAwait(false));
        }

        case "delete-io-device" when pos.Length >= 1:
        {
            using var hw = new AutomationHardwareConfigurer();
            return EmitResult(await hw
                .DeleteIoDeviceAsync(pos[0], Opt("project"), Flag("confirmed"), ct).ConfigureAwait(false));
        }
    }

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
                Opt("plc"), pos[0], OptOr("version", "*"), OptOr("distributor", "Tc3 Project"),
                ParseParameters(Opt("params")), ct).ConfigureAwait(false));

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

static int EmitObj<T>(T value, bool success)
{
    Console.WriteLine(TckitJson.Serialize(value));
    return success ? 0 : 1;
}

// Test-verb exit codes: 0 = ran and passed (or outcome not requested), 1 = infrastructure
// failure (runtime/timeout/parse), 3 = run completed but tests failed or expected results
// never appeared. 2 stays the usage error, so CI can tell the three apart.
static int EmitTestOutcome<T>(T value, bool infrastructureOk, bool? testsPassed)
{
    Console.WriteLine(TckitJson.Serialize(value));
    return !infrastructureOk ? 1 : testsPassed == false ? 3 : 0;
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

static DocFormat ParseDocFormat(string value) => value.Trim().ToLowerInvariant() switch
{
    "" or "html" => DocFormat.Html,
    "markdown" or "md" => DocFormat.Markdown,
    _ => throw new ArgumentException($"Unknown doc format '{value}'. Use 'html' or 'markdown'."),
};

static DutKind ParseDutKind(string value) => value.Trim().ToLowerInvariant() switch
{
    "struct" => DutKind.Struct,
    "enum" => DutKind.Enum,
    "union" => DutKind.Union,
    _ => throw new ArgumentException($"Unknown dutKind '{value}'."),
};

static IReadOnlyDictionary<string, object?> ParseWrites(string json)
{
    var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json)
        ?? throw new ArgumentException("writes must be a JSON object of symbol path -> value.");
    return parsed.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);
}

static IReadOnlyList<object?> ParseParams(string json)
{
    var parsed = System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement>>(json)
        ?? throw new ArgumentException("params must be a JSON array of positional parameters.");
    return parsed.Select(e => (object?)e).ToList();
}

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

static string VersionString()
{
    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
    var informational = assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion;
    // Drop the '+<commit>' source-revision suffix the SDK appends to the informational version.
    var version = informational?.Split('+')[0];
    return string.IsNullOrEmpty(version)
        ? assembly.GetName().Version?.ToString() ?? "unknown"
        : version;
}

static void PrintUsage()
{
    Console.WriteLine("tckit (C# rewrite) - CLI scaffold");
    Console.WriteLine("  version | --version");
    Console.WriteLine("read verbs:");
    Console.WriteLine("  get-structure <path> [--plc <name>]");
    Console.WriteLine("  get-pou-interface | get-pou-declaration <path> <pou> [--plc <name>]");
    Console.WriteLine("  get-pou-item <path> <pou> <item> [--plc <name>]");
    Console.WriteLine("  get-gvl | get-dut <path> <name> [--plc <name>]");
    Console.WriteLine("infosys docs verbs (network; results cached locally):");
    Console.WriteLine("  find-fb <fbName>");
    Console.WriteLine("  find-hardware <orderNumber>");
    Console.WriteLine("  search-docs <query> [--section <sectionPath>]");
    Console.WriteLine("  get-doc-page <url>");
    Console.WriteLine("doc generator verbs (local ST comments; no network):");
    Console.WriteLine("  generate-docs <projectDir> <outputDir> [--format html|markdown]");
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
    Console.WriteLine("  add-library-reference <lib> [--version *] [--distributor <d>] [--params <json>] [--plc]");
    Console.WriteLine("  delete-library-reference <lib> [--version *] [--distributor <d>] [--plc]");
    Console.WriteLine("  add-library-placeholder <name> <defaultLib> [--version *] [--distributor <d>] [--params <json>] [--plc]");
    Console.WriteLine("  set-placeholder-parameters <name> <paramsJson> [--plc <name>]");
    Console.WriteLine("  delete-placeholder <name> [--plc <name>]");
    Console.WriteLine("  save-plc-as-library <output> [--no-install] [--repo System] [--overwrite] [--plc <name>]");
    Console.WriteLine("  scan-hardware [--project <tcProject>]");
    Console.WriteLine("  scaffold-hardware-code [<gvlName>] [--plc <name>] [--parent <folder>] [--project <tcProject>]");
    Console.WriteLine("  add-ethercat-master [<deviceName>] [--project <tcProject>]");
    Console.WriteLine("  add-ethercat-box <parentName> <boxName> <orderNumber> [--before <sibling>] [--project <tcProject>]");
    Console.WriteLine("  delete-io-device <name|^path> [--project <tcProject>] [--confirmed]");
    Console.WriteLine("build / test / deploy verbs:");
    Console.WriteLine("  test <sln> --target <netid> [--plc <name>] [--timeout 120] [--junit <out.xml>]");
    Console.WriteLine("    composite CI verb: open -> build -> deploy -> run tests -> copy results");
    Console.WriteLine("  build [--plc <name>] [--force-log]");
    Console.WriteLine("  deploy <targetAmsId> [--plc <name>] [--no-autostart]");
    Console.WriteLine("  start-runtime <targetAmsId>");
    Console.WriteLine("  run-tests <targetAmsId> [--plc <name>] [--no-wait] [--timeout 120]");
    Console.WriteLine("  get-test-results <targetAmsId> [--plc <name>] [--xml <path>]");
    Console.WriteLine("    exit codes: 0 tests passed (or --no-wait), 1 run/parse failure,");
    Console.WriteLine("                3 tests failed or expected results missing");
    Console.WriteLine("symbol I/O verbs (ADS; target must be in Run mode):");
    Console.WriteLine("  read-symbols <targetAmsId> <path> [<path> ...]");
    Console.WriteLine("  write-symbols <targetAmsId> <writesJson|@file>");
    Console.WriteLine("  invoke-rpc <targetAmsId> <symbolPath> <methodName> [--params <json>]");
    Console.WriteLine("hardware diagnostics verbs (ADS / TwinSharp):");
    Console.WriteLine("  list-ethercat-masters <targetAmsId>");
    Console.WriteLine("  get-ethercat-status <targetAmsId> [--master <netId>]");
    Console.WriteLine("  get-ipc-hardware <targetAmsId>");
    Console.WriteLine("  list-axes <targetAmsId>");
    Console.WriteLine("  get-axis-state <targetAmsId> <axisId>");
}
