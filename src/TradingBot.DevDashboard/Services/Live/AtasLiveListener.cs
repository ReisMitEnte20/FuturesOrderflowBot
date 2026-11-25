using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace TradingBot.DevDashboard.Services.Live;

/// <summary>
/// Lokaler Empfang der ATAS-Bridge: TCP nur auf 127.0.0.1, eine Quelle gleichzeitig (eine neue Verbindung ersetzt die alte).
/// Zeilen (NDJSON) laufen über eine begrenzte Warteschlange in den <see cref="AtasLiveFeed"/>. Ist sie voll, wird die Zeile
/// verworfen und GEZÄHLT (BackendDropped) – nie still. Nur lesend: es wird nichts an ATAS zurückgesendet.
/// </summary>
public sealed class AtasLiveListener : BackgroundService
{
    private readonly AtasLiveFeed _feed;
    private readonly int _port;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(200_000) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private TcpClient? _current;
    public int Port => _port;
    public string? ListenError { get; private set; }

    public AtasLiveListener(AtasLiveFeed feed, int port)
    {
        _feed = feed;
        _port = port;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var consumer = Task.Run(() => ConsumeAsync(ct), ct);
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, _port);
            listener.Start();
        }
        catch (SocketException ex)
        {
            ListenError = $"Port {_port} nicht verfügbar: {ex.Message}";
            await consumer;
            return;
        }
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                var old = Interlocked.Exchange(ref _current, client);
                if (old is not null) { try { old.Close(); } catch { /* alte Quelle ersetzt */ } }
                _ = Task.Run(() => ReadClientAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    private async Task ReadClientAsync(TcpClient client, CancellationToken ct)
    {
        _feed.OnConnected(client.Client.RemoteEndPoint?.ToString() ?? "?");
        string reason = "Bridge hat die Verbindung beendet";
        try
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (line.Length == 0) continue;
                if (!_queue.Writer.TryWrite(line)) _feed.NoteBackendDrop(1);
            }
        }
        catch (OperationCanceledException) { reason = "Backend beendet"; }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { reason = $"Verbindung abgebrochen ({ex.GetType().Name})"; }
        finally
        {
            // Nur die AKTUELLE Quelle meldet „getrennt“ (eine ersetzte alte Verbindung nicht).
            if (ReferenceEquals(Volatile.Read(ref _current), client)) _feed.OnDisconnected(reason);
            try { client.Close(); } catch { }
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var line in _queue.Reader.ReadAllAsync(ct)) _feed.Ingest(line);
        }
        catch (OperationCanceledException) { }
    }
}
