namespace TradingBot.Infrastructure.MarketData.Rithmic;

public sealed class RithmicConfig
{
    public required string ApiKey { get; init; }
    public required string ApiSecret { get; init; }
    public string BaseUrl { get; init; } = "https://api.rithmic.com";
    public int TimeoutSeconds { get; init; } = 30;
    public int MaxRetries { get; init; } = 3;
}