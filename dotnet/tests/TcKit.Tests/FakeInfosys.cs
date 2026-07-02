using TcKit.Adapters.Docs;

namespace TcKit.Tests;

/// <summary>
/// In-memory fake of the infosys HTTP seam. Serves canned HTML by exact URL (null for anything not
/// stocked, mirroring a 404/transport miss) so the navigator, parser, and searcher are testable
/// without a live infosys. Records every requested URL for call-count assertions.
/// </summary>
internal sealed class FakeInfosysClient : IInfosysClient
{
    private readonly Dictionary<string, string> _pages;

    public FakeInfosysClient(Dictionary<string, string>? pages = null) =>
        _pages = pages ?? new Dictionary<string, string>(StringComparer.Ordinal);

    public List<string> Requested { get; } = [];

    public void Add(string url, string html) => _pages[url] = html;

    public Task<string?> GetAsync(string url, CancellationToken cancellationToken)
    {
        Requested.Add(url);
        return Task.FromResult(_pages.TryGetValue(url, out var html) ? html : null);
    }
}
