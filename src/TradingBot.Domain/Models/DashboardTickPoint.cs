using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>Ein Tickpunkt für die Visualisierung im Dashboard-Chart.</summary>
public sealed record DashboardTickPoint
{
    public required string Symbol { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public decimal Price { get; init; }
    public decimal Bid { get; init; }
    public decimal Ask { get; init; }
    public decimal Volume { get; init; }
    public PositionSide PositionSide { get; init; } = PositionSide.Flat;
    public int PositionQuantity { get; init; }
    public decimal UnrealizedGrossPnL { get; init; }
}
