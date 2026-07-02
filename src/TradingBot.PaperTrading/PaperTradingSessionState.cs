using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.PaperTrading;

/// <summary>
/// Read-only Snapshot des Session-Zustands (für Anzeige/Monitoring).
/// PnL-Werte sind realisierte Werte aus dem PositionManager; TotalSlippage ist informativ
/// (bereits in den Fill-Preisen enthalten, wird NICHT zusätzlich abgezogen).
/// </summary>
public sealed record PaperTradingSessionState
{
    public Guid SessionId { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? StoppedAt { get; init; }
    public string CurrentSymbol { get; init; } = string.Empty;

    /// <summary>Immer <see cref="TradingMode.Paper"/> – es gibt keinen Live-Modus.</summary>
    public TradingMode TradingMode { get; init; } = TradingMode.Paper;

    public PaperTradingRunStatus Status { get; init; }
    public bool IsRunning { get; init; }
    public bool IsPaused { get; init; }

    public Position? CurrentPosition { get; init; }
    public int TicksProcessed { get; init; }
    public int OpenOrders { get; init; }
    public int FilledOrders { get; init; }
    public int RejectedOrders { get; init; }
    public int TradesToday { get; init; }

    public decimal GrossPnL { get; init; }
    public decimal NetPnL { get; init; }
    public decimal TotalFees { get; init; }
    public decimal TotalSlippage { get; init; }
    public decimal UnrealizedGrossPnL { get; init; }

    public DateTimeOffset? LastTickTime { get; init; }
    public ConnectionStatus FeedHealthStatus { get; init; } = ConnectionStatus.Unknown;

    /// <summary>Letzte Risk-Entscheidung (Grund + Approved), sofern vorhanden.</summary>
    public RiskDecision? RiskStatus { get; init; }

    public bool KillSwitchActive { get; init; }
}
