using System.ComponentModel;
using ModelContextProtocol.Server;
using TcKit.Core.Models;
using TcKit.Core.Ports;
using TcKit.Core.Serialization;

namespace TcKit.Server.Tools;

/// <summary>
/// Project-authoring tools (COM Automation Interface). They mutate the solution open in the
/// attached TcXaeShell, so XAE must be running with the project open. PascalCase tool names +
/// camelCase parameters (C# conventions); each returns the snake_case Result contract.
/// </summary>
[McpServerToolType]
public sealed class WriterTools(IProjectWriter writer)
{
    [McpServerTool(Name = "OpenProject")]
    [Description("Open (or confirm open) a TwinCAT solution in XAE. Idempotent; rarely needed if "
        + "the project is already open. solutionPath is the absolute path to the .sln.")]
    public Task<string> OpenProject(string solutionPath, CancellationToken cancellationToken = default)
        => Run(() => writer.OpenProjectAsync(solutionPath, cancellationToken));

    [McpServerTool(Name = "AddPou")]
    [Description("Add a new POU to the open project. pouType is one of function_block | function | "
        + "program | interface. code is the full ST source including VAR blocks. parentFolder is an "
        + "optional slash-separated path under POUs (folders must already exist).")]
    public Task<string> AddPou(
        string name, string pouType, string code,
        string parentFolder = "", string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.AddPouAsync(name, ParsePouType(pouType), code, parentFolder, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "AddFolder")]
    [Description("Add a folder to a PLC project's source tree. parentPath is a slash-separated path "
        + "under the PLC project node (e.g. POUs, POUs/Drives, DUTs); defaults to POUs.")]
    public Task<string> AddFolder(
        string name, string parentPath = "POUs", string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.AddFolderAsync(name, parentPath, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "AddGvl")]
    [Description("Add a Global Variable List (declaration-only). code is the full VAR_GLOBAL / "
        + "END_VAR source. parentFolder is an optional path under POUs.")]
    public Task<string> AddGvl(
        string name, string code, string parentFolder = "", string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.AddGvlAsync(name, code, parentFolder, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "AddDut")]
    [Description("Add a Data Unit Type. dutKind is one of struct | enum | union. code is the full "
        + "TYPE ... END_TYPE source. parentFolder is an optional path under DUTs.")]
    public Task<string> AddDut(
        string name, string code, string dutKind = "struct",
        string parentFolder = "", string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.AddDutAsync(name, code, ParseDutKind(dutKind), parentFolder, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "AddMethod")]
    [Description("Add a method to an existing POU. code is the full ST source including the METHOD "
        + "header and any VAR blocks.")]
    public Task<string> AddMethod(
        string pouName, string methodName, string code, string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.AddMethodAsync(pouName, methodName, code, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "AddProperty")]
    [Description("Add a property to an existing POU. Supply getterCode and/or setterCode (at least "
        + "one); each is the accessor body, optionally preceded by a local VAR block, with no "
        + "PROPERTY header. returnType is the exposed type (e.g. LREAL, BOOL).")]
    public Task<string> AddProperty(
        string pouName, string propertyName, string returnType,
        string getterCode = "", string setterCode = "", string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.AddPropertyAsync(
            pouName, propertyName, returnType, Optional(getterCode), Optional(setterCode), Optional(plcName), cancellationToken));

    private static string? Optional(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static PouType ParsePouType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "function_block" or "functionblock" or "fb" => PouType.FunctionBlock,
        "function" => PouType.Function,
        "program" or "prg" => PouType.Program,
        "interface" or "itf" => PouType.Interface,
        _ => throw new ArgumentException(
            $"Unknown pouType '{value}'. Use function_block | function | program | interface."),
    };

    private static DutKind ParseDutKind(string value) => value.Trim().ToLowerInvariant() switch
    {
        "struct" => DutKind.Struct,
        "enum" => DutKind.Enum,
        "union" => DutKind.Union,
        _ => throw new ArgumentException($"Unknown dutKind '{value}'. Use struct | enum | union."),
    };

    private static async Task<string> Run(Func<Task<Result>> call)
    {
        try
        {
            return TckitJson.Serialize(await call().ConfigureAwait(false));
        }
#pragma warning disable CA1031 // The tool boundary funnels every failure into the Result error contract.
        catch (Exception exc)
        {
            return TckitJson.Serialize(Result.Fail(exc.Message));
        }
#pragma warning restore CA1031
    }
}
