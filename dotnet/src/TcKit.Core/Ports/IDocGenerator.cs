using TcKit.Core.Models;

namespace TcKit.Core.Ports;

/// <summary>
/// Generate documentation from the doc comments embedded in TwinCAT ST source. The generator parses
/// local <c>.TcPOU</c> / <c>.TcGVL</c> / <c>.TcDUT</c> files (auto-detecting RST line, RST block, and
/// Beckhoff XML <c>&lt;docu&gt;</c> comment styles) and renders either an HTML site or Markdown files.
/// This is the local-source generator; it is unrelated to the infosys <see cref="IDocsSearcher"/>.
/// </summary>
public interface IDocGenerator
{
    /// <summary>
    /// Generate documentation for a TwinCAT solution directory. One sub-tree is emitted per
    /// <c>.plcproj</c> (ADR-0005); the entry point is <c>index.html</c> or <c>index.md</c>. Returns a
    /// successful <see cref="Result"/> whose details carry the index path and object count, or a failed
    /// one carrying the error message.
    /// </summary>
    Task<Result> GenerateAsync(
        string projectPath, string outputPath, DocFormat format, CancellationToken cancellationToken);

    /// <summary>The status of the most recent generation run on this instance.</summary>
    DocStatus Status { get; }
}
