using System.ComponentModel;
using ModelContextProtocol.Server;
using TcKit.Core.Models;
using TcKit.Core.Ports;
using TcKit.Core.Serialization;

namespace TcKit.Server.Tools;

/// <summary>
/// Documentation-generator tools. Render a TwinCAT solution's embedded ST doc comments into an HTML
/// site or Markdown files. Unrelated to the infosys <see cref="DocsTools"/>: this lane reads local
/// source only. PascalCase tool names, camelCase parameters, snake_case output JSON.
/// </summary>
[McpServerToolType]
public sealed class DocGenTools(IDocGenerator generator)
{
    [McpServerTool(Name = "GenerateDocs")]
    [Description("Generate documentation from comments embedded in TwinCAT ST source. Auto-detects RST "
        + "line, RST block, and Beckhoff XML <docu> comments. projectPath is the TwinCAT solution "
        + "directory; outputPath is where the docs are written (index.html or index.md is the entry "
        + "point). format is 'html' (self-contained site, default) or 'markdown' (GitHub Flavoured "
        + "Markdown files).")]
    public async Task<string> GenerateDocs(
        string projectPath, string outputPath, string format = "html", CancellationToken cancellationToken = default)
    {
        try
        {
            var docFormat = ParseFormat(format);
            var result = await generator.GenerateAsync(projectPath, outputPath, docFormat, cancellationToken)
                .ConfigureAwait(false);
            return TckitJson.Serialize(result);
        }
#pragma warning disable CA1031 // The tool boundary funnels every failure into the error contract.
        catch (Exception exc)
        {
            return TckitJson.Serialize(new { error = exc.Message });
        }
#pragma warning restore CA1031
    }

    [McpServerTool(Name = "GetDocStatus")]
    [Description("Return the status of the most recent GenerateDocs run: idle, generating, complete, or error.")]
    public string GetDocStatus()
    {
        try
        {
            return TckitJson.Serialize(new { status = generator.Status });
        }
#pragma warning disable CA1031 // The tool boundary funnels every failure into the error contract.
        catch (Exception exc)
        {
            return TckitJson.Serialize(new { error = exc.Message });
        }
#pragma warning restore CA1031
    }

    private static DocFormat ParseFormat(string format) => format.Trim().ToLowerInvariant() switch
    {
        "" or "html" => DocFormat.Html,
        "markdown" or "md" => DocFormat.Markdown,
        _ => throw new ArgumentException($"Unknown doc format '{format}'. Use 'html' or 'markdown'."),
    };
}
