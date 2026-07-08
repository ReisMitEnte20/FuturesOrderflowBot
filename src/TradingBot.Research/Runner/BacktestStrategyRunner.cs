using TradingBot.Backtesting;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.MarketData;

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
