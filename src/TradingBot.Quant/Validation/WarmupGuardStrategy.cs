using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Quant.Validation;

/// <summary>
/// Dekorator, der die ausgewiesene Warmup-Regel eines Walk-forward-Abschnitts TATSÄCHLICH durchsetzt,
/// ohne die Backtest-Engine zu ändern:
///
/// - Jede Kerze wird an die innere Strategie weitergereicht, damit sich deren Indikatoren
///   ausschließlich mit den bis zu diesem Zeitpunkt verfügbaren Daten vorbereiten (kein Look-ahead).
/// - Während der ersten <see cref="_warmupBars"/> Kerzen seit dem letzten <see cref="Reset"/> wird
///   jedoch KEIN Signal weitergegeben — in dieser Zeit findet keine Handelsausführung statt.
///
/// Da jeder Abschnitt mit einer frisch zurückgesetzten Strategie läuft, ist der Warmup je Abschnitt
/// gleich lang und der eigentliche Bewertungszeitraum zwischen den Kandidaten vergleichbar. Der
/// Dekorator erzeugt niemals Orders (nur <see cref="TradeSignal"/>).
/// </summary>
public sealed class WarmupGuardStrategy : IStrategy
{
    private readonly IStrategy _inner;
    private readonly int _warmupBars;
    private int _seen;

    public WarmupGuardStrategy(IStrategy inner, int warmupBars)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (warmupBars < 0) throw new ArgumentOutOfRangeException(nameof(warmupBars), "Warmup darf nicht negativ sein.");
        _warmupBars = warmupBars;
    }

    public string Name => _warmupBars == 0 ? _inner.Name : $"{_inner.Name} (Warmup {_warmupBars} Bars)";

    public StrategyDataRequirements DataRequirements => _inner.DataRequirements;

    public void Initialize(StrategyExecutionContext context) => _inner.Initialize(context);

    public TradeSignal? OnCandle(Candle candle)
    {
        // Indikatoren dürfen sich mit den bis hier verfügbaren Daten vorbereiten ...
        var signal = _inner.OnCandle(candle);
        _seen++;
        // ... aber während des ausgewiesenen Warmups wird kein Signal gewertet.
        return _seen <= _warmupBars ? null : signal;
    }

    // Die OHLC-Engine wertet ausschließlich OnCandle aus; Tick/OrderFlow werden unverändert
    // weitergereicht (der Warmup greift dort nicht, weil sie in diesem Pfad nicht zur Ausführung führen).
    public TradeSignal? OnTick(MarketTick tick) => _inner.OnTick(tick);

    public TradeSignal? OnOrderFlowBar(OrderFlowBar bar) => _inner.OnOrderFlowBar(bar);

    public void Reset()
    {
        _seen = 0;
        _inner.Reset();
    }
}
