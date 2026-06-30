using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Tests.MarketData;

/// <summary>Stellbare Uhr für deterministische Heartbeat-Tests.</summary>
internal sealed class MutableClock : IClock
{
    public DateTimeOffset UtcNow { get; set; }
    public MutableClock(DateTimeOffset now) => UtcNow = now;
}

internal static class Md
{
    public static readonly DateTimeOffset T0 = new(2026, 6, 23, 13, 30, 0, TimeSpan.Zero);

    public static MarketTick Tick(
        double offsetSeconds, decimal price, decimal volume, string symbol = "NQ",
        AggressorSide aggressor = AggressorSide.Unknown) => new()
    {
        Symbol = symbol,
        Timestamp = T0.AddSeconds(offsetSeconds),
        Price = price,
        Volume = volume,
        Aggressor = aggressor
    };
}
