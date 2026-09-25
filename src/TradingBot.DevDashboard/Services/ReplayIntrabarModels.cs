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

    public long BarsProcessed { get; init; }   // Zeilen ab Start eingelesen (durch maxRows begrenzt)
    public long ValidTicks { get; init; }       // davon gültige, im Fenster (>= fromUtc) liegende Ticks
    public long ParseErrors { get; init; }
    public decimal NetDelta { get; init; }
    public decimal FinalCumulativeDelta { get; init; }
    public DateTimeOffset? From { get; init; }        // OpenTime der ersten Bar (Intervall-Beginn)
    public DateTimeOffset? To { get; init; }          // OpenTime der letzten Bar
    public DateTimeOffset? FirstTickTime { get; init; }  // Zeitstempel des ERSTEN tatsächlich eingelesenen Ticks
    public DateTimeOffset? LastTickTime { get; init; }   // Zeitstempel des LETZTEN eingelesenen Ticks
    public bool Truncated { get; init; }              // Einlesen durch maxRows abgeschnitten
    public bool DeltaCvdAvailable { get; init; }
    public long ElapsedMs { get; init; }

    /// <summary>
    /// Erste Bar ist eine TEILBAR: der erste eingelesene Tick liegt nach dem Beginn seines Bar-Intervalls
    /// (z.B. Zeitraumwahl ab 14:19 -&gt; Bar 14:15 enthält nur Ticks ab 14:19, nicht ab 14:15). Sie darf
    /// nicht als vollständige Kerze gewertet werden.
    /// </summary>
    public bool LeadingPartial => FirstTickTime is DateTimeOffset ft && From is DateTimeOffset fb && ft > fb;

    /// <summary>Letzte Bar ist eine TEILBAR, weil das Einlesen durch maxRows abgeschnitten wurde.</summary>
    public bool TrailingPartial => Truncated;

    public int FrameCount => Frames.Count;
    public int Wins => Trades.Count(t => t.NetPnL > 0m);
    public int Losses => Trades.Count(t => t.NetPnL < 0m);
}
