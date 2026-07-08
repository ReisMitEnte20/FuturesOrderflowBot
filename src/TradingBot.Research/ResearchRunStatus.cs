namespace TradingBot.Research;

/// <summary>Status eines Research-Laufs.</summary>
public enum ResearchRunStatus
{
    NotStarted = 0,
    Running = 1,
    Completed = 2,
    Cancelled = 3,
    Failed = 4
}
