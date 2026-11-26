using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace NasSearchBar;

// Talks to nas_search.py's /search endpoint. pin: the NAS certificate's SHA-256 from an earlier connection, or "".
public sealed class NasClient(Uri server, string user, string password, string pin = "")
{
    // The NAS serves DSM's own certificate, which is self-signed: the first one seen is trusted from then on and no
    // other one, so nobody in between can read the password. A certificate Windows trusts (a real one for the NAS's
    // name) is fine as it is.
    public string Pin { get; private set; } = pin;
    public bool CertChanged { get; private set; }  // the NAS showed another certificate than Pin: refused

    bool Trust(object _, X509Certificate? cert, X509Chain? __, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        var seen = cert?.GetCertHashString(HashAlgorithmName.SHA256);
        if (Pin == "" && seen is not null) Pin = seen;
        CertChanged = seen != Pin;
        return !CertChanged;
    }

    HttpClient? http;  // one per client: its certificate check needs this client's Pin
    HttpClient Http => http ??= new(new SocketsHttpHandler
    {
        ConnectCallback = ConnectIPv4First,
        SslOptions = { RemoteCertificateValidationCallback = Trust },
    })
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
