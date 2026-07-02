namespace TcKit.Core.Models;

/// <summary>
/// DTOs for the infosys DocsSearcher lane. snake_case JSON via <c>TckitJson</c>; the analogues of
/// the Python <c>ports/types.py</c> DocsSearcher dataclasses.
/// </summary>
public sealed record ParameterDoc(string Name, string Type, string Direction, string Description);

/// <summary>A Function Block's parsed infosys page: description plus its input/output parameters.</summary>
public sealed record FbDoc
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Url { get; init; }
    public IReadOnlyList<ParameterDoc> Inputs { get; init; } = [];
    public IReadOnlyList<ParameterDoc> Outputs { get; init; } = [];
    public string? Notes { get; init; }
}

/// <summary>A library's top-level infosys page: description plus the FBs it documents.</summary>
public sealed record LibraryDoc
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Url { get; init; }
    public IReadOnlyList<string> FunctionBlocks { get; init; } = [];
}

/// <summary>A single infosys search hit.</summary>
public sealed record SearchResult(string Title, string Url, string Snippet);

/// <summary>The result set for one infosys search.</summary>
public sealed record SearchResults
{
    public required string Query { get; init; }
    public IReadOnlyList<SearchResult> Results { get; init; } = [];
}

/// <summary>A fetched-and-parsed infosys page. <see cref="Cached"/> is true when served from disk.</summary>
public sealed record DocPage(string Url, string Title, string Content, bool Cached = false);

/// <summary>One property/value pair from a hardware terminal's "Technical data" table.</summary>
public sealed record TechnicalDataItem(string Property, string Value);

/// <summary>
/// A Beckhoff hardware product's infosys documentation, located by order number (e.g. EL3004): the
/// terminal page's description plus its parsed "Technical data" table (empty when none was found).
/// </summary>
public sealed record HardwareDoc
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Url { get; init; }
    public IReadOnlyList<TechnicalDataItem> TechnicalData { get; init; } = [];
}
