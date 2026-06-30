using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>
/// Vom OrderManager geführter Zustands-Snapshot einer Order. Unveränderlich;
/// bei jeder Transition wird per <c>with</c> ein neuer Snapshot erzeugt.
/// </summary>
public sealed record OrderState
{
    public required Guid OrderId { get; init; }
    public required string IdempotencyKey { get; init; }
    public Guid? SourceSignalId { get; init; }

    public required string Symbol { get; init; }
    public OrderSide Side { get; init; }
    public OrderType OrderType { get; init; }

    public int RequestedQuantity { get; init; }
    public int FilledQuantity { get; init; }
    public decimal? AverageFillPrice { get; init; }

    public OrderLifecycleState Lifecycle { get; init; } = OrderLifecycleState.Created;
    public string? BrokerOrderId { get; init; }
    public string Message { get; init; } = string.Empty;
    public Guid? BracketGroupId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public int RemainingQuantity => RequestedQuantity - FilledQuantity;

    public bool IsTerminal => Lifecycle is OrderLifecycleState.Filled
        or OrderLifecycleState.Cancelled
        or OrderLifecycleState.Rejected
        or OrderLifecycleState.Failed;
}
