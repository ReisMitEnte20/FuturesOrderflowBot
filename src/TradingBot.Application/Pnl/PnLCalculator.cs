using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Pnl;

/// <summary>
/// Standard-Implementierung von <see cref="IPnLCalculator"/>.
/// Komponiert die Gebühren über einen <see cref="IFeeCalculator"/> und hält
/// GrossPnL/NetPnL strikt getrennt. Validiert TickSize, TickValue und Kontraktanzahl.
/// </summary>
public sealed class PnLCalculator : IPnLCalculator
{
    private readonly IFeeCalculator _feeCalculator;

    public PnLCalculator(IFeeCalculator feeCalculator)
        => _feeCalculator = feeCalculator ?? throw new ArgumentNullException(nameof(feeCalculator));

    public decimal CalculateGrossPnL(
        InstrumentProfile instrument, PositionSide side,
        decimal entryPrice, decimal exitPrice, int quantity)
    {
        ArgumentNullException.ThrowIfNull(instrument);

        if (instrument.TickSize <= 0m)
            throw new ArgumentOutOfRangeException(nameof(instrument), "TickSize muss > 0 sein.");
        if (instrument.TickValue <= 0m)
            throw new ArgumentOutOfRangeException(nameof(instrument), "TickValue muss > 0 sein.");
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Kontraktanzahl muss > 0 sein.");
        if (side == PositionSide.Flat)
            throw new ArgumentException("Side muss Long oder Short sein.", nameof(side));

        decimal priceMove = side == PositionSide.Long
            ? exitPrice - entryPrice
            : entryPrice - exitPrice;

        decimal ticks = priceMove / instrument.TickSize;
        return ticks * instrument.TickValue * quantity;
    }

    public PnLResult CalculateTrade(
        InstrumentProfile instrument, FeeProfile feeProfile, PositionSide side,
        decimal entryPrice, decimal exitPrice, int quantity)
    {
        ArgumentNullException.ThrowIfNull(feeProfile);

        // Validierung von instrument/quantity/side erfolgt in CalculateGrossPnL.
        decimal gross = CalculateGrossPnL(instrument, side, entryPrice, exitPrice, quantity);

        // Ticks aus Gross rückrechnen (TickValue & quantity sind validiert > 0).
        decimal ticks = gross / (instrument.TickValue * quantity);

        FeeBreakdown fees = _feeCalculator.CalculateRoundTurnWithSlippage(feeProfile, instrument, quantity);

        return new PnLResult
        {
            GrossPnL = gross,
            Ticks = ticks,
            Fees = fees
        };
    }
}
