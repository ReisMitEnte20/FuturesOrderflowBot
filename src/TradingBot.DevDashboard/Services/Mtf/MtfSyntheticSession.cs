using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.DevDashboard.Services.Mtf;

/// <summary>
/// Deterministische SYNTHETISCHE Session (keine Marktdaten) für Funktionstests und Abnahmen: Zufallspfad mit Trendphasen,
/// unregelmäßigen Abständen und Aggressorseite. Gleicher Seed → identische Ereignisfolge.
/// </summary>
public static class MtfSyntheticSession
{
    public static List<MarketTick> Generate(int seed, DateTimeOffset start, int minutes, decimal tickSize = 0.25m, decimal startPrice = 21000m, string symbol = "MNQ")
    {
        var rnd = new Random(seed);
        var list = new List<MarketTick>();
        var t = start;
        var end = start.AddMinutes(minutes);
        decimal price = startPrice;
        int phaseLen = 0; int drift = 0;
        while (true)
        {
            t = t.AddTicks((long)(TimeSpan.TicksPerMillisecond * (20 + rnd.Next(0, 900))));
            if (t >= end) break;
            if (phaseLen-- <= 0) { phaseLen = 150 + rnd.Next(0, 500); drift = rnd.Next(-1, 2); }
            int step = rnd.Next(-2, 3) + (rnd.NextDouble() < 0.35 ? drift : 0);
            price = Math.Max(tickSize, price + step * tickSize);
            bool buy = step > 0 || (step == 0 && rnd.NextDouble() < 0.5 + drift * 0.15);
            list.Add(new MarketTick
            {
                Symbol = symbol, Timestamp = t, Price = price, Volume = 1 + rnd.Next(0, 4) + (rnd.NextDouble() < 0.05 ? rnd.Next(5, 25) : 0),
                Aggressor = buy ? AggressorSide.Buy : AggressorSide.Sell
            });
        }
        return list;
    }
}
