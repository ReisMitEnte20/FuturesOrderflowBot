using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>Eine (Teil-)Ausführung einer Order. Mehrere Fills pro Order sind möglich.</summary>
public sealed record FillEvent
{
    public Guid FillId { get; init; } = Guid.NewGuid();
    public required Guid OrderId { get; init; }
    public string? BrokerOrderId { get; init; }

    public required string Symbol { get; init; }
    public OrderSide Side { get; init; }
    public int Quantity { get; init; }
    public decimal FillPrice { get; init; }
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>True, wenn die Order damit noch nicht vollständig gefüllt ist.</summary>
    public bool IsPartial { get; init; }
}
