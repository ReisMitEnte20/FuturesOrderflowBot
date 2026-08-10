using TradingBot.Domain.Enums;

namespace TradingBot.DevDashboard.Services;

/// <summary>
/// RESEARCH / SIMULATION ONLY. Erzeugt eine DETERMINISTISCHE Replay-Demo-Session (Bars + Trades)
/// für den Backtest Replay Visualizer (Phase 12F). Read-only, gecacht, fester Seed → identische
/// Daten. Keine Broker-API, keine Live-Execution, keine echten Orders, keine Netzwerkcalls;
/// referenziert NICHT <c>TradingBot.Execution</c>. Die Trades stammen aus einer künstlichen
/// Demo-Logik — KEINE echte Strategie-Performance.
/// </summary>
public sealed class ReplayDemoService
{
    private const int Seed = 4242;
    private const int BarCount = 180;
    private const decimal DemoDollarPerPoint = 2m;   // Demo-Multiplikator (kein echter Kontraktwert)
    private const decimal DemoFeePerTrade = 4.00m;   // Demo-Kostenannahme (kein echter Broker-Tarif)

    private static readonly DateTimeOffset Start = new(2025, 1, 6, 14, 30, 0, TimeSpan.Zero);

    private readonly object _sync = new();
    private ReplaySession? _cached;

    public ReplaySession GetSession()
    {
        lock (_sync) return _cached ??= Build();
    }

    private static ReplaySession Build()
    {
        var bars = BuildBars();
        var trades = BuildTrades(bars);

        // Realisierte Equity je Bar = Summe NetPnL aller bis dahin geschlossenen Trades.
        var equity = new decimal[bars.Count];
        decimal running = 0m;
        int t = 0;
        var closedByExit = trades.OrderBy(x => x.ExitIndex).ToList();
        for (int i = 0; i < bars.Count; i++)
        {
            while (t < closedByExit.Count && closedByExit[t].ExitIndex <= i)
                running += closedByExit[t++].NetPnL;
            equity[i] = running;
        }

        return new ReplaySession
        {
            Symbol = "MNQ (Demo)",
            Bars = bars,
            Trades = trades,
            RealizedEquityByBar = equity,
            DollarPerPoint = DemoDollarPerPoint,
            TotalNetPnL = trades.Sum(x => x.NetPnL)
        };
    }

    private static IReadOnlyList<ReplayBar> BuildBars()
    {
        var rng = new Random(Seed);
        var bars = new List<ReplayBar>(BarCount);
        decimal price = 20000m;
        for (int i = 0; i < BarCount; i++)
        {
            // Deterministischer Random-Walk mit leichter Trendkomponente.
            decimal drift = (decimal)Math.Sin(i / 18.0) * 6m;
            decimal noise = (decimal)(rng.NextDouble() - 0.5) * 24m;
            decimal open = price;
            decimal close = Math.Round(open + drift + noise, 2);
            decimal hi = Math.Max(open, close) + Math.Round((decimal)rng.NextDouble() * 8m, 2);
            decimal lo = Math.Min(open, close) - Math.Round((decimal)rng.NextDouble() * 8m, 2);
            decimal vol = 200m + Math.Round((decimal)rng.NextDouble() * 800m, 0);
            decimal delta = Math.Round((decimal)(rng.NextDouble() - 0.5) * 400m, 0);

            bars.Add(new ReplayBar
            {
                Index = i, Time = Start.AddMinutes(i),
                Open = open, High = hi, Low = lo, Close = close, Volume = vol, Delta = delta
            });
            price = close;
        }
        return bars;
    }

    private static IReadOnlyList<ReplayTradeMarker> BuildTrades(IReadOnlyList<ReplayBar> bars)
    {
        // Deterministischer Demo-Fahrplan: alle 30 Bars ein Trade, Haltedauer 14 Bars, abwechselnd.
        var entries = new[] { 12, 42, 72, 102, 132, 162 };
        const int hold = 14;
        var trades = new List<ReplayTradeMarker>();
        int id = 1;
        foreach (var entry in entries)
        {
            int exit = Math.Min(entry + hold, bars.Count - 1);
            if (entry >= bars.Count - 1) break;
            bool isLong = id % 2 == 1;
            var side = isLong ? PositionSide.Long : PositionSide.Short;
            decimal ep = bars[entry].Close;
            decimal xp = bars[exit].Close;
            decimal sign = isLong ? 1m : -1m;
            decimal net = Math.Round((xp - ep) * sign * DemoDollarPerPoint - DemoFeePerTrade, 2);

            trades.Add(new ReplayTradeMarker
            {
                Id = id, Side = side,
                EntryIndex = entry, ExitIndex = exit,
                EntryTime = bars[entry].Time, ExitTime = bars[exit].Time,
                EntryPrice = ep, ExitPrice = xp,
                StopLoss = Math.Round(ep - sign * 40m, 2),
                TakeProfit = Math.Round(ep + sign * 60m, 2),
                NetPnL = net
            });
            id++;
        }
        return trades;
    }
}
