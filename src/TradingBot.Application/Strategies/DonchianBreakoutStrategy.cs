using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Strategies;

/// <summary>
/// REFERENZ-Strategie (rein OHLC, deterministisch) — KEINE Edge-/Profit-Behauptung. Dient ausschließlich dazu,
/// den Mehrstrategie-Vergleich mit einer ZWEITEN, andersartigen Strategie-Familie funktional abnehmen zu können
/// (die einzige weitere Strategie ist der SMA-Crossover).
///
/// <para><b>Regel (Donchian-Kanal-Ausbruch):</b> Betrachtet werden ausschließlich die <c>Channel</c> zuletzt
/// ABGESCHLOSSENEN Bars VOR der aktuellen Bar. Deren höchstes High bildet die obere Kanalgrenze, deren tiefstes
/// Low die untere. Schließt die aktuelle Bar über der oberen Grenze → Long-Ausbruch; schließt sie unter der
/// unteren Grenze → Short-Ausbruch. Ein Signal wird nur bei einem WECHSEL der Ausbruchsrichtung erzeugt
/// (analog zum SMA-Crossover), nicht bei jeder Bar innerhalb desselben Ausbruchs. Die aktuelle Bar geht nur mit
/// ihrem Close in die Entscheidung ein und erst NACH der Entscheidung mit High/Low in den Kanal — kein Lookahead.</para>
///
/// <para>Deterministisch: gleiche Kerzen → gleiche Signale. Parameter <c>Channel</c> (Bars) kommt aus
/// <see cref="StrategyConfig.Parameters"/>; nichts ist hartkodiert.</para>
/// </summary>
public sealed class DonchianBreakoutStrategy : IStrategy
{
    private readonly List<decimal> _highs = new();
    private readonly List<decimal> _lows = new();
    private int _channel = 20;
    private int? _lastBreakoutDirection; // +1 = zuletzt Ausbruch nach oben, -1 = nach unten

    public string Name => "DonchianBreakoutStrategy";

    public void Initialize(StrategyExecutionContext context)
    {
        var p = context.Config?.Parameters;
        if (p is not null && p.TryGetValue("Channel", out var c) && int.TryParse(c, out var ch) && ch > 0)
            _channel = ch;
        if (_channel < 1)
            throw new ArgumentException($"Channel ({_channel}) muss mindestens 1 Bar betragen.");
    }

    public TradeSignal? OnCandle(Candle candle)
    {
        // Kanal aus den 'Channel' Bars VOR der aktuellen Bar (die aktuelle Bar ist noch nicht im Fenster).
        TradeSignal? signal = null;
        if (_highs.Count >= _channel)
        {
            decimal upper = Max(_highs, _channel);
            decimal lower = Min(_lows, _channel);

            int direction = candle.Close > upper ? 1 : candle.Close < lower ? -1 : 0;
            // Signal bei einem echten Ausbruch, sobald sich die Richtung gegenüber dem letzten Ausbruch ändert
            // (auch beim ERSTEN Ausbruch nach dem Warmup). Bleibt der Kurs im Kanal (direction == 0), ändert sich
            // nichts.
            if (direction != 0 && direction != _lastBreakoutDirection)
            {
                _lastBreakoutDirection = direction;
                signal = new TradeSignal
                {
                    StrategyName = Name,
                    Symbol = candle.Symbol,
                    Direction = direction > 0 ? SignalDirection.Long : SignalDirection.Short,
                    Timestamp = candle.CloseTime,
                    ReferencePrice = candle.Close,
                    Reason = $"Donchian-Ausbruch (Referenz): Close {candle.Close:F2} " +
                             (direction > 0 ? $"> oberes Kanal-High {upper:F2}" : $"< unteres Kanal-Low {lower:F2}") +
                             $" über {_channel} Bars"
                };
            }
        }

        // Aktuelle Bar erst JETZT in den Kanal aufnehmen (kein Lookahead auf die eigene Bar).
        _highs.Add(candle.High);
        _lows.Add(candle.Low);
        return signal;
    }

    public void Reset()
    {
        _highs.Clear();
        _lows.Clear();
        _lastBreakoutDirection = null;
    }

    private static decimal Max(List<decimal> xs, int period)
    {
        decimal m = decimal.MinValue;
        for (int i = xs.Count - period; i < xs.Count; i++) if (xs[i] > m) m = xs[i];
        return m;
    }

    private static decimal Min(List<decimal> xs, int period)
    {
        decimal m = decimal.MaxValue;
        for (int i = xs.Count - period; i < xs.Count; i++) if (xs[i] < m) m = xs[i];
        return m;
    }
}
