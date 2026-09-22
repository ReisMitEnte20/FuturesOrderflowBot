namespace TradingBot.Infrastructure.MarketData.Rithmic.Models;

public sealed record RithmicCandleResponse
{
    public bool Success { get; init; }
    public List<RithmicCandle> Data { get; init; } = [];
    public string? Error { get; init; }
}