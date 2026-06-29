namespace TcKit.Adapters.Docs;

/// <summary>
/// The HTTP seam for the infosys lane: fetch a URL and return its body, or null on any
/// non-success / transport error. Keeping the single network primitive behind an interface lets the
/// navigator, parser, and searcher logic be CI-tested against canned HTML without a live infosys.
/// </summary>
internal interface IInfosysClient
{
    /// <summary>GET the URL (following redirects) and return the body text, or null on failure.</summary>
    Task<string?> GetAsync(string url, CancellationToken cancellationToken);
}
