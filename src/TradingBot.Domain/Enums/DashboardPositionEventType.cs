namespace TradingBot.Domain.Enums;

/// <summary>Typ eines Positions-Ereignisses für Dashboard-Overlays im Tick-Chart.</summary>
public enum DashboardPositionEventType
{
    Entry = 0,
    Add = 1,
    Reduce = 2,
    Exit = 3,
    Flip = 4
}
