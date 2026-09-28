using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.DevDashboard.Services;

/// <summary>Ein Live-Trade fürs Tape, mit fortlaufender Nummer für inkrementelles Abholen.</summary>
public sealed record RithmicTapeTick(
    long Seq,
    DateTimeOffset Time,
    decimal Price,
    decimal Size,
    AggressorSide Aggressor,
    decimal Bid,
    decimal Ask);

/// <summary>
/// Ringpuffer der letzten Live-Trades eines Instruments (threadsicher). Nur echte Rithmic-Trades,
/// Aggressor unverändert aus den Daten.
/// </summary>
public sealed class RithmicTickBuffer
{
    private readonly int _capacity;
    private readonly Queue<RithmicTapeTick> _ticks = new();
    private readonly object _lock = new();
    private long _nextSeq = 1;

    public RithmicTickBuffer(int capacity = 5_000)
    {
        _capacity = capacity;
    }

    public void Add(MarketTick tick)
    {
        lock (_lock)
        {
            _ticks.Enqueue(new RithmicTapeTick(_nextSeq++, tick.Timestamp, tick.Price, tick.Volume, tick.Aggressor, tick.Bid, tick.Ask));
            while (_ticks.Count > _capacity)
                _ticks.Dequeue();
        }
    }

    /// <summary>Alle Ticks mit Seq &gt; <paramref name="since"/>, höchstens die neuesten <paramref name="limit"/>.</summary>
    public RithmicTickPage Get(long since, int limit)
    {
        lock (_lock)
        {
            var newer = _ticks.Where(t => t.Seq > since).ToList();
            var page = newer.Count > limit ? newer.GetRange(newer.Count - limit, limit) : newer;
            return new RithmicTickPage(page, _nextSeq - 1, newer.Count > limit);
        }
    }
}

/// <summary>Ergebnis einer Tick-Abfrage. <c>LastSeq</c> als nächstes <c>since</c> verwenden.</summary>
public sealed record RithmicTickPage(IReadOnlyList<RithmicTapeTick> Ticks, long LastSeq, bool Truncated);
