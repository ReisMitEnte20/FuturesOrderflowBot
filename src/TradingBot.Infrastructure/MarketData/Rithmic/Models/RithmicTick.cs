namespace TradingBot.Infrastructure.MarketData.Rithmic.Models;

public sealed record RithmicTick
{
    public required string Symbol { get; init; }
    public required long Timestamp { get; init; }
    public decimal Price { get; init; }
    public decimal? Bid { get; init; }
    public decimal? Ask { get; init; }
    public decimal? BidSize { get; init; }
    public decimal? AskSize { get; init; }
    public decimal? Volume { get; init; }
    public string? TradeDirection { get; init; }
}