using TradingBot.Domain.Models;

namespace TradingBot.Tests.Pnl;

/// <summary>Beispiel-Profile für Fee-/PnL-Tests. Werte nur für Tests, nicht hardcoded in Produktivcode.</summary>
internal static class TestProfiles
{
    // NQ: TickSize 0.25, TickValue 5.00 -> PointValue 20.
    public static InstrumentProfile Nq() => new()
    {
        Symbol = "NQ", BrokerSymbol = "NQ", Exchange = "CME", Currency = "USD",
        TickSize = 0.25m, TickValue = 5.00m, PointValue = 20.00m, ContractMultiplier = 20.00m,
        MaxContracts = 5, SessionStart = new TimeOnly(9, 30), SessionEnd = new TimeOnly(16, 0)
    };

    // MNQ: TickSize 0.25, TickValue 0.50 -> PointValue 2.
    public static InstrumentProfile Mnq() => new()
    {
        Symbol = "MNQ", BrokerSymbol = "MNQ", Exchange = "CME", Currency = "USD",
        TickSize = 0.25m, TickValue = 0.50m, PointValue = 2.00m, ContractMultiplier = 2.00m,
        MaxContracts = 20, SessionStart = new TimeOnly(9, 30), SessionEnd = new TimeOnly(16, 0)
    };

    // Per-Side-Gebühren pro Kontrakt NQ: 0.85+1.18+0.10+0.00+0.02+0.00 = 2.15
    public static FeeProfile NqFees() => new()
    {
        BrokerName = "AMP Futures", ExecutionProvider = "Rithmic", Instrument = "NQ",
        CommissionPerSide = 0.85m, ExchangeFeePerSide = 1.18m, ClearingFeePerSide = 0.10m,
        RoutingFeePerSide = 0.00m, NfaFeePerSide = 0.02m, OtherFeePerSide = 0.00m,
        EstimatedSlippageTicks = 1.0m, MaxAllowedSlippageTicks = 3.0m
    };

    // Per-Side-Gebühren pro Kontrakt MNQ: 0.25+0.10+0.02+0.00+0.02+0.00 = 0.39
    public static FeeProfile MnqFees() => new()
    {
        BrokerName = "AMP Futures", ExecutionProvider = "Rithmic", Instrument = "MNQ",
        CommissionPerSide = 0.25m, ExchangeFeePerSide = 0.10m, ClearingFeePerSide = 0.02m,
        RoutingFeePerSide = 0.00m, NfaFeePerSide = 0.02m, OtherFeePerSide = 0.00m,
        EstimatedSlippageTicks = 1.0m, MaxAllowedSlippageTicks = 3.0m
    };
}
