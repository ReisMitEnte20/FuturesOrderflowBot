using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Fees;

/// <summary>
/// Standard-Implementierung von <see cref="IFeeCalculator"/>.
/// Alle Werte stammen ausschließlich aus dem übergebenen FeeProfile/InstrumentProfile –
/// nichts ist hardcoded. Rechnet durchgängig in <see cref="decimal"/>.
/// </summary>
public sealed class FeeCalculator : IFeeCalculator
{
    public FeeBreakdown CalculatePerSide(FeeProfile feeProfile, int quantity)
    {
        ArgumentNullException.ThrowIfNull(feeProfile);
        RequirePositive(quantity, nameof(quantity));

        return new FeeBreakdown
        {
            Commission   = feeProfile.CommissionPerSide   * quantity,
            ExchangeFee  = feeProfile.ExchangeFeePerSide  * quantity,
            ClearingFee  = feeProfile.ClearingFeePerSide  * quantity,
            RoutingFee   = feeProfile.RoutingFeePerSide   * quantity,
            NfaFee       = feeProfile.NfaFeePerSide       * quantity,
            OtherFee     = feeProfile.OtherFeePerSide     * quantity,
            SlippageCost = 0m
        };
    }

    public FeeBreakdown CalculateRoundTurn(FeeProfile feeProfile, int quantity)
    {
        var perSide = CalculatePerSide(feeProfile, quantity);

        // Round Turn = Entry + Exit = 2 Seiten. Slippage bleibt hier 0.
        return new FeeBreakdown
        {
            Commission   = perSide.Commission  * 2m,
            ExchangeFee  = perSide.ExchangeFee * 2m,
            ClearingFee  = perSide.ClearingFee * 2m,
            RoutingFee   = perSide.RoutingFee  * 2m,
            NfaFee       = perSide.NfaFee      * 2m,
            OtherFee     = perSide.OtherFee    * 2m,
            SlippageCost = 0m
        };
    }

    public decimal CalculateSlippageCost(FeeProfile feeProfile, InstrumentProfile instrument, int quantity)
    {
        ArgumentNullException.ThrowIfNull(feeProfile);
        ArgumentNullException.ThrowIfNull(instrument);
        RequirePositive(quantity, nameof(quantity));

        if (instrument.TickValue <= 0m)
            throw new ArgumentOutOfRangeException(nameof(instrument), "TickValue muss > 0 sein.");

        // Per-Side-Slippage: geschätzte Ticks × Geldwert pro Tick × Kontrakte.
        return feeProfile.EstimatedSlippageTicks * instrument.TickValue * quantity;
    }

    public FeeBreakdown CalculateRoundTurnWithSlippage(FeeProfile feeProfile, InstrumentProfile instrument, int quantity)
    {
        var roundTurn = CalculateRoundTurn(feeProfile, quantity);
        var slippagePerSide = CalculateSlippageCost(feeProfile, instrument, quantity);

        // Slippage fällt auf beiden Seiten an (Entry + Exit).
        return roundTurn with { SlippageCost = slippagePerSide * 2m };
    }

    private static void RequirePositive(int quantity, string paramName)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(paramName, "Kontraktanzahl muss > 0 sein.");
    }
}
