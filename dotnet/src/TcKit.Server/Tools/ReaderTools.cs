using System.ComponentModel;
using ModelContextProtocol.Server;
using TcKit.Core.Ports;
using TcKit.Core.Serialization;

namespace TcKit.Server.Tools;

/// <summary>
/// Read-only project inspection tools (offline XML parsing; no XAE required).
/// The MCP surface mirrors the C# identifiers: PascalCase tool names (set explicitly, since the
/// SDK would otherwise camelCase the method name) and camelCase parameters. Output JSON uses the
/// snake_case data contract from <see cref="TckitJson"/>.
/// </summary>
[McpServerToolType]
public sealed class ReaderTools(IProjectReader reader)
{
    [McpServerTool(Name = "GetStructure")]
    [Description("Return the project map: POUs by folder, tasks, libraries, plus GVL and DUT "
        + "names. The single call that orients you on an unfamiliar TwinCAT project; call it once "
        + "at the start of a session before the other read tools. projectPath is the solution root "
        + "directory or a .sln file inside it; plcName restricts the walk to one PLC project.")]
    public Task<string> GetStructure(
        string projectPath, string plcName = "", CancellationToken cancellationToken = default)
        => RunAsync(() => reader.GetStructureAsync(projectPath, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "GetPouInterface")]
    [Description("Return declarations and method/property signatures for a POU, without method "
        + "bodies. Call after GetStructure when you need to understand a POU's interface; never "
        + "for every POU. plcName disambiguates when the name exists in more than one PLC project.")]
    public Task<string> GetPouInterface(
        string pouName, string plcName = "", CancellationToken cancellationToken = default)
        => RunAsync(() => reader.GetPouInterfaceAsync(pouName, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "GetPouDeclaration")]
    [Description("Return only the FB-level declaration block of a POU (VAR sections, no methods). "
        + "Narrower than GetPouInterface; use when preparing a variable add and method signatures "
        + "are noise.")]
    public Task<string> GetPouDeclaration(
        string pouName, string plcName = "", CancellationToken cancellationToken = default)
        => RunAsync(() => reader.GetPouDeclarationAsync(pouName, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "GetPouItem")]
    [Description("Return the declaration and body of a single method, action, or property accessor. "
        + "The most surgical read. itemName accepts 'Execute' (method/action), 'Status' (property "
        + "header), or 'Status.Get' / 'Status.Set' (accessor body).")]
    public Task<string> GetPouItem(
        string pouName, string itemName, string plcName = "", CancellationToken cancellationToken = default)
        => RunAsync(() => reader.GetPouItemAsync(pouName, itemName, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "GetGvl")]
    [Description("Return the declaration block of a Global Variable List (e.g. GVL_Parameters).")]
    public Task<string> GetGvl(
        string gvlName, string plcName = "", CancellationToken cancellationToken = default)
        => RunAsync(() => reader.GetGvlAsync(gvlName, Optional(plcName), cancellationToken));

    [McpServerTool(Name = "GetDut")]
    [Description("Return the declaration block of a Data Unit Type: struct, enum, union, or alias "
        + "(e.g. ST_Config, E_State).")]
    public Task<string> GetDut(
        string dutName, string plcName = "", CancellationToken cancellationToken = default)
        => RunAsync(() => reader.GetDutAsync(dutName, Optional(plcName), cancellationToken));

    private static string? Optional(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static async Task<string> RunAsync<T>(Func<Task<T>> read)
    {
        try
        {
            return TckitJson.Serialize(await read().ConfigureAwait(false));
        }
#pragma warning disable CA1031 // The tool boundary deliberately funnels every failure into the error contract.
        catch (Exception exc)
        {
            return TckitJson.Serialize(new { error = exc.Message });
        }
#pragma warning restore CA1031
    }
}
