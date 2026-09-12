using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>Positions-Ereignis für Overlays im Tick-Chart (Entry/Exit/Partial).</summary>
public sealed record DashboardPositionEvent
{
    public DashboardPositionEventType EventType { get; init; }
    public required string Symbol { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public PositionSide PositionSide { get; init; } = PositionSide.Flat;
    public int PositionQuantity { get; init; }
    public decimal Price { get; init; }
    public decimal RealizedGrossPnL { get; init; }
    public decimal RealizedNetPnL { get; init; }
    public decimal? StopLossPrice { get; init; }
    public decimal? TakeProfitPrice { get; init; }
    public Guid? SourceOrderId { get; init; }
}
