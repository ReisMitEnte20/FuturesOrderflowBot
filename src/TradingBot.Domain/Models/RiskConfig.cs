namespace TradingBot.Domain.Models;

/// <summary>
/// Risiko-Grenzen für den RiskManager. Aus JSON geladen, zur Laufzeit über das
/// Dashboard anpassbar. Pflichtgrenzen schützen u. a. Prop-Firm-/Funded-Regeln.
/// </summary>
public sealed record RiskConfig
{
    public decimal MaxDailyLoss { get; init; }
    public decimal MaxLossPerTrade { get; init; }
    public int MaxTradesPerDay { get; init; }
    public int MaxContracts { get; init; }
    public int MaxOpenPositions { get; init; }
    public int MaxOrdersPerMinute { get; init; }
    public int MaxConsecutiveLosses { get; init; }

    public bool EnforceSession { get; init; } = true;

    public bool ProfitLockEnabled { get; init; }
    public decimal? ProfitLockThreshold { get; init; }

    public bool TrailingDrawdownEnabled { get; init; }
    public decimal? TrailingDrawdownAmount { get; init; }
}
