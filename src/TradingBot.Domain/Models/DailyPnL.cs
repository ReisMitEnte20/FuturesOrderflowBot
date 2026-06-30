namespace TradingBot.Domain.Models;

/// <summary>
/// Tages-PnL-Aggregat pro Konto. Basis für Max-Daily-Loss und Trailing-Drawdown-Prüfung.
/// </summary>
public sealed record DailyPnL
{
    public DateOnly Date { get; init; }
    public required string AccountId { get; init; }

    public decimal RealizedGrossPnL { get; init; }
    public decimal RealizedNetPnL { get; init; }
    public decimal TotalFees { get; init; }

    public int TradeCount { get; init; }
    public int WinCount { get; init; }
    public int LossCount { get; init; }

    /// <summary>Höchster erreichter NetPnL des Tages (für Trailing-Drawdown).</summary>
    public decimal PeakNetPnL { get; init; }
}
