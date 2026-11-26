using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NasSearchBar;

public class NasClientTests
{
    const string Json = """{"total": 1, "indexed": 5, "scanned": 0, "results": [{"name": "a.docx", "path": "\\\\N\\docs", "size": 1, "mtime": 0}]}""";

    // Tiny stand-in for nas_search.py: answers every request with `status` and `body`, records the request
    sealed class FakeServer : IDisposable
    {
        readonly HttpListener listener = new();
        public Uri Url { get; }
        public string? Auth { get; private set; }
        public string? Query { get; private set; }

        public FakeServer(int status = 200, string body = Json)
        {
            Url = new Uri($"http://127.0.0.1:{FreePort()}/");
            listener.Prefixes.Add(Url.ToString());
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    var ctx = await listener.GetContextAsync();
                    Auth = ctx.Request.Headers["Authorization"];
                    Query = ctx.Request.QueryString["q"];
                    ctx.Response.StatusCode = status;
                    await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body));
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose() => listener.Close();
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Returns_results_and_sends_the_query()
    {
        using var nas = new FakeServer();
        var r = await new NasClient(nas.Url, "", "").SearchAsync("合同 2025", CancellationToken.None);
        Assert.Equal("a.docx", r.Results[0].Name);
        Assert.Equal(5, r.Indexed);
        Assert.Equal("合同 2025", nas.Query);
        Assert.Null(nas.Auth);
    }

    [Fact]
    public async Task Reads_the_indexed_formats()
    {
        using var nas = new FakeServer(body: """{"exts": [{"ext": "pdf", "count": 12}, {"ext": "docx", "count": 3}]}""");
        var r = await new NasClient(nas.Url, "", "").ExtsAsync(CancellationToken.None);
        Assert.Equal([new ExtCount("pdf", 12), new ExtCount("docx", 3)], r.Exts);
    }

    [Fact]
    public async Task Localhost_reaches_an_ipv4_only_server_without_the_ipv6_delay()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);  // IPv4 only, like nas_search.py
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var c = await l.AcceptTcpClientAsync();
            var s = c.GetStream();
            await s.ReadAsync(new byte[4096]);
            var body = Encoding.UTF8.GetBytes("""{"exts": []}""");
            await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n"));
            await s.WriteAsync(body);
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await new NasClient(new Uri($"http://localhost:{port}/"), "", "").ExtsAsync(CancellationToken.None);
        l.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");  // ::1 first took ~2 s on Windows
    }

    [Fact]
    public async Task Sends_basic_auth()
    {
        using var nas = new FakeServer();
        await new NasClient(nas.Url, "me", "pä:ss").SearchAsync("a", CancellationToken.None);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("me:pä:ss")), nas.Auth);
    }

    [Fact]
    public async Task Wrong_password_is_a_401()
    {
        using var nas = new FakeServer(401, "");
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => new NasClient(nas.Url, "me", "bad").SearchAsync("a", CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, e.StatusCode);
    }

    // nas_search.py on HTTPS with a self-signed certificate, like DSM's: answers every connection with Json
    static (Uri Url, string Pin) FakeHttpsServer()
    {
        using var key = RSA.Create(2048);
        using var made = new CertificateRequest("CN=nas", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        var cert = X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);  // Windows needs a stored key
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                using var c = await l.AcceptTcpClientAsync();
                try
                {
                    using var tls = new SslStream(c.GetStream());
                    await tls.AuthenticateAsServerAsync(cert);
                    await tls.ReadAsync(new byte[4096]);
                    var body = Encoding.UTF8.GetBytes(Json);
                    await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n"));
                    await tls.WriteAsync(body);
                }
                catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException) { }  // a refused client
            }
        });
        return (new Uri($"https://127.0.0.1:{((IPEndPoint)l.LocalEndpoint).Port}/"), cert.GetCertHashString(HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task First_connection_pins_the_nas_certificate()
    {
        var (url, pin) = FakeHttpsServer();
        var nas = new NasClient(url, "me", "pw");
        Assert.Equal("a.docx", (await nas.SearchAsync("a", CancellationToken.None)).Results[0].Name);
        Assert.Equal(pin, nas.Pin);
        Assert.False(nas.CertChanged);
        await new NasClient(url, "me", "pw", pin).SearchAsync("a", CancellationToken.None);  // the same one: fine
    }

    [Fact]
    public async Task Another_certificate_is_refused()
    {
        var (url, _) = FakeHttpsServer();
        var nas = new NasClient(url, "me", "pw", "00" + new string('A', 62));  // pinned to some other NAS
        await Assert.ThrowsAsync<HttpRequestException>(() => nas.SearchAsync("a", CancellationToken.None));
        Assert.True(nas.CertChanged);
    }

    [Fact]
    public async Task Nas_down_is_a_connection_error() =>
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new NasClient(new Uri($"http://127.0.0.1:{FreePort()}/"), "", "").SearchAsync("a", CancellationToken.None));
}
