namespace TradingBot.Domain.Enums;

/// <summary>
/// Grund, warum der RiskManager ein Signal abgelehnt hat.
/// <see cref="None"/> bedeutet: genehmigt.
/// </summary>
public enum RiskRejectionReason
{
    None = 0,
    MaxDailyLossReached = 1,
    MaxLossPerTradeExceeded = 2,
    MaxTradesPerDayReached = 3,
    MaxContractsExceeded = 4,
    MaxOpenPositionsReached = 5,
    MaxOrdersPerMinuteExceeded = 6,
    ConsecutiveLossLimitReached = 7,
    OutsideTradingSession = 8,
    BrokerDisconnected = 9,
    MarketDataDisconnected = 10,
    MissingBrokerProfile = 11,
    MissingInstrumentProfile = 12,
    MissingFeeProfile = 13,
    KillSwitchActive = 14,
    TechnicalError = 15,
    PositionMismatch = 16,
    DuplicateSignal = 17,
    MissingRiskConfig = 18,
    SymbolNotAllowed = 19,
    ProfitLockActive = 20,
    TrailingDrawdownHit = 21
}
