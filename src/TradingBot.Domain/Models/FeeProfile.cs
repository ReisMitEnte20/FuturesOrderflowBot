namespace TradingBot.Domain.Models;

/// <summary>
/// Gebührenstruktur pro Broker + Execution-Provider + Instrument. Aus JSON geladen.
/// Alle Werte sind Platzhalter, bis der User echte Broker-Gebühren einträgt.
/// </summary>
public sealed record FeeProfile
{
    public required string BrokerName { get; init; }
    public required string ExecutionProvider { get; init; }
    public required string Instrument { get; init; }

    public decimal CommissionPerSide { get; init; }
    public decimal ExchangeFeePerSide { get; init; }
    public decimal ClearingFeePerSide { get; init; }
    public decimal RoutingFeePerSide { get; init; }
    public decimal NfaFeePerSide { get; init; }
    public decimal OtherFeePerSide { get; init; }

    public decimal EstimatedSlippageTicks { get; init; }
    public decimal MaxAllowedSlippageTicks { get; init; }

    public decimal MonthlyPlatformFee { get; init; }
    public decimal MonthlyDataFeedFee { get; init; }
}
