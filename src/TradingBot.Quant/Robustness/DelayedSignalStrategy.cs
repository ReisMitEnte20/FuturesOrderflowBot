using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Quant.Robustness;

/// <summary>
/// Dekorator, der Signale einer Strategie um eine feste Zahl an Bars VERZÖGERT. Damit lässt sich
/// die Empfindlichkeit gegenüber Ausführungsverzögerung prüfen, OHNE die Backtest-Engine zu ändern:
/// die Engine führt weiterhin am nächsten Bar-Open aus, das Signal erreicht sie nur später.
///
/// Die Strategie selbst bleibt unverändert und sieht weiterhin jede Kerze — verzögert wird
/// ausschließlich die Weitergabe des Signals. Erzeugt niemals Orders (nur <see cref="TradeSignal"/>).
/// </summary>
public sealed class DelayedSignalStrategy : IStrategy
{
    private readonly IStrategy _inner;
    private readonly int _delayBars;
    private readonly Queue<Pending> _pending = new();

    private sealed class Pending
    {
        public int Remaining;
        public required TradeSignal Signal;
    }

    public DelayedSignalStrategy(IStrategy inner, int delayBars)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (delayBars < 0) throw new ArgumentOutOfRangeException(nameof(delayBars), "Verzögerung darf nicht negativ sein.");
        _delayBars = delayBars;
    }

    public string Name => _delayBars == 0 ? _inner.Name : $"{_inner.Name} (+{_delayBars} Bar Verzögerung)";

    public StrategyDataRequirements DataRequirements => _inner.DataRequirements;

    public void Initialize(StrategyExecutionContext context) => _inner.Initialize(context);

    public TradeSignal? OnCandle(Candle candle)
    {
        var fresh = _inner.OnCandle(candle);
        if (_delayBars == 0) return fresh;

        TradeSignal? due = null;
        int count = _pending.Count;
        for (int i = 0; i < count; i++)
        {
            var p = _pending.Dequeue();
            p.Remaining--;
            if (p.Remaining <= 0 && due is null)
                due = p.Signal with
                {
                    Timestamp = candle.CloseTime,
                    DebugNotes = $"Um {_delayBars} Bar(s) verzögert weitergegeben (Ausführungsverzögerungs-Test)."
                };
            else _pending.Enqueue(p);
        }

        if (fresh is not null) _pending.Enqueue(new Pending { Remaining = _delayBars, Signal = fresh });
        return due;
    }

    public TradeSignal? OnTick(MarketTick tick) => _inner.OnTick(tick);

    public TradeSignal? OnOrderFlowBar(OrderFlowBar bar) => _inner.OnOrderFlowBar(bar);

    public void Reset()
    {
        _pending.Clear();
        _inner.Reset();
    }
}
