namespace TradingBot.Core.Interfaces;

/// <summary>
/// Abstrahiert die Zeit. Ermöglicht deterministische Backtests/Replays, weil dort
/// eine simulierte Uhr statt der Systemzeit verwendet werden kann.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
