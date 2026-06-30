using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>
/// Berechnet Gross- und NetPnL für Futures-Trades. Tick-Werte stammen aus dem
/// InstrumentProfile, Gebühren aus dem FeeProfile – nichts ist hardcoded.
/// Long und Short werden korrekt mit Vorzeichen behandelt.
/// </summary>
public interface IPnLCalculator
{
    /// <summary>
    /// GrossPnL = (Preisbewegung in P/L-Richtung / TickSize) × TickValue × Kontrakte.
    /// </summary>
    decimal CalculateGrossPnL(
        InstrumentProfile instrument, PositionSide side,
        decimal entryPrice, decimal exitPrice, int quantity);

    /// <summary>
    /// Vollständiges Ergebnis: GrossPnL, Gebührenaufstellung (Round Turn + Slippage)
    /// und NetPnL. NetPnL = GrossPnL - TotalFees.
    /// </summary>
    PnLResult CalculateTrade(
        InstrumentProfile instrument, FeeProfile feeProfile, PositionSide side,
        decimal entryPrice, decimal exitPrice, int quantity);
}
