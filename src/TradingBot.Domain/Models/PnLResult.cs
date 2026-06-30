namespace TradingBot.Domain.Models;

/// <summary>
/// Ergebnis einer PnL-Berechnung für einen geschlossenen Trade.
/// GrossPnL und NetPnL sind strikt getrennt; NetPnL ergibt sich immer als
/// GrossPnL minus aller Gebühren (inkl. Slippage). Reines Datenobjekt.
/// </summary>
public sealed record PnLResult
{
    /// <summary>Gewinn/Verlust vor Gebühren.</summary>
    public decimal GrossPnL { get; init; }

    /// <summary>Aufgeschlüsselte Gebühren inkl. Slippage.</summary>
    public FeeBreakdown Fees { get; init; } = FeeBreakdown.Zero;

    /// <summary>Vorzeichenbehaftete Anzahl bewegter Ticks (in P/L-Richtung).</summary>
    public decimal Ticks { get; init; }

    /// <summary>Gewinn/Verlust nach allen Gebühren. Einfache berechnete Eigenschaft.</summary>
    public decimal NetPnL => GrossPnL - Fees.TotalFees;
}
