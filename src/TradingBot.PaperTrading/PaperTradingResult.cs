using TradingBot.PaperTrading.Positions;

namespace TradingBot.PaperTrading;

/// <summary>Endergebnis einer Paper-Session (nach Stop/Datenende/Abbruch).</summary>
public sealed record PaperTradingResult
{
    public PaperTradingRunStatus Status { get; init; }
    public string? Message { get; init; }

    /// <summary>Finaler Zustands-Snapshot der Session.</summary>
    public required PaperTradingSessionState FinalState { get; init; }

    public IReadOnlyList<PaperClosedTrade> ClosedTrades { get; init; } = Array.Empty<PaperClosedTrade>();

    public int TicksProcessed { get; init; }
    public int SignalsGenerated { get; init; }
    public int OrdersSubmitted { get; init; }
    public int OrdersRejectedByRisk { get; init; }
    public int OrdersRejectedByBroker { get; init; }
    public int OrdersUnfilledAtEnd { get; init; }
}
