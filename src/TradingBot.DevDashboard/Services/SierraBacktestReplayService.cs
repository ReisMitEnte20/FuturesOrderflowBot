using System.Diagnostics;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.MarketData.Import;

namespace TradingBot.DevDashboard.Services;

/// <summary>Ergebnis eines lokalen Sierra-Replay-Backtests (read-only, Simulation-only).</summary>
public sealed record SierraReplayResult
{
    public required ReplaySession Session { get; init; }
    public long BarsProcessed { get; init; }
    public long ParseErrors { get; init; }
    public decimal NetDelta { get; init; }
    public decimal FinalCumulativeDelta { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public bool DeltaCvdAvailable { get; init; }
    public long ElapsedMs { get; init; }

    public int Wins => Session.Trades.Count(t => t.NetPnL > 0m);
    public int Losses => Session.Trades.Count(t => t.NetPnL < 0m);
}

/// <summary>
/// LOCAL HISTORICAL REPLAY / SIMULATION ONLY. Liest eine LOKALE Sierra-.txt STREAMEND, aggregiert
/// 1-Minuten-OrderFlowBars (über <see cref="SierraMarketDataAdapter"/>) und erzeugt Trades aus einer
/// bewusst simplen, als DEMO/Pipeline-Test markierten Orderflow-Regel. KEINE Profitabilitäts-/
/// Edge-Behauptung, keine Broker-API, keine Live-Execution, keine echten Orders, kein Fake-Orderflow.
/// Lädt NIE automatisch (nur auf expliziten Aufruf) und cached das Ergebnis.
/// </summary>
public sealed class SierraBacktestReplayService
{
    // Bewusst simple Demo-Regel (NUR Pipeline-Test, keine Strategie-Empfehlung):
    private const decimal DeltaThreshold = 50m;   // "stark" positiv/negativ (Demo-Schwelle)
    private const int HoldBars = 10;              // Zeit-Exit
    private const decimal StopPoints = 3m;        // Demo-SL
    private const decimal TakeProfitPoints = 5m;  // Demo-TP
    private const decimal DollarPerPoint = 5m;    // Demo-Kontraktwert (MES-nah, keine Order)
    private const decimal FeePerTrade = 4.00m;    // Demo-Kostenannahme (kein Broker-Tarif)

    /// <summary>Standard-Pfad der großen LOKALEN Datei (bleibt außerhalb des Repos).</summary>
    public const string DefaultLocalPath = @"A:\Projects\MARKET DATA\MESM26-CME.txt";

    public string LocalPath { get; }
    public bool LocalFileAvailable => File.Exists(LocalPath);
    public string? LastError { get; private set; }

    /// <summary>Unterstützte Time-Bar-Größen (Minuten) für das Replay.</summary>
    public static readonly int[] BarSizeMinutes = { 1, 5, 15 };

    private readonly object _sync = new();
    private readonly Dictionary<int, SierraReplayResult> _cache = new();

    public SierraBacktestReplayService(string? localPath = null) => LocalPath = localPath ?? DefaultLocalPath;

    /// <summary>
    /// Baut (gecacht je Bar-Größe) das Replay-Backtest-Ergebnis aus 1/5/15-Min Time-Bars;
    /// null + LastError bei Fehler. Ticks derselben Bar-Periode werden zu EINER Candle aggregiert.
    /// </summary>
    public SierraReplayResult? TryBuild(int barMinutes = 1, long maxRows = 100_000, string symbol = "MES")
    {
        if (barMinutes <= 0) barMinutes = 1;
        lock (_sync)
        {
            if (_cache.TryGetValue(barMinutes, out var hit)) return hit;
            LastError = null;
            try
            {
                if (!File.Exists(LocalPath))
                    throw new FileNotFoundException($"Lokale Sierra-Datei nicht gefunden: {LocalPath}");

                var sw = Stopwatch.StartNew();
                var res = new SierraMarketDataAdapter().LoadFromFile(
                    LocalPath, symbol, TimeSpan.FromMinutes(barMinutes), maxRows: maxRows);
                sw.Stop();

                var built = BuildResult(res, symbol, sw.ElapsedMilliseconds);
                _cache[barMinutes] = built;
                return built;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return null;
            }
        }
    }

    /// <summary>Baut aus einem bereits geladenen Datensatz (für Tests mit synthetischen Ticks).</summary>
    public static SierraReplayResult BuildFrom(SierraMarketDataResult data, string symbol, long elapsedMs = 0)
        => BuildResult(data, symbol, elapsedMs);

    /// <summary>Mögliche Replay-Granularitäten (jeder N-te Tick ein Frame).</summary>
    public static readonly int[] FrameEveryTicksOptions = { 1, 10, 25 };

    private readonly Dictionary<(int Bars, int Frame), IntrabarReplaySession> _intrabarCache = new();

    /// <summary>
    /// Baut (gecacht) eine INTRABAR-Replay-Session: streamt die lokale Sierra-Datei, sammelt
    /// Intrabar-Frames (jeder <paramref name="frameEveryTicks"/>-te Tick) und die finalisierten Bars.
    /// </summary>
    public IntrabarReplaySession? TryBuildIntrabar(
        int barMinutes = 5, int frameEveryTicks = 25, long maxRows = 100_000, string symbol = "MES")
    {
        if (barMinutes <= 0) barMinutes = 1;
        if (frameEveryTicks <= 0) frameEveryTicks = 1;
        lock (_sync)
        {
            if (_intrabarCache.TryGetValue((barMinutes, frameEveryTicks), out var hit)) return hit;
            LastError = null;
            try
            {
                if (!File.Exists(LocalPath))
                    throw new FileNotFoundException($"Lokale Sierra-Datei nicht gefunden: {LocalPath}");

                var frames = new List<SierraIntrabarFrame>();
                var sw = Stopwatch.StartNew();
                var agg = new SierraOrderFlowBarBuilder().BuildFile(
                    LocalPath, symbol, TimeSpan.FromMinutes(barMinutes), maxRows: maxRows,
                    frameEveryTicks: frameEveryTicks, onFrame: frames.Add);
                sw.Stop();

                var built = BuildIntrabarSession(agg, frames, symbol, barMinutes, frameEveryTicks, sw.ElapsedMilliseconds);
                _intrabarCache[(barMinutes, frameEveryTicks)] = built;
                return built;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return null;
            }
        }
    }

    /// <summary>Baut eine Intrabar-Session aus Aggregations-Ergebnis + Frames (für Tests offen).</summary>
    public static IntrabarReplaySession BuildIntrabarSession(
        SierraAggregationResult agg, IReadOnlyList<SierraIntrabarFrame> frames,
        string symbol, int barMinutes, int frameEveryTicks, long elapsedMs = 0)
    {
        var bars = new List<ReplayBar>(agg.Bars.Count);
        for (int i = 0; i < agg.Bars.Count; i++)
        {
            var b = agg.Bars[i].Bar;
            bars.Add(new ReplayBar
            {
                Index = i, Time = b.OpenTime,
                Open = b.Open, High = b.High, Low = b.Low, Close = b.Close,
                Volume = b.TotalVolume, Delta = b.Delta
            });
        }

        var trades = RunDemoRule(bars);
        var equity = new decimal[bars.Count];
        decimal running = 0m; int t = 0;
        var byExit = trades.OrderBy(x => x.ExitIndex).ToList();
        for (int i = 0; i < bars.Count; i++)
        {
            while (t < byExit.Count && byExit[t].ExitIndex <= i) running += byExit[t++].NetPnL;
            equity[i] = running;
        }

        return new IntrabarReplaySession
        {
            Symbol = $"{symbol} (Sierra local)",
            BarMinutes = barMinutes,
            FrameEveryTicks = frameEveryTicks,
            CompletedBars = bars,
            Frames = frames,
            Trades = trades,
            RealizedEquityByBar = equity,
            DollarPerPoint = DollarPerPoint,
            TotalNetPnL = trades.Sum(x => x.NetPnL),
            BarsProcessed = agg.RowsProcessed,
            ParseErrors = agg.ParseErrors,
            NetDelta = agg.NetDelta,
            FinalCumulativeDelta = agg.FinalCumulativeDelta,
            From = agg.FirstBarTime,
            To = agg.LastBarTime,
            DeltaCvdAvailable = agg.Capabilities.SupportsDeltaCvd,
            ElapsedMs = elapsedMs
        };
    }

    private static SierraReplayResult BuildResult(SierraMarketDataResult data, string symbol, long elapsedMs)
    {
        var ofBars = data.Dataset.OrderFlowBars;
        var bars = new List<ReplayBar>(ofBars.Count);
        for (int i = 0; i < ofBars.Count; i++)
        {
            var b = ofBars[i];
            bars.Add(new ReplayBar
            {
                Index = i, Time = b.OpenTime,
                Open = b.Open, High = b.High, Low = b.Low, Close = b.Close,
                Volume = b.TotalVolume, Delta = b.Delta
            });
        }

        var trades = RunDemoRule(bars);

        var equity = new decimal[bars.Count];
        decimal running = 0m; int t = 0;
        var byExit = trades.OrderBy(x => x.ExitIndex).ToList();
        for (int i = 0; i < bars.Count; i++)
        {
            while (t < byExit.Count && byExit[t].ExitIndex <= i) running += byExit[t++].NetPnL;
            equity[i] = running;
        }

        var session = new ReplaySession
        {
            Symbol = $"{symbol} (Sierra local)",
            Bars = bars,
            Trades = trades,
            RealizedEquityByBar = equity,
            DollarPerPoint = DollarPerPoint,
            TotalNetPnL = trades.Sum(x => x.NetPnL)
        };

        return new SierraReplayResult
        {
            Session = session,
            BarsProcessed = data.Aggregation.RowsProcessed,
            ParseErrors = data.Aggregation.ParseErrors,
            NetDelta = data.Aggregation.NetDelta,
            FinalCumulativeDelta = data.Aggregation.FinalCumulativeDelta,
            From = data.Aggregation.FirstBarTime,
            To = data.Aggregation.LastBarTime,
            DeltaCvdAvailable = data.Dataset.Capabilities.SupportsDeltaCvd,
            ElapsedMs = elapsedMs
        };
    }

    /// <summary>Simple, klar markierte Demo-Orderflow-Regel (keine Edge-Behauptung).</summary>
    private static IReadOnlyList<ReplayTradeMarker> RunDemoRule(IReadOnlyList<ReplayBar> bars)
    {
        var trades = new List<ReplayTradeMarker>();
        int id = 1, entryIdx = -1;
        PositionSide side = PositionSide.Flat;
        decimal entry = 0m, sl = 0m, tp = 0m;

        for (int i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            if (side == PositionSide.Flat)
            {
                if (bar.Delta >= DeltaThreshold && bar.Close > bar.Open)
                    (side, entryIdx, entry) = (PositionSide.Long, i, bar.Close);
                else if (bar.Delta <= -DeltaThreshold && bar.Close < bar.Open)
                    (side, entryIdx, entry) = (PositionSide.Short, i, bar.Close);
                if (side != PositionSide.Flat)
                {
                    decimal sign = side == PositionSide.Long ? 1m : -1m;
                    sl = entry - sign * StopPoints;
                    tp = entry + sign * TakeProfitPoints;
                }
                continue; // Entry-Bar wird nicht im selben Schritt gemanagt (kein Lookahead)
            }

            decimal? exitPrice = null;
            if (side == PositionSide.Long)
            {
                if (bar.Low <= sl) exitPrice = sl;
                else if (bar.High >= tp) exitPrice = tp;
                else if (i - entryIdx >= HoldBars) exitPrice = bar.Close;
            }
            else // Short
            {
                if (bar.High >= sl) exitPrice = sl;
                else if (bar.Low <= tp) exitPrice = tp;
                else if (i - entryIdx >= HoldBars) exitPrice = bar.Close;
            }

            if (exitPrice is decimal xp)
            {
                trades.Add(MakeTrade(id++, side, entryIdx, i, entry, xp, sl, tp, bars));
                side = PositionSide.Flat; entryIdx = -1;
            }
        }

        // Offene Position am Ende zum letzten Close schließen.
        if (side != PositionSide.Flat && bars.Count > 0)
            trades.Add(MakeTrade(id, side, entryIdx, bars.Count - 1, entry, bars[^1].Close, sl, tp, bars));

        return trades;
    }

    private static ReplayTradeMarker MakeTrade(
        int id, PositionSide side, int entryIdx, int exitIdx, decimal entry, decimal exit,
        decimal sl, decimal tp, IReadOnlyList<ReplayBar> bars)
    {
        decimal sign = side == PositionSide.Long ? 1m : -1m;
        decimal net = Math.Round((exit - entry) * sign * DollarPerPoint - FeePerTrade, 2);
        return new ReplayTradeMarker
        {
            Id = id, Side = side,
            EntryIndex = entryIdx, ExitIndex = exitIdx,
            EntryTime = bars[entryIdx].Time, ExitTime = bars[exitIdx].Time,
            EntryPrice = entry, ExitPrice = exit, StopLoss = sl, TakeProfit = tp, NetPnL = net
        };
    }

    /// <summary>
    /// Findet, rein für die Replay-Visualisierung, den Bar-Fortschritt (0..1) INNERHALB der Exit-Bar,
    /// an dem die Intrabar-Ticks den Zielpreis (SL/TP) zuerst erreichen — statt den Exit-Marker immer
    /// an die Bar-Grenze (Bar-Finalisierung) zu setzen. Ändert NICHTS an der Backtest-/Signal-Logik
    /// (kein Lookahead: Exit-Bar bleibt wie von <see cref="RunDemoRule"/> ermittelt), nur die
    /// Marker-Position innerhalb dieser Bar. Fallback 1.0 (Bar-Ende), z.B. bei Zeit-Exit am Bar-Close
    /// oder wenn keine Frames für diese Bar vorliegen (sollte nicht vorkommen).
    /// </summary>
    public static double FindIntrabarExitProgress(
        IReadOnlyList<SierraIntrabarFrame> frames, int barIndex, decimal targetPrice, bool touchHigh)
    {
        foreach (var f in frames)
        {
            if (f.CompletedBars != barIndex) continue;
            bool hit = touchHigh ? f.High >= targetPrice : f.Low <= targetPrice;
            if (hit) return Math.Clamp(f.BarProgressPercent / 100.0, 0.0, 1.0);
        }
        return 1.0;
    }
}
