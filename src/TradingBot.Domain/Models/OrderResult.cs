using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>Antwort des Brokers/Adapters auf eine Order-Aktion.</summary>
public sealed record OrderResult
{
    public required Guid OrderId { get; init; }

    /// <summary>Order-ID auf Broker-Seite (sofern vergeben).</summary>
    public string? BrokerOrderId { get; init; }

    public OrderStatus Status { get; init; }
    public int FilledQuantity { get; init; }
    public decimal? AverageFillPrice { get; init; }
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Ablehnungsgrund/Meldung des Brokers (falls vorhanden).</summary>
    public string? Message { get; init; }

    public bool IsRejected => Status == OrderStatus.Rejected;
}
