namespace TradingBot.Infrastructure.MarketData.Rithmic;

public sealed class RithmicException : Exception
{
    public int? StatusCode { get; }

    public RithmicException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
        => StatusCode = statusCode;
}