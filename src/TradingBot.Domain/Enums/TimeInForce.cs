namespace TradingBot.Domain.Enums;

/// <summary>Gültigkeitsdauer einer Order. Reale Unterstützung hängt vom BrokerProfile ab.</summary>
public enum TimeInForce
{
    Day = 0,
    GoodTillCancel = 1,
    ImmediateOrCancel = 2,
    FillOrKill = 3
}
