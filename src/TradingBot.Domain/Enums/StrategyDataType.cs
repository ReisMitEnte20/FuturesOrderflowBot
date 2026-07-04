namespace TradingBot.Domain.Enums;

/// <summary>
/// Primäre Datenquelle, die eine Strategie benötigt. Die StrategyEngine routet Events
/// nur an Strategien mit passendem Datentyp. <see cref="OrderFlow"/> erfordert echte
/// Bid/Ask/Aggressor-Daten – es werden niemals Orderflow-Werte erfunden.
/// </summary>
public enum StrategyDataType
{
    Tick = 0,
    Candle = 1,
    OrderFlow = 2
}
