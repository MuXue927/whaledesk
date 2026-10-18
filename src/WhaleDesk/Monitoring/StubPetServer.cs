using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WhaleDesk.Monitoring;

/// <summary>
/// A tiny loopback HTTP server that serves a canned /api/pet/state response.
///
/// Used by --probe-hotplug to prove the pet RECOVERS from a DSH that goes away and comes back
/// (DSH restart, plugin reinstall, crash) without touching the user's real DSH. TcpListener is
/// used instead of HttpListener on purpose: HttpListener needs a URL ACL when not elevated,
/// while a raw socket on 127.0.0.1 needs nothing.
/// </summary>
internal sealed class StubPetServer : IDisposable
{
    private readonly TcpListener _listener;
    private CancellationTokenSource? _cts;
    private string _payload = "{}";

    public int Port { get; }
    public int Hits { get; private set; }
    public int Errors { get; private set; }
    public Action<string>? Log { get; set; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public bool Listening { get; private set; }

    public StubPetServer(int port)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start(string json)
    {
        _payload = json;
        if (Listening) return;
        _listener.Start();
        Listening = true;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        if (!Listening) return;
        try { _listener.Stop(); } catch { }
        Listening = false;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                var body = Encoding.ASCII.GetString(buffer, 0, Math.Max(0, read));
                Hits++;
                Log?.Invoke("stub request: " + body.Split(' ').FirstOrDefault());
                var path = body.Split(' ').Length > 1 ? body.Split(' ')[1] : "/";

                var json = path.Contains("pets", StringComparison.Ordinal)
                    ? "{\"pets\":[{\"id\":\"whale\",\"name\":\"stub\"}]}"
                    : _payload;
                var bytes = Encoding.UTF8.GetBytes(json);
                var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                var head = Encoding.ASCII.GetBytes(header);
                await stream.WriteAsync(head, ct).ConfigureAwait(false);
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // cancellation / aborted client: the loop ends on cancellation, otherwise retry
                await Task.Delay(40, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                try { client?.Dispose(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        Stop();
        try { _listener.Stop(); } catch { }
    }
}
