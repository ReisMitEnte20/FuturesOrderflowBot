namespace TradingBot.Domain.Enums;

/// <summary>
/// Vom OrderManager getrackter Lebenszyklus einer Order (intern), getrennt vom
/// broker-gemeldeten <see cref="OrderStatus"/>. Failed/Unknown verhalten sich fail-closed.
/// </summary>
public enum OrderLifecycleState
{
    Created = 0,
    Submitted = 1,
    Accepted = 2,
    PartiallyFilled = 3,
    Filled = 4,
    Cancelled = 5,
    Rejected = 6,
    Failed = 7
}
