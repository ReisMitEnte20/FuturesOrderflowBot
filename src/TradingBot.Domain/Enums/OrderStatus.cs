namespace TradingBot.Domain.Enums;

/// <summary>Lebenszyklus-Status einer Order.</summary>
public enum OrderStatus
{
    PendingNew = 0,
    New = 1,
    Working = 2,
    PartiallyFilled = 3,
    Filled = 4,
    Cancelled = 5,
    Rejected = 6,
    Expired = 7
}
