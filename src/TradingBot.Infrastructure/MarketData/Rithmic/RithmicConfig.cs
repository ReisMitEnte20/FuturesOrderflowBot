namespace TradingBot.Infrastructure.MarketData.Rithmic;

public sealed class RithmicConfig
{
    public required string Username { get; init; }
    public required string Password { get; init; }
    public string BaseUrl { get; init; } = "https://api.rithmic.com";
    public int TimeoutSeconds { get; init; } = 30;
    public int MaxRetries { get; init; } = 3;
    public string DefaultSymbol { get; init; } = "NQ";
    public string DefaultInterval { get; init; } = "1m";
    public int LookbackCandles { get; init; } = 1000;
}