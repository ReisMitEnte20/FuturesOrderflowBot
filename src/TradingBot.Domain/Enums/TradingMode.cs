namespace TradingBot.Domain.Enums;

/// <summary>
/// Betriebsmodus des Bots. Paper ist Standard; Live muss manuell aktiviert werden.
/// Alle Modi durchlaufen dieselbe Signal-&gt;Risk-&gt;Order-Pipeline.
/// </summary>
public enum TradingMode
{
    Backtest = 0,
    Replay = 1,
    Paper = 2,
    Live = 3
}
