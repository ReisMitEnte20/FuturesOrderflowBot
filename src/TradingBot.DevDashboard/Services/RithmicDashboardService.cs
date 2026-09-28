using TradingBot.Infrastructure.MarketData.Rithmic.Protocol;

namespace TradingBot.DevDashboard.Services;

/// <summary>
/// Rithmic-Marktdaten fürs Dashboard über R|Protocol (Ticker + History Plant). NUR Marktdaten:
/// kein Order-/PnL-Plant, keine Orders. Netzwerk nur bei explizitem Connect.
/// Konfiguration (Abschnitt "Rithmic"): AppName, AppVersion, Gateways{Name → wss-URI}.
/// Zugangsdaten werden nicht gespeichert – das Passwort geht nur in den Login-Request.
/// </summary>
public sealed class RithmicDashboardService : IAsyncDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<RithmicDashboardService>? _logger;
    private readonly RithmicTransportFactory? _transportFactory;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private RithmicMarketDataClient? _client;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RithmicTickBuffer> _tickBuffers = new();
    private RithmicConformanceSession? _conformance;
    private string? _conformanceGateway;
    private string? _conformanceError;
    private string? _gatewayUrl;
    private string? _lastError;

    public RithmicDashboardService(
        IConfiguration configuration,
        ILogger<RithmicDashboardService>? logger = null,
        RithmicTransportFactory? transportFactory = null)
    {
        _configuration = configuration;
        _logger = logger;
        _transportFactory = transportFactory;
    }

    public bool IsConnected => _client?.IsConnected == true;

    public IReadOnlyDictionary<string, string> Gateways =>
        _configuration.GetSection("Rithmic:Gateways").GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .ToDictionary(c => c.Key, c => c.Value!);

    public RithmicOptionsResult GetOptions() => new(
        Gateways.Keys.ToList(),
        !string.IsNullOrWhiteSpace(_configuration["Rithmic:AppName"]));

    public async Task<RithmicConnectResult> ConnectAsync(RithmicConnectRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrEmpty(request.Password) || string.IsNullOrWhiteSpace(request.System))
            return Fail("User ID, Passwort und System sind erforderlich.", null);

        if (!TryBuildOptions(request.Gateway, out var options, out var gatewayUrl, out var error))
            return Fail(error!, gatewayUrl);

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
            {
                // Idempotent: gleiche Verbindung erneut angefragt (z. B. UI nach Reload) -> Erfolg melden.
                if (_client!.User == request.UserId.Trim() && _client.SystemName == request.System && _gatewayUrl == gatewayUrl)
                    return new RithmicConnectResult(true, "Bereits verbunden.", gatewayUrl);
                // Anderes Konto/Gateway: ablehnen, ohne den Status der bestehenden Verbindung zu verfälschen.
                return new RithmicConnectResult(false, $"Bereits verbunden als {_client.User} ({_client.SystemName}) – zuerst Disconnect.", _gatewayUrl);
            }

            if (_client is not null)
                await _client.DisposeAsync();

            _client = new RithmicMarketDataClient(options!, _transportFactory, new LoggerAdapter(_logger));
            _client.TradeReceived += OnTradeReceived;
            _gatewayUrl = gatewayUrl;

            await _client.ConnectAsync(new RithmicLoginCredentials(request.UserId.Trim(), request.Password, request.System), true, cancellationToken);
            _lastError = null;
            _logger?.LogInformation("Rithmic verbunden: {System} über {Gateway}", request.System, request.Gateway);
            var message = _client.HasHistory ? "Verbunden (Ticker + History)." : "Verbunden (nur Ticker – History-Plant nicht verfügbar).";
            return new RithmicConnectResult(true, message, gatewayUrl);
        }
        catch (Exception ex) when (ex is RithmicProtocolException or OperationCanceledException
                                       or System.Net.WebSockets.WebSocketException or System.Net.Http.HttpRequestException)
        {
            if (_client is not null)
                await _client.DisposeAsync();
            _client = null;
            _logger?.LogWarning("Rithmic-Connect fehlgeschlagen: {Error}", ex.Message);
            return Fail(ex is OperationCanceledException ? "Timeout beim Verbindungsaufbau." : ex.Message, gatewayUrl);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<RithmicDisconnectResult> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
        {
            client.TradeReceived -= OnTradeReceived;
            await client.DisposeAsync();
        }
        _tickBuffers.Clear();
        _gatewayUrl = null;
        _lastError = null;
        return new RithmicDisconnectResult(true, "Disconnected");
    }

    public RithmicStatusResult GetStatus() => new(
        IsConnected,
        IsConnected ? _client!.User : null,
        IsConnected ? _gatewayUrl : null,
        _client?.LastError ?? (IsConnected ? null : _lastError),
        IsConnected ? _client!.SystemName : null,
        _client?.HasHistory == true);

    public Task SubscribeAsync(string symbol, string exchange, CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        // Puffer VOR dem Abo anlegen, damit der erste Live-Trade nicht verloren geht.
        _tickBuffers.GetOrAdd(TickKey(symbol, exchange), _ => new RithmicTickBuffer());
        return client.SubscribeAsync(symbol, exchange, cancellationToken);
    }

    /// <summary>Live-Trades eines abonnierten Instruments seit <paramref name="since"/> (Seq).</summary>
    public RithmicTickPage GetTicks(string symbol, string exchange, long since, int limit)
    {
        RequireClient();
        if (!_tickBuffers.TryGetValue(TickKey(symbol, exchange), out var buffer))
            throw new InvalidOperationException($"{symbol} ({exchange}) ist nicht abonniert – zuerst /subscribe.");
        return buffer.Get(since, Math.Clamp(limit, 1, 5_000));
    }

    private void OnTradeReceived(string symbol, string exchange, TradingBot.Domain.Models.MarketTick tick)
    {
        if (_tickBuffers.TryGetValue(TickKey(symbol, exchange), out var buffer))
            buffer.Add(tick);
    }

    private static string TickKey(string symbol, string exchange) => $"{exchange}:{symbol}";

    public IReadOnlyCollection<RithmicQuote> GetQuotes() => _client?.Quotes ?? [];

    public Task<IReadOnlyList<RithmicTimeBar>> GetMinuteBarsAsync(
        string symbol, string exchange, DateTimeOffset from, DateTimeOffset to, int periodMinutes, CancellationToken cancellationToken = default) =>
        RequireClient().GetMinuteBarsAsync(symbol, exchange, from, to, periodMinutes, cancellationToken);

    public Task<string?> GetFrontMonthAsync(string rootSymbol, string exchange, CancellationToken cancellationToken = default) =>
        RequireClient().GetFrontMonthAsync(rootSymbol, exchange, cancellationToken);

    /// <summary>
    /// Startet die Conformance-Session (Order Plant von "Rithmic Test", nur Login/Heartbeat, keine Orders).
    /// Rithmic gibt Produktions-Gateways (für Prop-Firm-Konten) erst nach diesem Test frei.
    /// </summary>
    public async Task<RithmicConnectResult> StartConformanceAsync(RithmicConformanceRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrEmpty(request.Password))
            return FailConformance("User ID und Passwort (Rithmic-Test-Zugang) sind erforderlich.", null);
        if (!TryBuildOptions(request.Gateway, out var options, out var gatewayUrl, out var error))
            return FailConformance(error!, gatewayUrl);

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_conformance?.IsRunning == true)
                return FailConformance("Conformance-Session läuft bereits.", _conformanceGateway);

            var session = new RithmicConformanceSession(options!, _transportFactory, new LoggerAdapter(_logger));
            await session.StartAsync(request.UserId.Trim(), request.Password, cancellationToken);
            _conformance = session;
            _conformanceGateway = gatewayUrl;
            _conformanceError = null;
            _logger?.LogInformation("Rithmic-Conformance gestartet über {Gateway}", request.Gateway);
            return new RithmicConnectResult(true, "Conformance-Session läuft (Order Plant 'Rithmic Test', nur Heartbeat). App laufen lassen, bis Rithmic bestätigt.", gatewayUrl);
        }
        catch (Exception ex) when (ex is RithmicProtocolException or OperationCanceledException
                                       or System.Net.WebSockets.WebSocketException or System.Net.Http.HttpRequestException)
        {
            _logger?.LogWarning("Rithmic-Conformance fehlgeschlagen: {Error}", ex.Message);
            return FailConformance(ex is OperationCanceledException ? "Timeout beim Verbindungsaufbau." : ex.Message, gatewayUrl);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<RithmicDisconnectResult> StopConformanceAsync()
    {
        var session = Interlocked.Exchange(ref _conformance, null);
        if (session is not null)
            await session.DisposeAsync();
        _conformanceGateway = null;
        _conformanceError = null;
        return new RithmicDisconnectResult(true, "Conformance-Session beendet.");
    }

    public RithmicConformanceStatus GetConformanceStatus() => new(
        _conformance?.IsRunning == true,
        _conformance?.User,
        _conformance?.IsRunning == true ? _conformanceGateway : null,
        _conformance?.StartedAt,
        _conformance?.LastError ?? _conformanceError);

    public async ValueTask DisposeAsync()
    {
        await StopConformanceAsync();
        await DisconnectAsync();
        _connectLock.Dispose();
    }

    private RithmicMarketDataClient RequireClient() =>
        _client is { IsConnected: true } c ? c : throw new InvalidOperationException("Rithmic ist nicht verbunden.");

    private bool TryBuildOptions(string? gatewayName, out RithmicProtocolOptions? options, out string? gatewayUrl, out string? error)
    {
        options = null;
        error = null;
        if (!Gateways.TryGetValue(gatewayName ?? "", out gatewayUrl) || !Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var gatewayUri))
        {
            gatewayUrl = null;
            error = $"Gateway '{gatewayName}' ist nicht konfiguriert. Verfügbar: {string.Join(", ", Gateways.Keys)}";
            return false;
        }

        var appName = _configuration["Rithmic:AppName"];
        var appVersion = _configuration["Rithmic:AppVersion"];
        if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(appVersion))
        {
            error = "Rithmic:AppName/AppVersion fehlen in der Konfiguration.";
            return false;
        }

        options = new RithmicProtocolOptions { GatewayUri = gatewayUri, AppName = appName, AppVersion = appVersion };
        return true;
    }

    private RithmicConnectResult FailConformance(string message, string? gatewayUrl)
    {
        _conformanceError = message;
        return new RithmicConnectResult(false, message, gatewayUrl);
    }

    private RithmicConnectResult Fail(string message, string? gatewayUrl)
    {
        _lastError = message;
        return new RithmicConnectResult(false, message, gatewayUrl);
    }

    /// <summary>Brücke vom Core-Logger des Clients auf den ASP.NET-Logger.</summary>
    private sealed class LoggerAdapter(ILogger? logger) : TradingBot.Core.Interfaces.ILogger
    {
        public void Info(string message) => logger?.LogInformation("{Message}", message);
        public void Warning(string message) => logger?.LogWarning("{Message}", message);
        public void Error(string message, Exception? exception = null) => logger?.LogError(exception, "{Message}", message);
    }
}

public record RithmicConnectRequest(
    string UserId,
    string Password,
    string System,
    string Gateway
)
{
    // Passwort nie in Logs/ToString ausgeben.
    public override string ToString() => $"RithmicConnectRequest {{ UserId = {UserId}, System = {System}, Gateway = {Gateway} }}";
}

public record RithmicConnectResult(
    bool Success,
    string Message,
    string? GatewayUrl
);

public record RithmicDisconnectResult(
    bool Success,
    string Message
);

public record RithmicStatusResult(
    bool IsConnected,
    string? Username,
    string? GatewayUrl,
    string? LastError,
    string? SystemName,
    bool HasHistory
);

public record RithmicOptionsResult(
    IReadOnlyList<string> Gateways,
    bool AppConfigured
);

public record RithmicSubscribeRequest(string Symbol, string Exchange);

public record RithmicConformanceRequest(string UserId, string Password, string Gateway)
{
    public override string ToString() => $"RithmicConformanceRequest {{ UserId = {UserId}, Gateway = {Gateway} }}";
}

public record RithmicConformanceStatus(
    bool IsRunning,
    string? Username,
    string? GatewayUrl,
    DateTimeOffset? StartedAt,
    string? LastError
);
