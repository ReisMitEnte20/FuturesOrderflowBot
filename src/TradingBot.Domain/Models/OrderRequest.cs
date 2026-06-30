using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>
/// Konkrete Order-Anfrage an den Broker. Wird AUSSCHLIESSLICH vom OrderManager
/// erzeugt – niemals von einer Strategie. Trägt einen Idempotency-Key, damit ein
/// Signal nicht doppelt ausgeführt wird.
/// </summary>
public sealed record OrderRequest
{
    public Guid OrderId { get; init; } = Guid.NewGuid();

    /// <summary>Idempotenz-Schlüssel (typisch abgeleitet vom Signal), verhindert Doppel-Orders.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Signal, aus dem diese Order entstand (Rückverfolgbarkeit).</summary>
    public Guid? SourceSignalId { get; init; }

    public required string AccountId { get; init; }
    public required string Symbol { get; init; }
    public required string BrokerSymbol { get; init; }

    public OrderSide Side { get; init; }
    public OrderType OrderType { get; init; }
    public int Quantity { get; init; }
    public TimeInForce TimeInForce { get; init; } = TimeInForce.Day;

    public decimal? LimitPrice { get; init; }
    public decimal? StopPrice { get; init; }

    /// <summary>Optionaler angehängter Stop-Loss-Preis (Bracket).</summary>
    public decimal? StopLossPrice { get; init; }

    /// <summary>Optionaler angehängter Take-Profit-Preis (Bracket).</summary>
    public decimal? TakeProfitPrice { get; init; }

    /// <summary>Verknüpft Entry/SL/TP einer Bracket bzw. OCO-Gruppe.</summary>
    public Guid? BracketGroupId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
