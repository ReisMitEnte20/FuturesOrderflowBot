using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Strategies;

/// <summary>
/// Führt registrierte, AKTIVE Strategien deterministisch (Registrierungs-Reihenfolge) auf
/// Marktdaten-Events aus und sammelt deren Signale.
///
/// Garantien:
/// - Deaktivierte Strategien werden NICHT aufgerufen (Framework-Garantie, keine Disziplinfrage).
/// - Symbol- und RequiredDataType-Routing gemäß StrategyConfig.
/// - Orderflow-Bars ohne echte Bid/Ask-Klassifikation werden fail-closed verworfen (keine Fake-Signale).
/// - MaxSignalsPerSession wird durchgesetzt; überzählige Signale werden verworfen (mit Grund).
/// - Fehlende Vorschlagswerte am Signal (Menge/Stop/TP) werden aus der StrategyConfig ergänzt.
/// - KEINE Order-/Broker-/Risk-Referenz: Output sind ausschließlich TradeSignal-Objekte.
/// </summary>
public sealed class StrategyEngine : IStrategyEngine
{
    private readonly IStrategyRegistry _registry;
    private readonly object _sync = new();
    private readonly List<TradeSignal> _collected = new();
    private readonly Dictionary<string, Counter> _counters = new(StringComparer.OrdinalIgnoreCase);

    public StrategyEngine(IStrategyRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public IReadOnlyList<TradeSignal> CollectedSignals
    {
        get { lock (_sync) return _collected.ToList(); }
    }

    public IReadOnlyList<StrategyRuntimeState> States
    {
        get
        {
            lock (_sync)
                return _registry.All.Select(d =>
                {
                    var c = _counters.GetValueOrDefault(d.Name);
                    return new StrategyRuntimeState
                    {
                        Name = d.Name,
                        Enabled = _registry.IsEnabled(d.Name),
                        SignalsGenerated = c?.Signals ?? 0,
                        LastSignalAt = c?.LastSignalAt
                    };
                }).ToList();
        }
    }

    public void Initialize(StrategyExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var d in _registry.All)
            d.Strategy.Initialize(context with { Config = d.Config });
    }

    public IReadOnlyList<StrategyEvaluationResult> OnTick(MarketTick tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        return Dispatch(StrategyDataType.Tick, tick.Symbol, tick.Timestamp, s => s.OnTick(tick));
    }

    public IReadOnlyList<StrategyEvaluationResult> OnCandle(Candle candle)
    {
        ArgumentNullException.ThrowIfNull(candle);
        return Dispatch(StrategyDataType.Candle, candle.Symbol, candle.CloseTime, s => s.OnCandle(candle));
    }

    public IReadOnlyList<StrategyEvaluationResult> OnOrderFlowBar(OrderFlowBar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);

        // Fail-closed: Bar mit Volumen, aber ohne echte Bid/Ask-Klassifikation -> NICHT verteilen.
        if (bar.TotalVolume > 0m && bar.BidVolume + bar.AskVolume <= 0m)
        {
            return _registry.All
                .Where(d => _registry.IsEnabled(d.Name)
                    && d.Config.RequiredDataType == StrategyDataType.OrderFlow
                    && SymbolMatches(d, bar.Symbol))
                .Select(d => StrategyEvaluationResult.NoSignal(
                    d.Name, "OrderFlowBar ohne echte Bid/Ask-Klassifikation – verworfen (keine Fake-Daten)."))
                .ToList();
        }

        return Dispatch(StrategyDataType.OrderFlow, bar.Symbol, bar.CloseTime, s => s.OnOrderFlowBar(bar));
    }

    public void Reset()
    {
        lock (_sync)
        {
            _collected.Clear();
            _counters.Clear();
        }
        foreach (var d in _registry.All)
            d.Strategy.Reset();
    }

    // ---- intern --------------------------------------------------------------

    private IReadOnlyList<StrategyEvaluationResult> Dispatch(
        StrategyDataType dataType, string symbol, DateTimeOffset eventTime, Func<IStrategy, TradeSignal?> invoke)
    {
        var results = new List<StrategyEvaluationResult>();

        foreach (var d in _registry.All)
        {
            if (!_registry.IsEnabled(d.Name)) continue;                 // deaktiviert -> gar nicht aufrufen
            if (d.Config.RequiredDataType != dataType) continue;        // falscher Datentyp
            if (!SymbolMatches(d, symbol)) continue;                    // falsches Symbol

            var signal = invoke(d.Strategy);
            if (signal is null)
            {
                results.Add(StrategyEvaluationResult.NoSignal(d.Name));
                continue;
            }

            lock (_sync)
            {
                var counter = _counters.TryGetValue(d.Name, out var c) ? c : (_counters[d.Name] = new Counter());

                if (d.Config.MaxSignalsPerSession is int max && counter.Signals >= max)
                {
                    results.Add(StrategyEvaluationResult.NoSignal(
                        d.Name, $"MaxSignalsPerSession ({max}) erreicht – Signal verworfen."));
                    continue;
                }

                var enriched = Enrich(signal, d.Config);
                counter.Signals++;
                counter.LastSignalAt = eventTime;
                _collected.Add(enriched);
                results.Add(StrategyEvaluationResult.WithSignal(d.Name, enriched));
            }
        }

        return results;
    }

    /// <summary>Ergänzt fehlende Vorschlagswerte aus der Config (Config = Default, Strategie hat Vorrang).</summary>
    private static TradeSignal Enrich(TradeSignal signal, StrategyConfig config) => signal with
    {
        SuggestedQuantity = signal.SuggestedQuantity ?? config.SuggestedContracts,
        SuggestedStopLossTicks = signal.SuggestedStopLossTicks ?? config.StopLossTicks,
        SuggestedTakeProfitTicks = signal.SuggestedTakeProfitTicks ?? config.TakeProfitTicks
    };

    private static bool SymbolMatches(StrategyDescriptor d, string symbol)
        => string.Equals(d.Config.Symbol, symbol, StringComparison.OrdinalIgnoreCase);

    private sealed class Counter
    {
        public int Signals;
        public DateTimeOffset? LastSignalAt;
    }
}
