namespace TradingBot.Domain.Models;

/// <summary>
/// Tagesaktueller Risiko-Zustand eines Kontos. Wird vom State-Updater (spätere Phase)
/// fortgeschrieben; der RiskManager LIEST diesen Zustand nur. Unveränderliches Snapshot-Objekt.
/// </summary>
public sealed record DailyRiskState
{
    public DateOnly TradingDate { get; init; }

    public decimal GrossPnL { get; init; }
    public decimal NetPnL { get; init; }
    public decimal RealizedPnL { get; init; }

    /// <summary>Optionaler unrealisierter PnL offener Positionen.</summary>
    public decimal UnrealizedPnL { get; init; }

    public int TradesTaken { get; init; }
    public int WinningTrades { get; init; }
    public int LosingTrades { get; init; }
    public int ConsecutiveLosses { get; init; }

    /// <summary>Größter Drawdown des Tages (positiver Betrag).</summary>
    public decimal MaxDrawdownToday { get; init; }

    /// <summary>Höchster erreichter NetPnL des Tages – Basis für Trailing Drawdown.</summary>
    public decimal PeakNetPnL { get; init; }

    public int OrdersThisMinute { get; init; }

    /// <summary>Vom Updater gesetztes Hard-Flag, dass das Tagesverlustlimit erreicht ist.</summary>
    public bool IsDailyLossLimitHit { get; init; }

    /// <summary>Optionales Flag, dass der Profit Lock ausgelöst wurde.</summary>
    public bool IsProfitLockActive { get; init; }

    /// <summary>Frischer Tageszustand (alles 0) für das angegebene Datum.</summary>
    public static DailyRiskState Start(DateOnly date) => new() { TradingDate = date };
}
