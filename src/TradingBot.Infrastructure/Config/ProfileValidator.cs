using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Config;

/// <summary>
/// Einfache Plausibilitätsprüfungen für geladene Profile/Configs.
/// Wirft <see cref="ProfileValidationException"/> bei ungültigen Werten – keine stillen Fehler.
/// </summary>
public static class ProfileValidator
{
    public static void Validate(BrokerProfile p)
    {
        Require(!string.IsNullOrWhiteSpace(p.BrokerName), "BrokerName darf nicht leer sein.");
        Require(!string.IsNullOrWhiteSpace(p.ExecutionProvider), "ExecutionProvider darf nicht leer sein.");
        Require(p.MaxOrdersPerSecond >= 0, "MaxOrdersPerSecond darf nicht negativ sein.");
        Require(p.ApiRateLimit >= 0, "ApiRateLimit darf nicht negativ sein.");
    }

    public static void Validate(InstrumentProfile p)
    {
        Require(!string.IsNullOrWhiteSpace(p.Symbol), "Symbol darf nicht leer sein.");
        Require(!string.IsNullOrWhiteSpace(p.BrokerSymbol), "BrokerSymbol darf nicht leer sein.");
        Require(!string.IsNullOrWhiteSpace(p.Exchange), "Exchange darf nicht leer sein.");
        Require(p.TickSize > 0m, "TickSize muss > 0 sein.");
        Require(p.TickValue > 0m, "TickValue muss > 0 sein.");
        Require(p.PointValue > 0m, "PointValue muss > 0 sein.");
        Require(p.ContractMultiplier > 0m, "ContractMultiplier muss > 0 sein.");
        Require(p.MaxContracts > 0, "MaxContracts muss > 0 sein.");
        Require(p.DefaultStopLossTicks >= 0, "DefaultStopLossTicks darf nicht negativ sein.");
        Require(p.DefaultTakeProfitTicks >= 0, "DefaultTakeProfitTicks darf nicht negativ sein.");
        Require(p.DefaultTrailingStopTicks >= 0, "DefaultTrailingStopTicks darf nicht negativ sein.");
        Require(p.SessionStart < p.SessionEnd,
            "SessionStart muss vor SessionEnd liegen (gleiche-Tag-Sessionlogik).");
    }

    public static void Validate(FeeProfile p)
    {
        Require(!string.IsNullOrWhiteSpace(p.BrokerName), "BrokerName darf nicht leer sein.");
        Require(!string.IsNullOrWhiteSpace(p.ExecutionProvider), "ExecutionProvider darf nicht leer sein.");
        Require(!string.IsNullOrWhiteSpace(p.Instrument), "Instrument darf nicht leer sein.");
        Require(p.CommissionPerSide >= 0m, "CommissionPerSide darf nicht negativ sein.");
        Require(p.ExchangeFeePerSide >= 0m, "ExchangeFeePerSide darf nicht negativ sein.");
        Require(p.ClearingFeePerSide >= 0m, "ClearingFeePerSide darf nicht negativ sein.");
        Require(p.RoutingFeePerSide >= 0m, "RoutingFeePerSide darf nicht negativ sein.");
        Require(p.NfaFeePerSide >= 0m, "NfaFeePerSide darf nicht negativ sein.");
        Require(p.OtherFeePerSide >= 0m, "OtherFeePerSide darf nicht negativ sein.");
        Require(p.EstimatedSlippageTicks >= 0m, "EstimatedSlippageTicks darf nicht negativ sein.");
        Require(p.MaxAllowedSlippageTicks >= 0m, "MaxAllowedSlippageTicks darf nicht negativ sein.");
        Require(p.MonthlyPlatformFee >= 0m, "MonthlyPlatformFee darf nicht negativ sein.");
        Require(p.MonthlyDataFeedFee >= 0m, "MonthlyDataFeedFee darf nicht negativ sein.");
    }

    public static void Validate(RiskConfig p)
    {
        Require(p.MaxDailyLoss > 0m, "MaxDailyLoss muss > 0 sein.");
        Require(p.MaxLossPerTrade >= 0m, "MaxLossPerTrade darf nicht negativ sein.");
        Require(p.MaxTradesPerDay >= 0, "MaxTradesPerDay darf nicht negativ sein.");
        Require(p.MaxContracts > 0, "MaxContracts muss > 0 sein.");
        Require(p.MaxOpenPositions > 0, "MaxOpenPositions muss > 0 sein.");
        Require(p.MaxOrdersPerMinute >= 0, "MaxOrdersPerMinute darf nicht negativ sein.");
        Require(p.MaxConsecutiveLosses >= 0, "MaxConsecutiveLosses darf nicht negativ sein.");
        if (p.ProfitLockEnabled)
            Require(p.ProfitLockThreshold is > 0m, "ProfitLockThreshold muss > 0 sein, wenn ProfitLock aktiv ist.");
        if (p.TrailingDrawdownEnabled)
            Require(p.TrailingDrawdownAmount is > 0m, "TrailingDrawdownAmount muss > 0 sein, wenn TrailingDrawdown aktiv ist.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new ProfileValidationException(message);
    }
}
