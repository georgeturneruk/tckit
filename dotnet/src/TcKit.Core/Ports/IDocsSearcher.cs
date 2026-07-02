using TcKit.Core.Models;

namespace TcKit.Core.Ports;

/// <summary>
/// Search and retrieve Beckhoff infosys documentation. Always call <see cref="FindFbAsync"/> before
/// writing code that uses an unfamiliar Beckhoff FB: Beckhoff FBs have specific input/output
/// conventions and timing requirements that are not reliably in training data, especially for newer
/// TF libraries.
/// </summary>
public interface IDocsSearcher
{
    /// <summary>
    /// Locate and parse the infosys page for a Function Block. Combines search and page-fetch in one;
    /// the most common call. Throws <see cref="FileNotFoundException"/> when no page can be found.
    /// </summary>
    Task<FbDoc> FindFbAsync(string fbName, CancellationToken cancellationToken);

    /// <summary>
    /// Fetch the top-level documentation page for a Beckhoff library. Throws
    /// <see cref="FileNotFoundException"/> when no page can be found.
    /// </summary>
    Task<LibraryDoc> FindLibraryAsync(string libraryName, CancellationToken cancellationToken);

    /// <summary>
    /// Search infosys for a term by scanning section indexes, optionally scoped to one section
    /// (e.g. <c>tcplclib_tc2_ethercat</c>). Pass null/empty to search the known sections.
    /// </summary>
    Task<SearchResults> SearchAsync(string query, string? section, CancellationToken cancellationToken);

    /// <summary>
    /// Fetch and parse a specific infosys page. Pages are cached locally to protect against HTML
    /// structure changes and to avoid re-fetching. Accepts direct content URLs or english.php
    /// wrapper URLs.
    /// </summary>
    Task<DocPage> GetPageAsync(string url, CancellationToken cancellationToken);

    /// <summary>
    /// Locate the infosys documentation for a Beckhoff hardware product by order number (e.g.
    /// EL3004, EP1xxx): the terminal page's description plus its parsed "Technical data" table.
    /// Throws <see cref="FileNotFoundException"/> when no matching section/page can be found.
    /// </summary>
    Task<HardwareDoc> FindHardwareAsync(string orderNumber, CancellationToken cancellationToken);
}
