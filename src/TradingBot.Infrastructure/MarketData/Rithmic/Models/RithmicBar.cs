namespace TradingBot.Infrastructure.MarketData.Rithmic.Models;

public sealed record RithmicBar
{
    public required string Symbol { get; init; }
    public required string Interval { get; init; }
    public required long OpenTime { get; init; }
    public required long CloseTime { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public decimal Volume { get; init; }
    public decimal? VWAP { get; init; }
    public long? Trades { get; init; }
    public long? BuyVolume { get; init; }
    public long? SellVolume { get; init; }
}