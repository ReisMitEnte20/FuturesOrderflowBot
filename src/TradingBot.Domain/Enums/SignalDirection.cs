namespace TradingBot.Domain.Enums;

/// <summary>
/// Richtung eines Strategie-Signals. Ein Signal ist KEINE Order.
/// </summary>
public enum SignalDirection
{
    Flat = 0,
    Long = 1,
    Short = 2
}
