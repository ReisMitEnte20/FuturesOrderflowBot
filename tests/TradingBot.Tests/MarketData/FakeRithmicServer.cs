using System.Threading.Channels;
using Google.Protobuf;
using TradingBot.Infrastructure.MarketData.Rithmic.Protocol;
using TradingBot.Infrastructure.MarketData.Rithmic.Protocol.Messages;

namespace TradingBot.Tests.MarketData;

/// <summary>
/// In-Memory-Nachbildung eines Rithmic-R|Protocol-Gateways (kein Netzwerk). Antwortet auf System-Info,
/// Login, Heartbeat, Logout, MarketDataUpdate, FrontMonth und TimeBarReplay wie im Protokoll beschrieben.
/// </summary>
internal sealed class FakeRithmicServer
{
    public List<string> Systems { get; } = ["Rithmic Test", "Rithmic Paper Trading"];
    public List<RequestLogin> Logins { get; } = [];
    public List<int> ReceivedTemplates { get; } = [];
    public List<FakeRithmicTransport> Connections { get; } = [];
    public List<Uri> ConnectedUris { get; } = [];

    /// <summary>rp_code der Login-Antwort (Standard: Erfolg).</summary>
    public string[] LoginRpCode { get; set; } = ["0"];
    public double HeartbeatInterval { get; set; } = 30;

    /// <summary>Wird nach erfolgreichem Subscribe aufgerufen, um Stream-Messages zu pushen.</summary>
    public Action<FakeRithmicTransport, RequestMarketDataUpdate>? OnSubscribe { get; set; }

    /// <summary>Liefert die Bars zu einem Replay-Request (Standard: keine).</summary>
    public Func<RequestTimeBarReplay, IEnumerable<ResponseTimeBarReplay>> Bars { get; set; } = _ => [];

    public string FrontMonth { get; set; } = "MESZ6";

    /// <summary>Serverseitig bestehende Abos ("EXCHANGE:SYMBOL"); doppeltes Abo -> rp_code 1029 wie bei Rithmic.</summary>
    public HashSet<string> Subscriptions { get; } = [];

    public IRithmicTransport CreateTransport()
    {
        var transport = new FakeRithmicTransport(this);
        lock (Connections)
            Connections.Add(transport);
        return transport;
    }

    internal void Handle(FakeRithmicTransport connection, byte[] bytes)
    {
        var templateId = Base.Parser.ParseFrom(bytes).TemplateId;
        lock (ReceivedTemplates)
            ReceivedTemplates.Add(templateId);

        switch (templateId)
        {
            case RithmicTemplates.RequestRithmicSystemInfo:
            {
                var request = RequestRithmicSystemInfo.Parser.ParseFrom(bytes);
                var response = new ResponseRithmicSystemInfo { TemplateId = RithmicTemplates.ResponseRithmicSystemInfo };
                response.UserMsg.AddRange(request.UserMsg);
                response.RpCode.Add("0");
                response.SystemName.AddRange(Systems);
                connection.Push(response);
                break;
            }
            case RithmicTemplates.RequestLogin:
            {
                var request = RequestLogin.Parser.ParseFrom(bytes);
                lock (Logins)
                    Logins.Add(request);
                var response = new ResponseLogin
                {
                    TemplateId = RithmicTemplates.ResponseLogin,
                    HeartbeatInterval = HeartbeatInterval,
                };
                response.UserMsg.AddRange(request.UserMsg);
                response.RpCode.AddRange(LoginRpCode);
                connection.Push(response);
                break;
            }
            case RithmicTemplates.RequestHeartbeat:
                connection.Push(new ResponseHeartbeat { TemplateId = RithmicTemplates.ResponseHeartbeat, RpCode = { "0" } });
                break;
            case RithmicTemplates.RequestLogout:
                connection.Push(new ResponseLogout { TemplateId = RithmicTemplates.ResponseLogout, RpCode = { "0" } });
                break;
            case RithmicTemplates.RequestMarketDataUpdate:
            {
                var request = RequestMarketDataUpdate.Parser.ParseFrom(bytes);
                var response = new ResponseMarketDataUpdate { TemplateId = RithmicTemplates.ResponseMarketDataUpdate };
                response.UserMsg.AddRange(request.UserMsg);
                var key = $"{request.Exchange}:{request.Symbol}";
                var subscribe = request.Request == RequestMarketDataUpdate.Types.Request.Subscribe;
                bool duplicate;
                lock (Subscriptions)
                    duplicate = subscribe ? !Subscriptions.Add(key) : !Subscriptions.Remove(key);
                if (subscribe && duplicate)
                    response.RpCode.AddRange(["1029", "update bit type already exists"]);
                else
                    response.RpCode.Add("0");
                connection.Push(response);
                if (subscribe && !duplicate)
                    OnSubscribe?.Invoke(connection, request);
                break;
            }
            case RithmicTemplates.RequestFrontMonthContract:
            {
                var request = RequestFrontMonthContract.Parser.ParseFrom(bytes);
                var response = new ResponseFrontMonthContract
                {
                    TemplateId = RithmicTemplates.ResponseFrontMonthContract,
                    Symbol = request.Symbol,
                    Exchange = request.Exchange,
                    TradingSymbol = FrontMonth,
                    IsFrontMonthSymbol = true,
                };
                response.UserMsg.AddRange(request.UserMsg);
                response.RpCode.Add("0");
                connection.Push(response);
                break;
            }
            case RithmicTemplates.RequestTimeBarReplay:
            {
                var request = RequestTimeBarReplay.Parser.ParseFrom(bytes);
                foreach (var bar in Bars(request))
                {
                    bar.TemplateId = RithmicTemplates.ResponseTimeBarReplay;
                    bar.UserMsg.AddRange(request.UserMsg);
                    bar.RqHandlerRpCode.Add("0");
                    connection.Push(bar);
                }
                var done = new ResponseTimeBarReplay { TemplateId = RithmicTemplates.ResponseTimeBarReplay };
                done.UserMsg.AddRange(request.UserMsg);
                done.RpCode.Add("0");
                connection.Push(done);
                break;
            }
        }
    }
}

internal sealed class FakeRithmicTransport : IRithmicTransport
{
    private readonly FakeRithmicServer _server;
    private readonly Channel<byte[]> _toClient = Channel.CreateUnbounded<byte[]>();

    public FakeRithmicTransport(FakeRithmicServer server) => _server = server;

    public bool Closed { get; private set; }

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        lock (_server.ConnectedUris)
            _server.ConnectedUris.Add(uri);
        return Task.CompletedTask;
    }

    public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        if (Closed)
            throw new InvalidOperationException("Transport geschlossen.");
        _server.Handle(this, message.ToArray());
        return Task.CompletedTask;
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _toClient.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        Closed = true;
        _toClient.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public void Push(IMessage message) => _toClient.Writer.TryWrite(message.ToByteArray());

    /// <summary>Simuliert Server-seitiges Schließen.</summary>
    public void ServerClose() => _toClient.Writer.TryComplete();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
