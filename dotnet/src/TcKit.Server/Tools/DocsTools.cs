using System.ComponentModel;
using ModelContextProtocol.Server;
using TcKit.Core.Ports;
using TcKit.Core.Serialization;

namespace TcKit.Server.Tools;

/// <summary>
/// Beckhoff infosys documentation tools. PascalCase tool names (set explicitly) and camelCase
/// parameters; output JSON uses the snake_case contract from <see cref="TckitJson"/>. The searcher
/// navigates infosys's own menu tree and caches results to disk, so no external search is involved.
/// </summary>
[McpServerToolType]
public sealed class DocsTools(IDocsSearcher searcher)
{
    [McpServerTool(Name = "FindFb")]
    [Description("Search and fetch Beckhoff infosys documentation for a Function Block. Always call "
        + "this before writing code that uses an unfamiliar Beckhoff FB; returns inputs, outputs, and "
        + "a description. fbName is the FB name (e.g. FB_EcCoESdoRead).")]
    public Task<string> FindFb(string fbName, CancellationToken cancellationToken = default)
        => RunAsync(() => searcher.FindFbAsync(fbName, cancellationToken));

    [McpServerTool(Name = "SearchDocs")]
    [Description("Search Beckhoff infosys documentation for a term. section optionally scopes the "
        + "search to one infosys section (e.g. tcplclib_tc2_ethercat); leave empty to search the "
        + "known sections. Sections must have been indexed by a prior FindFb call.")]
    public Task<string> SearchDocs(
        string query, string section = "", CancellationToken cancellationToken = default)
        => RunAsync(() => searcher.SearchAsync(query, Optional(section), cancellationToken));

    [McpServerTool(Name = "FindHardware")]
    [Description("Look up Beckhoff infosys documentation for a hardware product by order number "
        + "(e.g. EL3004, EL1008, EP1xxx). Returns the terminal's description and its parsed "
        + "'Technical data' table. Pairs with ScanHardware / the EtherCAT authoring verbs, which deal "
        + "in the same order numbers. Covers EtherCAT terminals/boxes/measurement modules.")]
    public Task<string> FindHardware(string orderNumber, CancellationToken cancellationToken = default)
        => RunAsync(() => searcher.FindHardwareAsync(orderNumber, cancellationToken));

    [McpServerTool(Name = "GetDocPage")]
    [Description("Fetch and parse a specific Beckhoff infosys page. Pages are cached locally; prefer "
        + "FindFb for looking up specific FBs. url is a full infosys URL (direct content or "
        + "english.php wrapper).")]
    public Task<string> GetDocPage(string url, CancellationToken cancellationToken = default)
        => RunAsync(() => searcher.GetPageAsync(url, cancellationToken));

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
