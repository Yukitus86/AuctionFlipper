using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AuctionFlipper.Core;
using AuctionFlipper.Ui;

namespace AuctionFlipper.Services;

/// <summary>
/// A read-only dashboard served on the loopback interface, so the board can live on a second
/// monitor or a phone on the same machine while the desktop app does the work.
///
/// It is a hand-rolled HTTP/1.1 server over a TCP listener rather than <c>HttpListener</c>, for one
/// practical reason: HttpListener needs a URL ACL reservation, which means running the tool as
/// administrator or asking the user to run a netsh command before a local page will open. A plain
/// socket on 127.0.0.1 needs no permission at all.
///
/// It binds to loopback only and never exposes the API key.
/// </summary>
public sealed class WebDashboardServer : IDisposable
{
    private readonly int _port;
    private readonly Func<DashboardSnapshot> _snapshotProvider;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly List<StreamWriter> _eventClients = new();
    private readonly object _clientGate = new();

    public WebDashboardServer(int port, Func<DashboardSnapshot> snapshotProvider)
    {
        _port = port;
        _snapshotProvider = snapshotProvider;
    }

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }
    public string Url => $"http://127.0.0.1:{_port}/";

    public void Start()
    {
        if (IsRunning) return;

        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            IsRunning = true;
            LastError = null;
        }
        catch (SocketException ex)
        {
            LastError = $"Could not open port {_port}: {ex.Message}";
            return;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _ = Task.Run(() => BroadcastLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); }
        catch (SocketException) { }

        lock (_clientGate)
        {
            foreach (StreamWriter writer in _eventClients)
            {
                try { writer.Dispose(); } catch (IOException) { }
            }
            _eventClients.Clear();
        }

        IsRunning = false;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
            var writer = new StreamWriter(stream, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = false };

            string? requestLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (requestLine is null) { client.Dispose(); return; }

            // Drain headers; nothing here needs them, but the request must be consumed.
            while (true)
            {
                string? header = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(header)) break;
            }

            string[] parts = requestLine.Split(' ');
            string path = parts.Length > 1 ? parts[1] : "/";
            int query = path.IndexOf('?');
            if (query >= 0) path = path[..query];

            switch (path)
            {
                case "/" or "/index.html":
                    await WriteAssetAsync(writer, "index.html", "text/html; charset=utf-8", ct);
                    break;
                case "/app.js":
                    await WriteAssetAsync(writer, "app.js", "application/javascript; charset=utf-8", ct);
                    break;
                case "/style.css":
                    await WriteAssetAsync(writer, "style.css", "text/css; charset=utf-8", ct);
                    break;
                case "/api/state":
                    await WriteTextAsync(writer, 200, "application/json; charset=utf-8",
                        BuildJson(_snapshotProvider()), ct);
                    break;
                case "/events":
                    await ServeEventStreamAsync(client, writer, ct);
                    return;   // the socket stays open and is owned by the broadcast loop
                default:
                    await WriteTextAsync(writer, 404, "text/plain; charset=utf-8", "Not found", ct);
                    break;
            }

            await writer.FlushAsync(ct).ConfigureAwait(false);
            client.Dispose();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            try { client.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task ServeEventStreamAsync(TcpClient client, StreamWriter writer, CancellationToken ct)
    {
        await writer.WriteAsync(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/event-stream\r\n" +
            "Cache-Control: no-cache\r\n" +
            "Connection: keep-alive\r\n\r\n").ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);

        // Send the current state immediately so the page is not blank until the next broadcast.
        await SendEventAsync(writer, BuildJson(_snapshotProvider()), ct).ConfigureAwait(false);

        lock (_clientGate) _eventClients.Add(writer);

        try
        {
            // Hold the connection until the client goes away or the server stops.
            while (!ct.IsCancellationRequested && client.Connected)
                await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_clientGate) _eventClients.Remove(writer);
            try { client.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);

                StreamWriter[] clients;
                lock (_clientGate)
                {
                    if (_eventClients.Count == 0) continue;
                    clients = _eventClients.ToArray();
                }

                // Built once and shared: the snapshot is identical for every viewer.
                string json = BuildJson(_snapshotProvider());

                foreach (StreamWriter writer in clients)
                {
                    try
                    {
                        await SendEventAsync(writer, json, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                        lock (_clientGate) _eventClients.Remove(writer);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task SendEventAsync(StreamWriter writer, string json, CancellationToken ct)
    {
        await writer.WriteAsync("data: ").ConfigureAwait(false);
        await writer.WriteAsync(json).ConfigureAwait(false);
        await writer.WriteAsync("\n\n").ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(StreamWriter writer, int status, string contentType, string body,
        CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        await writer.WriteAsync(
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {bytes.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n").ConfigureAwait(false);
        await writer.WriteAsync(body).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteAssetAsync(StreamWriter writer, string name, string contentType,
        CancellationToken ct)
    {
        string body = LoadAsset(name) ?? "Asset missing.";
        await WriteTextAsync(writer, 200, contentType, body, ct).ConfigureAwait(false);
    }

    private static readonly Dictionary<string, string> AssetCache = new(StringComparer.Ordinal);

    private static string? LoadAsset(string name)
    {
        lock (AssetCache)
        {
            if (AssetCache.TryGetValue(name, out string? cached)) return cached;

            Assembly assembly = typeof(WebDashboardServer).Assembly;
            string? resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("Web." + name, StringComparison.Ordinal));
            if (resource is null) return null;

            using Stream? stream = assembly.GetManifestResourceStream(resource);
            if (stream is null) return null;

            using var reader = new StreamReader(stream, Encoding.UTF8);
            string text = reader.ReadToEnd();
            AssetCache[name] = text;
            return text;
        }
    }

    // ------------------------------------------------------------------ payload

    private static string BuildJson(DashboardSnapshot snapshot)
    {
        var buffer = new MemoryStream(16 * 1024);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("budgetUsed", snapshot.BudgetUsed);
            w.WriteNumber("budgetLimit", snapshot.BudgetLimit);
            w.WriteString("book", snapshot.BookText);
            w.WriteString("tape", snapshot.TapeText);
            w.WriteString("sniper", snapshot.SniperText);
            w.WriteString("scan", snapshot.ScanText);
            w.WriteString("uptime", snapshot.UptimeText);

            w.WriteStartArray("flips");
            foreach (FlipOpportunity flip in snapshot.Flips)
            {
                w.WriteStartObject();
                w.WriteString("item", flip.Info.DisplayName);
                w.WriteString("mono", Format.Monogram(flip.Info.DisplayName));
                w.WriteNumber("hue", flip.Info.Hue);
                w.WriteNumber("count", flip.Count);
                w.WriteString("grade", Format.Grade(flip.Grade));
                w.WriteString("buy", Format.Coins(flip.BuyTotal));
                w.WriteString("sell", Format.Coins(flip.ResellTotal));
                w.WriteString("net", Format.Signed(flip.NetProfit));
                w.WriteNumber("netRaw", flip.NetProfit);
                w.WriteString("roi", Format.Percent(flip.Roi));
                w.WriteString("rate", Format.Rate(flip.SalesPerHour));
                w.WriteNumber("confidence", Math.Round(flip.Confidence));
                w.WriteString("age", Format.Age(flip.AgeMs));
                w.WriteString("absorb", Format.Absorb(flip.AbsorbHours));
                w.WriteString("seller", flip.SellerName);
                w.WriteString("badges", string.Join("|", Format.Badges(flip.Flags)));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public void Dispose() => Stop();
}

public sealed record DashboardSnapshot(
    int BudgetUsed,
    int BudgetLimit,
    string BookText,
    string TapeText,
    string SniperText,
    string ScanText,
    string UptimeText,
    IReadOnlyList<FlipOpportunity> Flips);
