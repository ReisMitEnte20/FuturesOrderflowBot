using TradingBot.Application.Strategies;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Config;
using TradingBot.Infrastructure.MarketData;
using TradingBot.PaperTrading;

namespace TradingBot.DevDashboard.Services;

/// <summary>
/// Verwaltet EINE lokale Demo-Paper-Session für den Monitor. PAPER SIMULATION ONLY:
/// nutzt ausschließlich PaperTradingEngine + PaperExecutionAdapter mit lokaler Sample-CSV –
/// keine Broker-API, keine Live-Execution, keine Netzwerkcalls, keine echten Orders.
///
/// Profile (Instrument/Fee/Broker/Risk) werden READ-ONLY aus config/ geladen (nichts hardcoded);
/// die Strategie ist die deterministische TestSignalStrategy (keine Profit-Strategie).
/// Start/Pause/Resume/Stop wirken NUR auf diese lokale Simulation.
/// </summary>
public sealed class PaperDemoService
{
    private readonly string? _root;
    private readonly object _sync = new();
    private PaperTradingSession? _session;
    private string? _lastError;

    public PaperDemoService(string? repoRoot) => _root = repoRoot;

    /// <summary>Aktuelle (oder letzte) Demo-Session; null wenn noch nie gestartet.</summary>
    public PaperTradingSession? Session
    {
        get { lock (_sync) return _session; }
    }

    /// <summary>Letzter Startfehler (z. B. fehlende Config/CSV), für die Anzeige.</summary>
    public string? LastError
    {
        get { lock (_sync) return _lastError; }
    }

    public PaperTradingSessionState? GetState() => Session?.GetState();

    /// <summary>
    /// Startet die Demo-Session (Replay der lokalen Sample-CSV). Läuft bereits eine Session,
    /// wird sie unverändert zurückgegeben (kein Doppelstart).
    /// <paramref name="replayOptions"/>: Standard RealTime (~60s Demo); Tests nutzen Fast.
    /// </summary>
    public async Task<PaperTradingSession?> StartDemoAsync(ReplayOptions? replayOptions = null)
    {
        lock (_sync)
        {
            if (_session is { IsRunning: true } || _session?.Status == PaperTradingRunStatus.Paused)
                return _session;
            _lastError = null;
        }

        try
        {
            if (_root is null)
                throw new InvalidOperationException("Repo-Wurzel (TradingBot.sln) nicht gefunden.");

            string csvPath = Path.Combine(_root, "samples", "marketdata", "demo-paper-session.csv");
            if (!File.Exists(csvPath))
                throw new FileNotFoundException($"Demo-CSV nicht gefunden: {csvPath}");

            // Profile READ-ONLY aus config/ laden – keine hardcoded Fees/TickValues.
            var cfg = new JsonConfigService();
            var instrument = await new InstrumentProfileProvider(cfg, Path.Combine(_root, "config", "instruments"))
                .GetAsync("NQ");
            var broker = (await new BrokerProfileProvider(cfg, Path.Combine(_root, "config", "brokers"))
                .GetAllAsync()).FirstOrDefault();
            FeeProfile? fee = null;
            if (broker is not null)
                fee = await new FeeProfileProvider(cfg, Path.Combine(_root, "config", "fees"))
                    .GetAsync(broker.BrokerName, broker.ExecutionProvider, "NQ");
            var riskFiles = Directory.GetFiles(Path.Combine(_root, "config", "risk"), "*.json");
            var risk = riskFiles.Length > 0 ? await cfg.LoadAsync<RiskConfig>(riskFiles[0]) : null;

            var request = new PaperTradingRequest
            {
                MarketData = new CsvMarketDataProvider(csvPath, replayOptions ?? ReplayOptions.Realtime),
                Symbol = "NQ",
                // Deterministische Dummy-Strategie – NUR Infrastruktur-Demo, keine Profit-Strategie.
                Strategy = new TestSignalStrategy(intervalTicks: 4, alternate: true, quantity: 1, stopLossTicks: 40),
                Account = new TradingAccount { AccountId = "PAPER-DEMO", BrokerName = broker?.BrokerName ?? "n/a" },
                Instrument = instrument,
                Fee = fee,
                Broker = broker,
                Risk = risk
            };

            var session = new PaperTradingEngine().Start(request);
            lock (_sync) _session = session;
            return session;
        }
        catch (Exception ex)
        {
            lock (_sync) _lastError = ex.Message;
            return null;
        }
    }

    public void Pause() => Session?.Pause();
    public void Resume() => Session?.Resume();

    public async Task StopAsync()
    {
        var session = Session;
        if (session is not null && session.Status is PaperTradingRunStatus.Running or PaperTradingRunStatus.Paused)
            await session.StopAsync();
    }
}
