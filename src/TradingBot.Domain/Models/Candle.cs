namespace TradingBot.Domain.Models;

/// <summary>OHLCV-Kerze (Zeit-, Tick- oder Volumen-basiert; Aggregation erfolgt später).</summary>
public sealed record Candle
{
    public required string Symbol { get; init; }
    public DateTimeOffset OpenTime { get; init; }
    public DateTimeOffset CloseTime { get; init; }

    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public decimal Volume { get; init; }

    public decimal Range => High - Low;
    public bool IsBullish => Close > Open;
}
