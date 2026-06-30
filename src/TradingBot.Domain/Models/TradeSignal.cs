using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>
/// Reines Strategie-Signal. WICHTIG: Ein Signal ist KEINE Order und enthält
/// keine Ausführungsdetails. Erst RiskManager + OrderManager entscheiden, ob und
/// wie daraus eine Order wird. Mengen-/Stop-Angaben sind nur Vorschläge.
/// </summary>
public sealed record TradeSignal
{
    public Guid SignalId { get; init; } = Guid.NewGuid();
    public required string StrategyName { get; init; }
    public required string Symbol { get; init; }
    public SignalDirection Direction { get; init; }
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Referenzpreis zum Signalzeitpunkt (nur informativ).</summary>
    public decimal ReferencePrice { get; init; }

    /// <summary>Optionale Konfidenz 0..1 (Strategie-intern).</summary>
    public decimal Confidence { get; init; }

    /// <summary>Vorgeschlagene Kontraktanzahl. Finale Menge entscheidet der RiskManager.</summary>
    public int? SuggestedQuantity { get; init; }

    public int? SuggestedStopLossTicks { get; init; }
    public int? SuggestedTakeProfitTicks { get; init; }

    /// <summary>Kurzbegründung des Signals (z. B. "Stacked Imbalance + CVD").</summary>
    public string Reason { get; init; } = string.Empty;
}
