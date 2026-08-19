using TradingBot.Backtesting;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.MarketData;
using TradingBot.Infrastructure.MarketData.Import;

namespace TradingBot.Research.Runner;

/// <summary>
/// Produktive Implementierung: baut aus den Inputs einen frischen Replay-Feed + eine frisch
/// initialisierte Strategie und führt die BESTEHENDE <see cref="BacktestEngine"/> aus.
/// Keine Broker-/Live-Referenz. Deterministisch (AsFastAsPossible, tick-gesteuerte Uhr).
/// </summary>
public sealed class BacktestStrategyRunner : IStrategyBacktestRunner
{
    private readonly IBacktestEngine _engine;

    public BacktestStrategyRunner(IBacktestEngine? engine = null) => _engine = engine ?? new BacktestEngine();

    /// <summary>
    /// Erzeugt <see cref="StrategyRunInputs"/> aus einer lokalen Sierra-CSV-Datei (Time-Bars oder
    /// Range-Bars egal — hier werden **rohe Ticks** gestreamt und direkt in den Replay-Provider
    /// eingespeist). So fliesst echte Sierra-OrderFlow-Daten in die Backtest-/Research-Pipeline
    /// ohne Architektur-Bruch. Liefert auch die Capabilities/Quality-Flags für Robustheits-Prüfung.
    /// </summary>
    public static StrategyRunInputs CreateFromSierraFile(
        string sierraPath, string symbol, StrategyCandidate candidate, StrategyConfig config,
        InstrumentProfile instrument, FeeProfile fee, BrokerProfile broker,
        RiskConfig risk, TradingAccount account, decimal? slippageOverride = null,
        long? maxRows = null, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null)
    {
        var adapter = new SierraMarketDataAdapter();
        var ticks = adapter.StreamTicksFromFile(sierraPath, symbol, maxRows, fromUtc, toUtc);
        var agg = adapter.LoadFromFile(sierraPath, symbol, TimeSpan.FromMinutes(1), maxRows, fromUtc, toUtc);
        
        return new StrategyRunInputs
        {
            Candidate = candidate,
            Config = config,
            Ticks = ticks,
            Symbol = symbol,
            Instrument = instrument,
            Fee = fee,
            Broker = broker,
            Risk = risk,
            Account = account,
            SlippageTicksOverride = slippageOverride,
            DataQualityOk = agg.Aggregation.Capabilities.SupportsDeltaCvd,
            CapabilitiesSufficient = agg.Aggregation.Capabilities.SupportsDeltaCvd
        };
    }

    public async Task<StrategyRunResult> RunAsync(StrategyRunInputs inputs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        // Frische Strategie-Instanz, mit der effektiven Config initialisiert.
        var strategy = inputs.Candidate.Build(inputs.Config);
        strategy.Initialize(new StrategyExecutionContext
        {
            Symbol = inputs.Symbol,
            Instrument = inputs.Instrument,
            Config = inputs.Config
        });

        var request = new BacktestRequest
        {
            MarketData = new ReplayMarketDataProvider(inputs.Ticks, ReplayOptions.Fast),
            Symbol = inputs.Symbol,
            Strategy = strategy,
            Instrument = inputs.Instrument,
            Fee = inputs.Fee,
            Broker = inputs.Broker,
            Risk = inputs.Risk,
            Account = inputs.Account,
            Config = new BacktestConfiguration { SlippageTicksOverride = inputs.SlippageTicksOverride }
        };

        var result = await _engine.RunAsync(request, cancellationToken).ConfigureAwait(false);

        return new StrategyRunResult
        {
            StrategyName = inputs.Candidate.Name,
            Config = inputs.Config,
            Statistics = result.Statistics,
            Trades = result.Trades,
            Metrics = ResearchMetricSet.FromBacktest(
                result.Statistics, inputs.DataQualityOk, inputs.CapabilitiesSufficient)
        };
    }
}
