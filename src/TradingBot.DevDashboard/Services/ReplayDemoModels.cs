using TradingBot.Domain.Enums;

namespace TradingBot.DevDashboard.Services;

/// <summary>Eine Bar der Replay-Demo (read-only, deterministisch erzeugt).</summary>
public sealed record ReplayBar
{
    public int Index { get; init; }
    public DateTimeOffset Time { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public decimal Volume { get; init; }
    public decimal Delta { get; init; }
    public bool IsBullish => Close >= Open;
}

/// <summary>Ein abgeschlossener Demo-Trade als Replay-Marker (kein echter Order-/Broker-Bezug).</summary>
public sealed record ReplayTradeMarker
{
    public int Id { get; init; }
    public PositionSide Side { get; init; }        // Long / Short
    public int EntryIndex { get; init; }
    public int ExitIndex { get; init; }
    public DateTimeOffset EntryTime { get; init; }
    public DateTimeOffset ExitTime { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal ExitPrice { get; init; }
    public decimal StopLoss { get; init; }
    public decimal TakeProfit { get; init; }
    public decimal NetPnL { get; init; }

    public bool IsOpenAt(int index) => index >= EntryIndex && index < ExitIndex;
    public bool IsClosedAt(int index) => index >= ExitIndex;
}

/// <summary>
/// Vollständige Replay-Demo-Session: Bars + Trade-Marker + realisierte Equity je Bar.
/// RESEARCH / SIMULATION ONLY – künstliche, deterministische Daten, keine echte Performance.
/// </summary>
public sealed record ReplaySession
{
    public required string Symbol { get; init; }
    public required IReadOnlyList<ReplayBar> Bars { get; init; }
    public required IReadOnlyList<ReplayTradeMarker> Trades { get; init; }

    /// <summary>Realisierter kumulierter NetPnL bis einschließlich Bar-Index i.</summary>
    public required IReadOnlyList<decimal> RealizedEquityByBar { get; init; }

    public decimal DollarPerPoint { get; init; }
    public decimal TotalNetPnL { get; init; }

    public int BarCount => Bars.Count;
}
