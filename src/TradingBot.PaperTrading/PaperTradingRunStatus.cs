namespace TradingBot.PaperTrading;

/// <summary>Zustand einer Paper-Trading-Session.</summary>
public enum PaperTradingRunStatus
{
    NotStarted = 0,
    Running = 1,
    Paused = 2,
    /// <summary>Manuell per StopAsync beendet.</summary>
    Stopped = 3,
    /// <summary>Datenstrom zu Ende (Replay abgeschlossen).</summary>
    Completed = 4,
    /// <summary>Durch externes CancellationToken abgebrochen.</summary>
    Cancelled = 5,
    Failed = 6
}
