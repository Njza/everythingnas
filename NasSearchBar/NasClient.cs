using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace NasSearchBar;

// Talks to nas_search.py's /search endpoint
public sealed class NasClient(Uri server, string user, string password)
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    readonly string? auth = user == "" ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    public async Task<SearchResponse> SearchAsync(string q, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(server, "search?q=" + Uri.EscapeDataString(q)));
        if (auth is not null) req.Headers.Authorization = new("Basic", auth);
        using var res = await Http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<SearchResponse>(SearchResponse.Json, ct)
               ?? throw new JsonException("empty response");
    }
}
