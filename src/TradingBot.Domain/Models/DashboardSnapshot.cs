using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>
/// Read-only Snapshot für ein externes Dashboard (z. B. HKUDS/Vibe-Trading),
/// inkl. Session-Status, PnL, Tick-Serie und Positions-Events.
/// </summary>
public sealed record DashboardSnapshot
{
    public Guid SessionId { get; init; }
    public required string Symbol { get; init; }
    public TradingMode TradingMode { get; init; } = TradingMode.Paper;
    public string SessionStatus { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; }

    public bool IsReadOnly { get; init; } = true;
    public bool IsExecutionControlEnabled { get; init; }

    public int TicksProcessed { get; init; }
    public decimal GrossPnL { get; init; }
    public decimal NetPnL { get; init; }
    public decimal TotalFees { get; init; }
    public decimal TotalSlippage { get; init; }
    public decimal UnrealizedGrossPnL { get; init; }

    public ConnectionStatus FeedHealthStatus { get; init; } = ConnectionStatus.Unknown;
    public RiskDecision? RiskStatus { get; init; }
    public bool KillSwitchActive { get; init; }

    public IReadOnlyList<DashboardTickPoint> TickSeries { get; init; } = Array.Empty<DashboardTickPoint>();
    /// <summary>
    /// Alias für externe Dashboard-Clients, die Tick-Chart-Daten unter "tickData" erwarten.
    /// </summary>
    public IReadOnlyList<DashboardTickPoint> TickData => TickSeries;
    public IReadOnlyList<DashboardPositionEvent> PositionEvents { get; init; } = Array.Empty<DashboardPositionEvent>();
}
