using TradingBot.Infrastructure.MarketData.Import;

namespace TradingBot.DevDashboard.Services;

/// <summary>
/// Intrabar-/Tick-Replay-Session (LOCAL HISTORICAL / SIMULATION ONLY): abgeschlossene Bars +
/// Intrabar-Frames, mit denen sich die aktuelle „forming candle" aus den historischen Ticks aufbaut.
/// Keine Broker-/Execution-Referenz, kein Fake-Orderflow.
/// </summary>
public sealed record IntrabarReplaySession
{
    public required string Symbol { get; init; }
    public int BarMinutes { get; init; }
    public int FrameEveryTicks { get; init; }

    /// <summary>Bereits finalisierte Bars (Index = Bar-Index).</summary>
    public required IReadOnlyList<ReplayBar> CompletedBars { get; init; }

    /// <summary>Intrabar-Momentaufnahmen (gesampelt); der Replay-Index läuft über diese Frames.</summary>
    public required IReadOnlyList<SierraIntrabarFrame> Frames { get; init; }

    /// <summary>Trades aus der Demo-Regel (auf finalisierten Bars); Marker erscheinen bei Bar-Finalisierung.</summary>
    public required IReadOnlyList<ReplayTradeMarker> Trades { get; init; }
    public required IReadOnlyList<decimal> RealizedEquityByBar { get; init; }

    public decimal DollarPerPoint { get; init; }
    public decimal TotalNetPnL { get; init; }

    public long BarsProcessed { get; init; }
    public long ParseErrors { get; init; }
    public decimal NetDelta { get; init; }
    public decimal FinalCumulativeDelta { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public bool DeltaCvdAvailable { get; init; }
    public long ElapsedMs { get; init; }

    public int FrameCount => Frames.Count;
    public int Wins => Trades.Count(t => t.NetPnL > 0m);
    public int Losses => Trades.Count(t => t.NetPnL < 0m);
}
