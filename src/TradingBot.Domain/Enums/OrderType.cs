namespace TradingBot.Domain.Enums;

/// <summary>Ordertyp. Welche Typen real erlaubt sind, bestimmt das BrokerProfile.</summary>
public enum OrderType
{
    Market = 0,
    Limit = 1,
    Stop = 2,
    StopLimit = 3
}
