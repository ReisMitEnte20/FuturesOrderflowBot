namespace TradingBot.Domain.Enums;

/// <summary>Abspielgeschwindigkeit des Replay-Feeds.</summary>
public enum ReplayMode
{
    /// <summary>So schnell wie möglich, ohne Verzögerung (Backtest).</summary>
    AsFastAsPossible = 0,
    /// <summary>Echtzeit: Verzögerung entspricht den Tick-Zeitabständen.</summary>
    RealTime = 1,
    /// <summary>Schneller als Echtzeit um den Faktor SpeedFactor.</summary>
    FasterThanRealtime = 2
}
