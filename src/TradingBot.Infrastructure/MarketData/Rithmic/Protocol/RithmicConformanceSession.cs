using TradingBot.Core.Interfaces;

namespace TradingBot.Infrastructure.MarketData.Rithmic.Protocol;

/// <summary>
/// Rithmic-Conformance-Test: Rithmic verlangt, dass sich die App am Order Plant von "Rithmic Test" anmeldet
/// und eingeloggt bleibt, bevor Produktions-Gateways (z. B. für Prop-Firm-Konten) freigegeben werden.
/// Diese Klasse macht GENAU das: System-Info prüfen → Login (Order Plant) → Heartbeats → Logout.
/// Sie sendet keine Order-Messages (diese existieren im Projekt nicht) und akzeptiert nur "Rithmic Test".
/// Die zugrundeliegende Session wird nicht nach außen gegeben.
/// </summary>
public sealed class RithmicConformanceSession : IAsyncDisposable
{
    public const string TestSystemName = "Rithmic Test";

    private readonly RithmicProtocolOptions _options;
    private readonly RithmicTransportFactory _transportFactory;
    private readonly ILogger _logger;
    private RithmicPlantSession? _session;

    public RithmicConformanceSession(
        RithmicProtocolOptions options,
        RithmicTransportFactory? transportFactory = null,
        ILogger? logger = null)
    {
        _options = options;
        _transportFactory = transportFactory ?? (() => new WebSocketRithmicTransport());
        _logger = logger ?? NullLogger.Instance;
    }

    public bool IsRunning => _session?.IsLoggedIn == true;
    public DateTimeOffset? StartedAt { get; private set; }
    public string? User { get; private set; }
    public string? LastError { get; private set; }

    public async Task StartAsync(string user, string password, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("Conformance-Session läuft bereits.");

        var systems = await RithmicPlantSession.GetSystemNamesAsync(_options, _transportFactory, cancellationToken);
        if (!systems.Contains(TestSystemName))
            throw new RithmicProtocolException(
                $"Conformance nur gegen '{TestSystemName}' erlaubt – dieses Gateway bietet: {string.Join(", ", systems)}");

        var session = RithmicPlantSession.CreateConformanceOrderPlant(_options, _transportFactory, _logger);
        session.Disconnected += reason => LastError = reason;
        await session.LoginAsync(new RithmicLoginCredentials(user, password, TestSystemName), cancellationToken);

        _session = session;
        StartedAt = DateTimeOffset.UtcNow;
        User = user;
        LastError = null;
        _logger.Info("Rithmic-Conformance-Session gestartet (Order Plant, nur Login/Heartbeat).");
    }

    public async Task StopAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
            await session.DisposeAsync();
        StartedAt = null;
        User = null;
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
