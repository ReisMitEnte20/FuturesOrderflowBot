using System.Collections.Concurrent;
using Google.Protobuf;
using TradingBot.Core.Interfaces;
using TradingBot.Infrastructure.MarketData.Rithmic.Protocol.Messages;

namespace TradingBot.Infrastructure.MarketData.Rithmic.Protocol;

/// <summary>
/// Eine eingeloggte Verbindung zu genau einem Rithmic-Plant (Ticker oder History).
/// Ablauf gemäß R|Protocol: WebSocket öffnen → RequestLogin → Heartbeat-Schleife; Antworten werden
/// über <c>user_msg</c> (Request-ID) ihren Requests zugeordnet, Streams (LastTrade/BBO) per Event gemeldet.
/// </summary>
public sealed class RithmicPlantSession : IAsyncDisposable
{
    private static readonly IReadOnlyDictionary<int, MessageParser> ResponseParsers = new Dictionary<int, MessageParser>
    {
        [RithmicTemplates.ResponseMarketDataUpdate] = ResponseMarketDataUpdate.Parser,
        [RithmicTemplates.ResponseFrontMonthContract] = ResponseFrontMonthContract.Parser,
        [RithmicTemplates.ResponseTimeBarReplay] = ResponseTimeBarReplay.Parser,
        [RithmicTemplates.Reject] = Reject.Parser,
    };

    private readonly RequestLogin.Types.SysInfraType _infraType;
    private readonly string _name;
    private readonly RithmicProtocolOptions _options;
    private readonly RithmicTransportFactory _transportFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();

    private IRithmicTransport? _transport;
    private CancellationTokenSource? _loopCts;
    private Task? _receiveLoop;
    private Task? _heartbeatLoop;
    private long _requestCounter;
    private volatile bool _loggedIn;

    public RithmicPlantSession(
        RithmicPlant plant,
        RithmicProtocolOptions options,
        RithmicTransportFactory transportFactory,
        ILogger? logger = null)
        : this(RithmicProtocolMapping.ToInfraType(plant), plant.ToString(), options, transportFactory, logger)
    {
        Plant = plant;
    }

    private RithmicPlantSession(
        RequestLogin.Types.SysInfraType infraType,
        string name,
        RithmicProtocolOptions options,
        RithmicTransportFactory transportFactory,
        ILogger? logger)
    {
        _infraType = infraType;
        _name = name;
        _options = options;
        _transportFactory = transportFactory;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Marktdaten-Plant; <c>null</c> nur bei der Conformance-Session (Order Plant, ohne Orders).</summary>
    public RithmicPlant? Plant { get; }
    public bool IsLoggedIn => _loggedIn;
    public TimeSpan HeartbeatInterval { get; private set; } = TimeSpan.FromSeconds(30);
    public string? LastError { get; private set; }

    /// <summary>Stream-Messages ohne Request-Bezug (z. B. LastTrade 150, BestBidOffer 151).</summary>
    public event Action<int, byte[]>? StreamMessage;

    /// <summary>Verbindung beendet (Server-Close, ForcedLogout oder Fehler). Parameter: Grund.</summary>
    public event Action<string>? Disconnected;

    /// <summary>
    /// NUR für den Rithmic-Conformance-Test: Session am Order Plant, die ausschließlich Login, Heartbeat und
    /// Logout sendet. Order-Messages existieren in diesem Projekt nicht (keine Order-Protos eingebunden).
    /// </summary>
    internal static RithmicPlantSession CreateConformanceOrderPlant(
        RithmicProtocolOptions options, RithmicTransportFactory transportFactory, ILogger? logger) =>
        new(RequestLogin.Types.SysInfraType.OrderPlant, "Conformance", options, transportFactory, logger);

    /// <summary>Ruft die verfügbaren System-Namen ab (eigene, kurzlebige Verbindung, kein Login nötig).</summary>
    public static async Task<IReadOnlyList<string>> GetSystemNamesAsync(
        RithmicProtocolOptions options, RithmicTransportFactory transportFactory, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);

        await using var transport = transportFactory();
        await transport.ConnectAsync(options.GatewayUri, timeout.Token);
        var request = new RequestRithmicSystemInfo { TemplateId = RithmicTemplates.RequestRithmicSystemInfo };
        request.UserMsg.Add("system-info");
        await transport.SendAsync(request.ToByteArray(), timeout.Token);

        while (true)
        {
            var bytes = await transport.ReceiveAsync(timeout.Token)
                ?? throw new RithmicProtocolException("Verbindung beim Abruf der System-Info geschlossen.");
            if (ReadTemplateId(bytes) != RithmicTemplates.ResponseRithmicSystemInfo)
                continue;

            var response = ResponseRithmicSystemInfo.Parser.ParseFrom(bytes);
            await transport.CloseAsync(timeout.Token);
            if (!RithmicProtocolMapping.IsSuccess(response.RpCode))
                throw new RithmicProtocolException(
                    $"System-Info abgelehnt: {RithmicProtocolMapping.Describe(response.RpCode)}", response.RpCode);
            return response.SystemName.ToList();
        }
    }

    public async Task LoginAsync(RithmicLoginCredentials credentials, CancellationToken cancellationToken = default)
    {
        if (_transport is not null)
            throw new InvalidOperationException($"{_name}-Session ist bereits verbunden.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        var transport = _transportFactory();
        try
        {
            await transport.ConnectAsync(_options.GatewayUri, timeout.Token);

            var login = new RequestLogin
            {
                TemplateId = RithmicTemplates.RequestLogin,
                TemplateVersion = _options.TemplateVersion,
                User = credentials.User,
                Password = credentials.Password,
                AppName = _options.AppName,
                AppVersion = _options.AppVersion,
                SystemName = credentials.SystemName,
                InfraType = _infraType,
            };
            login.UserMsg.Add("login");
            await transport.SendAsync(login.ToByteArray(), timeout.Token);

            ResponseLogin response;
            while (true)
            {
                var bytes = await transport.ReceiveAsync(timeout.Token)
                    ?? throw new RithmicProtocolException("Verbindung während des Logins geschlossen.");
                if (ReadTemplateId(bytes) == RithmicTemplates.ResponseLogin)
                {
                    response = ResponseLogin.Parser.ParseFrom(bytes);
                    break;
                }
            }

            if (!RithmicProtocolMapping.IsSuccess(response.RpCode))
                throw new RithmicProtocolException(
                    $"Login ({_name}) abgelehnt: {RithmicProtocolMapping.Describe(response.RpCode)}", response.RpCode);

            if (response.HeartbeatInterval > 0)
                HeartbeatInterval = TimeSpan.FromSeconds(response.HeartbeatInterval);
        }
        catch
        {
            await transport.CloseAsync(CancellationToken.None);
            await transport.DisposeAsync();
            throw;
        }

        _transport = transport;
        _loggedIn = true;
        LastError = null;
        _loopCts = new CancellationTokenSource();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_loopCts.Token));

        // Nach erfolgreichem Login erwartet Rithmic mindestens einen Heartbeat.
        await SendHeartbeatAsync(cancellationToken);
        _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_loopCts.Token));
        _logger.Info($"Rithmic {_name}-Plant eingeloggt (System {credentials.SystemName}).");
    }

    /// <summary>Sendet eine Message ohne Antwort-Erwartung (z. B. Heartbeat).</summary>
    public async Task SendAsync(IMessage message, CancellationToken cancellationToken = default)
    {
        var transport = _transport ?? throw new InvalidOperationException($"{_name}-Session ist nicht verbunden.");
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await transport.SendAsync(message.ToByteArray(), cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Sendet einen Request und sammelt alle zugehörigen Antworten (gleiche <c>user_msg</c>) bis zur
    /// Abschluss-Antwort mit gesetztem <c>rp_code</c>. rp_code "0" = OK, "7" = keine Daten, sonst Fehler.
    /// Die Abschluss-Antwort ist im Ergebnis enthalten (sie kann Daten tragen, z. B. FrontMonth).
    /// </summary>
    public async Task<IReadOnlyList<IMessage>> RequestAsync(IMessage request, CancellationToken cancellationToken = default)
    {
        var requestId = $"{_name.ToLowerInvariant()}-{Interlocked.Increment(ref _requestCounter)}";
        var userMsg = GetStringList(request, "user_msg")
            ?? throw new ArgumentException("Request-Typ hat kein user_msg-Feld.", nameof(request));
        userMsg.Add(requestId);

        var pending = new PendingRequest();
        _pending[requestId] = pending;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeout);
            await using var _ = timeout.Token.Register(() => pending.Completion.TrySetCanceled());

            await SendAsync(request, timeout.Token);
            return await pending.Completion.Task;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RithmicProtocolException($"Timeout: keine vollständige Antwort von Rithmic ({_name}) für {request.Descriptor.Name}.");
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var transport = _transport;
        if (transport is null)
            return;

        if (_loggedIn)
        {
            try
            {
                var logout = new RequestLogout { TemplateId = RithmicTemplates.RequestLogout };
                logout.UserMsg.Add("logout");
                await SendAsync(logout, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Rithmic {_name}-Logout konnte nicht gesendet werden: {ex.Message}");
            }
        }

        await ShutdownAsync("Logout", notify: false);
    }

    public async ValueTask DisposeAsync()
    {
        await LogoutAsync();
        _sendLock.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var reason = "Verbindung vom Server geschlossen.";
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytes = await _transport!.ReceiveAsync(cancellationToken);
                if (bytes is null)
                    break;

                var templateId = ReadTemplateId(bytes);
                if (templateId == RithmicTemplates.ForcedLogout)
                {
                    reason = "ForcedLogout von Rithmic (z. B. zu viele parallele Sessions).";
                    break;
                }

                if (templateId is RithmicTemplates.ResponseHeartbeat or RithmicTemplates.ResponseLogout)
                    continue;

                if (ResponseParsers.TryGetValue(templateId, out var parser))
                    Dispatch(templateId, parser.ParseFrom(bytes));
                else
                    StreamMessage?.Invoke(templateId, bytes);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            reason = $"Empfangsfehler: {ex.Message}";
        }

        if (!cancellationToken.IsCancellationRequested)
            await ShutdownAsync(reason, notify: true);
    }

    private void Dispatch(int templateId, IMessage message)
    {
        var requestId = GetStringList(message, "user_msg")?.FirstOrDefault();
        if (requestId is null || !_pending.TryGetValue(requestId, out var pending))
        {
            if (templateId == RithmicTemplates.Reject)
                _logger.Warning($"Rithmic {_name}: Reject ohne Request-Bezug: {RithmicProtocolMapping.Describe(GetStringList(message, "rp_code") ?? [])}");
            return;
        }

        var rpCode = GetStringList(message, "rp_code") ?? [];
        if (templateId == RithmicTemplates.Reject)
        {
            pending.Completion.TrySetException(new RithmicProtocolException(
                $"Rithmic Reject: {RithmicProtocolMapping.Describe(rpCode)}", rpCode.ToList()));
            return;
        }

        pending.Responses.Add(message);
        if (rpCode.Count == 0)
            return; // Daten-Antwort, Abschluss folgt noch.

        if (RithmicProtocolMapping.IsSuccess(rpCode) || RithmicProtocolMapping.IsNoData(rpCode))
            pending.Completion.TrySetResult(pending.Responses.ToList());
        else
            pending.Completion.TrySetException(new RithmicProtocolException(
                $"Rithmic-Fehler ({message.Descriptor.Name}): {RithmicProtocolMapping.Describe(rpCode)}", rpCode.ToList()));
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        // Etwas vor Ablauf des vom Server vorgegebenen Intervalls senden.
        var interval = TimeSpan.FromMilliseconds(Math.Max(1000, HeartbeatInterval.TotalMilliseconds * 0.8));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken);
                await SendHeartbeatAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Warning($"Rithmic {_name}-Heartbeat fehlgeschlagen: {ex.Message}");
        }
    }

    private Task SendHeartbeatAsync(CancellationToken cancellationToken) =>
        SendAsync(new RequestHeartbeat { TemplateId = RithmicTemplates.RequestHeartbeat }, cancellationToken);

    private async Task ShutdownAsync(string reason, bool notify)
    {
        var transport = Interlocked.Exchange(ref _transport, null);
        if (transport is null)
            return;

        _loggedIn = false;
        if (notify)
            LastError = reason;

        _loopCts?.Cancel();
        foreach (var pending in _pending.Values)
            pending.Completion.TrySetException(new RithmicProtocolException($"Verbindung beendet: {reason}"));

        await transport.CloseAsync(CancellationToken.None);
        await transport.DisposeAsync();
        _logger.Info($"Rithmic {_name}-Plant getrennt: {reason}");
        if (notify)
            Disconnected?.Invoke(reason);
    }

    internal static int ReadTemplateId(byte[] bytes) => Base.Parser.ParseFrom(bytes).TemplateId;

    private static IList<string>? GetStringList(IMessage message, string fieldName) =>
        message.Descriptor.FindFieldByName(fieldName)?.Accessor.GetValue(message) as IList<string>;

    private sealed class PendingRequest
    {
        public List<IMessage> Responses { get; } = [];
        public TaskCompletionSource<IReadOnlyList<IMessage>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
