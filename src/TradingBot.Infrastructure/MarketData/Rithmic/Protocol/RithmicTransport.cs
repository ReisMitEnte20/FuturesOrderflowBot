using System.Net.WebSockets;

namespace TradingBot.Infrastructure.MarketData.Rithmic.Protocol;

/// <summary>
/// Transport für R|Protocol: eine WebSocket-Binary-Message = eine serialisierte Protobuf-Message.
/// Abstrahiert, damit Session/Client ohne Netzwerk getestet werden können.
/// </summary>
public interface IRithmicTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);
    Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);

    /// <summary>Nächste vollständige Message; <c>null</c>, wenn die Verbindung geschlossen wurde.</summary>
    Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}

public delegate IRithmicTransport RithmicTransportFactory();

/// <summary>TLS-WebSocket-Transport (wss://). Zertifikatsprüfung über den System-Trust-Store.</summary>
public sealed class WebSocketRithmicTransport : IRithmicTransport
{
    private readonly ClientWebSocket _socket = new();

    public WebSocketRithmicTransport()
    {
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(60);
    }

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        _socket.ConnectAsync(uri, cancellationToken);

    public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) =>
        _socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).AsTask();

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken);
            }
            catch (WebSocketException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return message.ToArray();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing Connection", cancellationToken);
            }
            catch (WebSocketException)
            {
                // Gegenseite bereits weg – nichts mehr zu tun.
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
