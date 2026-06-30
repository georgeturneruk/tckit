namespace TcKit.Core.Models;

/// <summary>
/// Lifecycle of a documentation-generation run, surfaced by <c>get_doc_status</c>. Serialised
/// snake_case (idle / generating / complete / error) via <see cref="Serialization.TckitJson"/>.
/// </summary>
public enum DocStatus
{
    Idle,
    Generating,
    Complete,
    Error,
}

/// <summary>Output format for the documentation generator.</summary>
public enum DocFormat
{
    /// <summary>Self-contained HTML site (search, hierarchy, cross-references, dark/light toggle).</summary>
    Html,

    /// <summary>GitHub Flavoured Markdown files, one per object plus per-PLC and solution indexes.</summary>
    Markdown,
}
