using System.Net.Http;

namespace TcKit.Adapters.Docs;

/// <summary>
/// The live <see cref="IInfosysClient"/>: a shared <see cref="HttpClient"/> with a browser User-Agent
/// (infosys serves a stripped page to unknown agents) and a short timeout. Non-200 responses and
/// transport errors collapse to null so callers treat "not reachable" and "not found" alike, matching
/// the Python adapter's best-effort navigation.
/// </summary>
internal sealed class HttpInfosysClient : IInfosysClient
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        + "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private static readonly HttpClient Client = CreateClient();

    public async Task<string?> GetAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best-effort fetch: any transport failure becomes "not reachable" (null).
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        return client;
    }
}
