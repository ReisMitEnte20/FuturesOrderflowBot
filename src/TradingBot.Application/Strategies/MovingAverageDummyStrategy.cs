using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Strategies;

/// <summary>
/// DUMMY-Strategie (Candle-basiert) NUR für Infrastruktur-Tests – KEINE Profit-Strategie.
/// Einfacher SMA-Crossover: schneller Durchschnitt kreuzt langsamen → Long-/Short-Signal.
/// Perioden kommen aus StrategyConfig.Parameters ("FastPeriod", "SlowPeriod") – nichts hardcoded.
/// Deterministisch: gleiche Kerzen → gleiche Signale.
/// </summary>
public sealed class MovingAverageDummyStrategy : IStrategy
{
    private readonly List<decimal> _closes = new();
    private int _fast = 3;
    private int _slow = 5;
    private int? _lastCrossDirection; // +1 fast über slow, -1 darunter

    public string Name => "MovingAverageDummyStrategy";

    public void Initialize(StrategyExecutionContext context)
    {
        var p = context.Config?.Parameters;
        if (p is not null)
        {
            if (p.TryGetValue("FastPeriod", out var f) && int.TryParse(f, out var fast) && fast > 0) _fast = fast;
            if (p.TryGetValue("SlowPeriod", out var s) && int.TryParse(s, out var slow) && slow > 0) _slow = slow;
        }
        if (_fast >= _slow)
            throw new ArgumentException($"FastPeriod ({_fast}) muss kleiner als SlowPeriod ({_slow}) sein.");
    }

    public TradeSignal? OnCandle(Candle candle)
    {
        _closes.Add(candle.Close);
        if (_closes.Count < _slow) return null;

        decimal fastAvg = Average(_fast);
        decimal slowAvg = Average(_slow);
        int direction = fastAvg > slowAvg ? 1 : fastAvg < slowAvg ? -1 : 0;

        if (direction == 0 || direction == _lastCrossDirection) return null;

        bool isFirstReading = _lastCrossDirection is null;
        _lastCrossDirection = direction;
        if (isFirstReading) return null; // erst ein echter Wechsel ist ein Crossover

        return new TradeSignal
        {
            StrategyName = Name,
            Symbol = candle.Symbol,
            Direction = direction > 0 ? SignalDirection.Long : SignalDirection.Short,
            Timestamp = candle.CloseTime,
            ReferencePrice = candle.Close,
            Reason = $"SMA-Crossover (Dummy): fast({_fast})={fastAvg:F2} vs slow({_slow})={slowAvg:F2}"
        };
    }

    public void Reset()
    {
        _closes.Clear();
        _lastCrossDirection = null;
    }

    private decimal Average(int period)
    {
        decimal sum = 0m;
        for (int i = _closes.Count - period; i < _closes.Count; i++) sum += _closes[i];
        return sum / period;
    }
}
