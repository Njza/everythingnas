using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace NasSearchBar;

// Talks to nas_search.py's /search endpoint
public sealed class NasClient(Uri server, string user, string password)
{
    static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectCallback = ConnectIPv4First })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    // nas_search.py listens on IPv4 only, and Windows takes ~2 s to refuse an IPv6 try that comes first,
    // e.g. localhost -> ::1. That made every request take 2 s.
    static async ValueTask<Stream> ConnectIPv4First(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var ips = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };  // dual mode: takes both
        try
        {
            await socket.ConnectAsync(ips.OrderBy(a => a.AddressFamily != AddressFamily.InterNetwork).ToArray(),
                ctx.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    readonly string? auth = user == "" ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    public Task<SearchResponse> SearchAsync(string q, CancellationToken ct) =>
        GetAsync<SearchResponse>("search?q=" + Uri.EscapeDataString(q), ct);

    public Task<ExtsResponse> ExtsAsync(CancellationToken ct) => GetAsync<ExtsResponse>("exts", ct);

    async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(server, path));
        if (auth is not null) req.Headers.Authorization = new("Basic", auth);
        using var res = await Http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<T>(SearchResponse.Json, ct)
               ?? throw new JsonException("empty response");
    }
}
