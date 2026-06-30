using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>
/// Berechnet Gebühren aus einem FeeProfile. Reine Berechnung, kein IO – daher synchron.
/// Es werden KEINE Brokerwerte hardcoded; alle Werte stammen aus dem übergebenen
/// <see cref="FeeProfile"/> bzw. <see cref="InstrumentProfile"/>.
/// Konvention: Alle *PerSide-Werte sind pro Kontrakt pro Seite.
/// </summary>
public interface IFeeCalculator
{
    /// <summary>
    /// Gebühren für EINE Seite (Entry oder Exit). Slippage ist hier 0
    /// (Slippage wird separat geschätzt). Skaliert mit der Kontraktanzahl.
    /// </summary>
    FeeBreakdown CalculatePerSide(FeeProfile feeProfile, int quantity);

    /// <summary>
    /// Reine Broker-/Exchange-Gebühren für einen vollständigen Round Turn (Entry + Exit),
    /// also 2× die Per-Side-Gebühren. Slippage ist hier 0.
    /// </summary>
    FeeBreakdown CalculateRoundTurn(FeeProfile feeProfile, int quantity);

    /// <summary>
    /// Geschätzte Slippage-Kosten in Geld für EINE Seite:
    /// EstimatedSlippageTicks × TickValue × Kontrakte (Tick-Werte aus dem InstrumentProfile).
    /// </summary>
    decimal CalculateSlippageCost(FeeProfile feeProfile, InstrumentProfile instrument, int quantity);

    /// <summary>
    /// Vollständige Kostenaufstellung eines geschlossenen Trades: Round-Turn-Gebühren
    /// plus Round-Turn-Slippage (2× Per-Side-Slippage). Das ist die Grundlage für NetPnL.
    /// </summary>
    FeeBreakdown CalculateRoundTurnWithSlippage(FeeProfile feeProfile, InstrumentProfile instrument, int quantity);
}
