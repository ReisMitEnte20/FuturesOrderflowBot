using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Backtesting;

/// <summary>Ergebnis eines Backtest-Laufs.</summary>
public sealed record BacktestResult
{
    public BacktestRunStatus Status { get; init; } = BacktestRunStatus.NotStarted;
    public string? Message { get; init; }

    public BacktestStatistics Statistics { get; init; } = BacktestStatistics.Empty;
    public IReadOnlyList<BacktestTrade> Trades { get; init; } = Array.Empty<BacktestTrade>();

    public int TicksProcessed { get; init; }
    public int SignalsGenerated { get; init; }
    public int OrdersSubmitted { get; init; }
    public int OrdersRejectedByRisk { get; init; }
    public int OrdersRejectedByBroker { get; init; }
    public int OrdersUnfilledAtEnd { get; init; }

    // ---- Offene Position am Datenende (die Engine stellt NICHT zwangsweise glatt) ----
    public PositionSide FinalPositionSide { get; init; } = PositionSide.Flat;
    public int FinalPositionQuantity { get; init; }
    public decimal FinalPositionAvgPrice { get; init; }
    public decimal FinalPositionUnrealizedGross { get; init; }

    /// <summary>Letzter tatsächlich verarbeiteter Tick (Zeit/Preis) – Basis der offenen Bewertung.</summary>
    public DateTimeOffset? LastTickUtc { get; init; }
    public decimal? LastTickPrice { get; init; }
    /// <summary>Offene Position zum letzten beobachteten Preis bewertet (brutto, nicht gebucht; 0 wenn flach).</summary>
    public decimal FinalPositionUnrealizedAtLastTick { get; init; }

    /// <summary>Grund, wenn der <see cref="BacktestRequest.TickGuard"/> den Lauf gestoppt hat.</summary>
    public string? StopReason { get; init; }
    /// <summary>Zeitstempel des NICHT verarbeiteten Ticks, vor dem gestoppt wurde (sein Preis wird bewusst nicht übernommen).</summary>
    public DateTimeOffset? StoppedBeforeTickUtc { get; init; }

    /// <summary>Eingereichte Orders mit auslösendem Signal (für die ID-Verknüpfung Signal → Order → Fill → Trade).</summary>
    public IReadOnlyList<BacktestOrderRecord> Orders { get; init; } = Array.Empty<BacktestOrderRecord>();
    /// <summary>Je abgeschlossenem Trade (gleicher Index wie <see cref="Trades"/>): Order- und Fill-IDs von Ein- und Ausstieg.</summary>
    public IReadOnlyList<BacktestTradeLink> TradeLinks { get; init; } = Array.Empty<BacktestTradeLink>();

    /// <summary>Feed-Zustand am Ende (informativ, aus dem FeedHealthMonitor).</summary>
    public MarketDataConnectionState FinalFeedState { get; init; } = MarketDataConnectionState.Unknown;
}

/// <summary>Eine eingereichte Order mit dem Signal, das sie ausgelöst hat, und der Position zum Zeitpunkt der Einreichung.</summary>
public sealed record BacktestOrderRecord(Guid OrderId, Guid SignalId, DateTimeOffset SignalTime, SignalDirection SignalDirection,
    string SignalStrategy, string SignalReason, decimal SignalReferencePrice, OrderSide Side, int Quantity, OrderType OrderType,
    OrderLifecycleState Lifecycle, PositionSide PositionAtSubmit, int PositionQuantityAtSubmit);

/// <summary>Order-/Fill-IDs eines abgeschlossenen Trades (Ein- und Ausstieg).</summary>
public sealed record BacktestTradeLink(Guid EntryOrderId, Guid EntryFillId, Guid ExitOrderId, Guid ExitFillId);
