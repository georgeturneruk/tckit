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

    [McpServerTool(Name = "UpdatePouDeclaration")]
    [Description("Replace a POU's FB-level declaration block (VAR sections / signature). code is the "
        + "new declaration, header through the last END_VAR.")]
    public Task<string> UpdatePouDeclaration(
        string pouName, string code, string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.UpdatePouDeclarationAsync(pouName, code, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "UpdatePouImplementation")]
    [Description("Replace a POU's cyclic implementation body. code is ST statements only, no header "
        + "or VAR blocks.")]
    public Task<string> UpdatePouImplementation(
        string pouName, string code, string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.UpdatePouImplementationAsync(pouName, code, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "UpdateMethodBody")]
    [Description("Replace the full body of a method, action, or property. code is the combined "
        + "declaration + implementation, including the METHOD/ACTION/PROPERTY header and any VAR blocks.")]
    public Task<string> UpdateMethodBody(
        string pouName, string methodName, string code, string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.UpdateMethodBodyAsync(pouName, methodName, code, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "UpdatePouDeclarationPatch")]
    [Description("Anchored edit on a POU's declaration: replace the single occurrence of oldString "
        + "with newString. Fails if oldString is missing or appears more than once.")]
    public Task<string> UpdatePouDeclarationPatch(
        string pouName, string oldString, string newString, string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.UpdatePouDeclarationPatchAsync(pouName, oldString, newString, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "UpdatePouImplementationPatch")]
    [Description("Anchored edit on a POU's implementation: replace the single occurrence of oldString "
        + "with newString. Fails if oldString is missing or appears more than once.")]
    public Task<string> UpdatePouImplementationPatch(
        string pouName, string oldString, string newString, string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.UpdatePouImplementationPatchAsync(pouName, oldString, newString, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "UpdateMethodBodyPatch")]
    [Description("Anchored edit on a method/action/property's combined source: replace the single "
        + "occurrence of oldString with newString. Fails if oldString is missing or appears more than once.")]
    public Task<string> UpdateMethodBodyPatch(
        string pouName, string methodName, string oldString, string newString,
        string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.UpdateMethodBodyPatchAsync(pouName, methodName, oldString, newString, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "DeletePou")]
    [Description("Delete a POU (FB, function, program, or interface). Refuses a PROGRAM still bound "
        + "to a task; detach the task's PouCall first.")]
    public Task<string> DeletePou(string name, string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.DeletePouAsync(name, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "DeleteMethod")]
    [Description("Delete a method or action from a POU.")]
    public Task<string> DeleteMethod(
        string pouName, string methodName, string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.DeleteMethodAsync(pouName, methodName, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "DeleteProperty")]
    [Description("Delete a property (and its Get/Set accessors) from a POU.")]
    public Task<string> DeleteProperty(
        string pouName, string propertyName, string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.DeletePropertyAsync(pouName, propertyName, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "DeleteGvl")]
    [Description("Delete a Global Variable List (validates the item really is a GVL).")]
    public Task<string> DeleteGvl(string name, string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.DeleteGvlAsync(name, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "DeleteDut")]
    [Description("Delete a Data Unit Type (struct, enum, union, or alias).")]
    public Task<string> DeleteDut(string name, string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.DeleteDutAsync(name, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "DeleteFolder")]
    [Description("Delete a folder from a PLC project's source tree. Refuses a non-empty folder "
        + "unless recursive is true. parentPath optionally disambiguates a name in multiple subtrees.")]
    public Task<string> DeleteFolder(
        string name, string parentPath = "", bool recursive = false, string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.DeleteFolderAsync(name, parentPath, recursive, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "AddVariable")]
    [Description("Add one variable declaration to a named scope block. scope is one of VAR_INPUT, "
        + "VAR_OUTPUT, VAR_IN_OUT, VAR, VAR_PERSISTENT, VAR_TEMP, or 'VAR CONSTANT'. declaration is a "
        + "single line e.g. 'bEnable : BOOL;'. itemName targets a method's local VARs (else FB-level).")]
    public Task<string> AddVariable(
        string pouName, string scope, string declaration,
        string itemName = "", string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.AddVariableAsync(pouName, scope, declaration, Optional(itemName), Optional(plcName), cancellationToken));

    [McpServerTool(Name = "DeleteVariable")]
    [Description("Remove one variable declaration from a POU or method. Refuses multi-name lists "
        + "(use UpdatePouDeclarationPatch for those). itemName targets a method's local VARs.")]
    public Task<string> DeleteVariable(
        string pouName, string variableName,
        string itemName = "", string plcName = "", CancellationToken cancellationToken = default)
        => Run(() => writer.DeleteVariableAsync(pouName, variableName, Optional(itemName), Optional(plcName), cancellationToken));

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
