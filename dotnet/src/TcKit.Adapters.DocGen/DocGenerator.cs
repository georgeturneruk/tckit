using System.Text;
using TcKit.Core.Models;
using TcKit.Core.Ports;

namespace TcKit.Adapters.DocGen;

/// <summary>
/// Generates documentation from RST/XML-commented TwinCAT source. Self-contained: it parses the local
/// project tree into a <see cref="DocModel"/> and renders either a self-contained HTML site or a tree of
/// GitHub Flavoured Markdown files. No Sphinx, no plcdoc, no subprocess (the analogue of the Python
/// <c>HtmlGenerator</c> / <c>MarkdownGenerator</c> adapters, unified behind one format switch).
/// </summary>
/// <remarks>
/// Output layout (ADR-0005): a top-level <c>index.html</c>/<c>index.md</c> lists the PLC projects; per-PLC
/// pages live under <c>&lt;plc_name&gt;/</c>. The HTML form also writes a per-PLC <c>hierarchy.html</c> and
/// <c>search-index.json</c>. Used-by cross-references are scoped within a single PLC project.
/// </remarks>
public sealed class DocGenerator : IDocGenerator
{
    private volatile DocStatusBox _status = new(DocStatus.Idle);

    /// <inheritdoc />
    public DocStatus Status => _status.Value;

    /// <inheritdoc />
    public async Task<Result> GenerateAsync(
        string projectPath, string outputPath, DocFormat format, CancellationToken cancellationToken)
    {
        _status = new DocStatusBox(DocStatus.Generating);

        ProjectDoc project;
        try
        {
            project = DocModel.BuildProjectDoc(projectPath);
        }
        catch (NoSourceFilesException exc)
        {
            _status = new DocStatusBox(DocStatus.Error);
            return Result.Fail(exc.Message);
        }
#pragma warning disable CA1031 // Mirror the Python adapter: any parse failure becomes a failed Result.
        catch (Exception exc)
        {
            _status = new DocStatusBox(DocStatus.Error);
            return Result.Fail($"Failed to parse project: {exc.Message}");
        }
#pragma warning restore CA1031

        var output = new DirectoryInfo(Path.GetFullPath(outputPath));
        output.Create();

        try
        {
            var totalObjects = format == DocFormat.Markdown
                ? await RenderMarkdownAsync(project, output.FullName, cancellationToken).ConfigureAwait(false)
                : await RenderHtmlAsync(project, output.FullName, cancellationToken).ConfigureAwait(false);

            _status = new DocStatusBox(DocStatus.Complete);
            var indexName = format == DocFormat.Markdown ? "index.md" : "index.html";
            return Result.Ok(new Dictionary<string, object?>
            {
                ["index"] = Path.Combine(output.FullName, indexName),
                ["output_path"] = output.FullName,
                ["plcs"] = project.Plcs.Count,
                ["objects"] = totalObjects,
            });
        }
#pragma warning disable CA1031 // Render/IO failures are reported via the Result contract, not thrown to the caller.
        catch (Exception exc)
        {
            _status = new DocStatusBox(DocStatus.Error);
            return Result.Fail($"Failed to render templates: {exc.Message}");
        }
#pragma warning restore CA1031
    }

    private static async Task<int> RenderHtmlAsync(ProjectDoc project, string output, CancellationToken ct)
    {
        await WriteAsync(Path.Combine(output, "index.html"), HtmlRenderer.SolutionIndex(project), ct).ConfigureAwait(false);

        var total = 0;
        foreach (var plc in project.Plcs.Values)
        {
            var plcDir = Path.Combine(output, plc.Name);
            Directory.CreateDirectory(plcDir);

            var known = plc.Objects.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);

            await WriteAsync(Path.Combine(plcDir, "index.html"), HtmlRenderer.PlcIndex(plc), ct).ConfigureAwait(false);
            foreach (var obj in plc.Objects)
            {
                await WriteAsync(
                    Path.Combine(plcDir, $"{obj.Name}.html"), HtmlRenderer.ObjectPage(obj, plc, known), ct).ConfigureAwait(false);
            }

            await WriteAsync(Path.Combine(plcDir, "hierarchy.html"), HtmlRenderer.Hierarchy(plc, known), ct).ConfigureAwait(false);
            await WriteAsync(Path.Combine(plcDir, "search-index.json"), SearchIndex.Build(plc.Objects), ct).ConfigureAwait(false);
            total += plc.Objects.Count;
        }

        return total;
    }

    private static async Task<int> RenderMarkdownAsync(ProjectDoc project, string output, CancellationToken ct)
    {
        await WriteAsync(Path.Combine(output, "index.md"), MarkdownRenderer.SolutionIndex(project), ct).ConfigureAwait(false);

        var total = 0;
        foreach (var plc in project.Plcs.Values)
        {
            var plcDir = Path.Combine(output, plc.Name);
            Directory.CreateDirectory(plcDir);

            await WriteAsync(Path.Combine(plcDir, "index.md"), MarkdownRenderer.PlcIndex(plc), ct).ConfigureAwait(false);
            foreach (var obj in plc.Objects)
            {
                await WriteAsync(Path.Combine(plcDir, $"{obj.Name}.md"), MarkdownRenderer.ObjectPage(obj), ct).ConfigureAwait(false);
            }

            total += plc.Objects.Count;
        }

        return total;
    }

    private static Task WriteAsync(string path, string content, CancellationToken ct)
        => File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct);

    // Boxed status so the field can be swapped atomically via `volatile` (an enum field cannot be volatile).
    private sealed record DocStatusBox(DocStatus Value);
}
