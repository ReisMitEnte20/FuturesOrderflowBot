using FluentAssertions;
using TradingBot.DevDashboard.Services;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.MarketData.Import;
using Xunit;

namespace TradingBot.Tests.DevDashboard;

/// <summary>
/// Phase: Sierra local backtest → Replay. Prüft die read-only Pipeline (Bars → Demo-Regel → Trades →
/// ReplaySession) mit kleinen synthetischen Sierra-Ticks. LOCAL / SIMULATION ONLY, kein Fake-Orderflow.
/// </summary>
public class SierraBacktestReplayServiceTests
{
    private const string Header =
        "Date, Time, Open, High, Low, Last, Volume, NumberOfTrades, BidVolume, AskVolume";

    private static SierraMarketDataResult Load(string csv) =>
        new SierraMarketDataAdapter().Load(new StringReader(csv), "MES", TimeSpan.FromMinutes(1));

    // Bar 23:00: stark positives Delta (+70) & Close>Open -> Long-Entry; Bar 23:01: High >= TP -> Exit.
    private const string BullishHighDelta = Header + "\n" +
        "2025/12/28, 23:00:05, 100.00,100.00,100.00,100.00, 30, 1, 0, 30\n" +
        "2025/12/28, 23:00:40, 100.50,100.50,100.50,100.50, 40, 1, 0, 40\n" +
        "2025/12/28, 23:01:20, 106.00,106.00,106.00,106.00, 10, 1, 0, 10\n";

    [Fact]
    public void Demo_rule_creates_trade_from_bars()
    {
        var r = SierraBacktestReplayService.BuildFrom(Load(BullishHighDelta), "MES");

        r.Session.Bars.Should().HaveCount(2);
        r.Session.Trades.Should().ContainSingle();
        var t = r.Session.Trades[0];
        t.Side.Should().Be(PositionSide.Long);
        t.EntryIndex.Should().BeLessThan(t.ExitIndex);
        t.NetPnL.Should().Be(21m);            // (105.50-100.50)*5 - 4
        r.Wins.Should().Be(1);
        r.DeltaCvdAvailable.Should().BeTrue();
    }

    [Fact]
    public void Replay_session_equity_is_consistent()
    {
        var s = SierraBacktestReplayService.BuildFrom(Load(BullishHighDelta), "MES").Session;

        s.RealizedEquityByBar.Should().HaveCount(s.BarCount);
        s.RealizedEquityByBar[^1].Should().Be(s.TotalNetPnL);
        s.Symbol.Should().Contain("Sierra");
    }

    [Fact]
    public void Missing_bid_ask_produces_no_fake_orderflow_signals()
    {
        var csv = "Date, Time, Last, Volume\n" +
                  "2025/12/28, 23:00:05, 100.00, 30\n" +
                  "2025/12/28, 23:01:05, 100.50, 40\n";
        var r = SierraBacktestReplayService.BuildFrom(Load(csv), "MES");

        r.DeltaCvdAvailable.Should().BeFalse();
        r.Session.Trades.Should().BeEmpty();   // ohne echtes Delta keine Signale
    }

    [Fact]
    public void TryBuild_reads_local_file_streaming()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"sierra_probe_{Guid.NewGuid():N}.txt");
        File.WriteAllText(tmp, BullishHighDelta);
        try
        {
            var svc = new SierraBacktestReplayService(tmp);
            svc.LocalFileAvailable.Should().BeTrue();
            var r = svc.TryBuild();
            r.Should().NotBeNull(svc.LastError);
            r!.Session.Trades.Should().ContainSingle();
        }
        finally { File.Delete(tmp); }
    }

    // Mehrere Ticks je Minute -> 1 Candle; 5-Min bündelt mehr Ticks -> weniger Bars als 1-Min.
    private const string ThreeMinutesManyTicks =
        "Date, Time, Open, High, Low, Last, Volume, NumberOfTrades, BidVolume, AskVolume\n" +
        "2025/12/28, 23:00:05, 100.00,100,100,100.00, 3, 1, 0, 3\n" +
        "2025/12/28, 23:00:40, 100.50,100,100,100.50, 2, 1, 0, 2\n" +   // selbe Minute
        "2025/12/28, 23:01:10, 100.25,100,100,100.25, 4, 1, 4, 0\n" +
        "2025/12/28, 23:04:30, 100.75,100,100,100.75, 1, 1, 0, 1\n" +   // eigene Minute
        "2025/12/28, 23:06:00, 101.00,100,100,101.00, 1, 1, 0, 1\n";

    [Fact]
    public void One_minute_aggregates_same_minute_ticks_into_one_bar()
    {
        var bars = SierraBacktestReplayService.BuildFrom(Load(ThreeMinutesManyTicks), "MES").Session.Bars;

        bars.Should().HaveCount(4);                 // Minuten 23:00, 23:01, 23:04, 23:06
        bars[0].Open.Should().Be(100.00m);
        bars[0].Close.Should().Be(100.50m);         // letzter Trade der Minute
        bars[0].Volume.Should().Be(5m);            // 3 + 2 (beide Ticks der Minute)
        bars[0].Delta.Should().Be(5m);             // Ask 5 - Bid 0
    }

    [Fact]
    public void Five_minute_has_fewer_bars_than_one_minute()
    {
        var oneMin = new SierraMarketDataAdapter().Load(new StringReader(ThreeMinutesManyTicks), "MES", TimeSpan.FromMinutes(1));
        var fiveMin = new SierraMarketDataAdapter().Load(new StringReader(ThreeMinutesManyTicks), "MES", TimeSpan.FromMinutes(5));

        var b1 = SierraBacktestReplayService.BuildFrom(oneMin, "MES").Session.Bars;
        var b5 = SierraBacktestReplayService.BuildFrom(fiveMin, "MES").Session.Bars;

        b5.Count.Should().BeLessThan(b1.Count);     // 5-Min bündelt stärker
        b1.Count.Should().Be(4);
        b5.Count.Should().Be(2);                    // [23:00–23:05) und [23:05–23:10)
    }

    [Fact]
    public void Play_index_is_bar_based_not_tick_based()
    {
        var s = SierraBacktestReplayService.BuildFrom(Load(ThreeMinutesManyTicks), "MES").Session;
        s.BarCount.Should().Be(4);                  // 4 Bars, nicht 5 Ticks
        s.RealizedEquityByBar.Should().HaveCount(4);
    }

    [Fact]
    public void Missing_local_file_sets_error()
    {
        var svc = new SierraBacktestReplayService(@"A:\nope\does-not-exist.txt");
        svc.LocalFileAvailable.Should().BeFalse();
        svc.TryBuild().Should().BeNull();
        svc.LastError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Intrabar_session_has_more_frames_than_completed_bars()
    {
        var csv =
            "Date, Time, Open, High, Low, Last, Volume, NumberOfTrades, BidVolume, AskVolume\n" +
            "2025/12/28, 23:00:10, 100.00,0,0,100.00, 1, 1, 0, 1\n" +
            "2025/12/28, 23:01:00, 101.00,0,0,101.00, 1, 1, 0, 1\n" +
            "2025/12/28, 23:02:00, 99.00,0,0,99.00, 1, 1, 1, 0\n" +
            "2025/12/28, 23:06:00, 102.00,0,0,102.00, 1, 1, 0, 1\n";

        var frames = new System.Collections.Generic.List<SierraIntrabarFrame>();
        var agg = new SierraOrderFlowBarBuilder().Build(
            new StringReader(csv), "MES", TimeSpan.FromMinutes(5), frameEveryTicks: 1, onFrame: frames.Add);

        var s = SierraBacktestReplayService.BuildIntrabarSession(agg, frames, "MES", 5, 1);

        s.CompletedBars.Should().HaveCount(2);
        s.FrameCount.Should().Be(4);                          // Replay-Index über Frames, nicht nur Bars
        s.FrameCount.Should().BeGreaterThan(s.CompletedBars.Count);
        s.RealizedEquityByBar.Should().HaveCount(s.CompletedBars.Count);
        s.DeltaCvdAvailable.Should().BeTrue();
    }

    [Fact]
    public void Dashboard_has_no_execution_reference()
    {
        var referenced = typeof(SierraBacktestReplayService).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!).ToList();
        referenced.Should().NotContain(n => n.Contains("TradingBot.Execution"));
    }

    // --- Intrabar-Exit-Marker: Sichtbarkeit über die REALE Replay-Reihenfolge (Touch-Frame) ---
    private static ReplayTradeMarker ExitTrade(int exitIndex, decimal exitPrice, decimal sl, decimal tp,
        PositionSide side = PositionSide.Long, int entryIndex = 0) => new()
        {
            Id = 1, Side = side, EntryIndex = entryIndex, ExitIndex = exitIndex,
            EntryPrice = 100m, ExitPrice = exitPrice, StopLoss = sl, TakeProfit = tp, NetPnL = 0m
        };

    // Synthetisches Frame: <paramref name="price"/> ist das Kursereignis DIESES Frames (CurrentPrice,
    // maßgeblich für die Touch-Erkennung); high/low sind das laufende (kumulierte) Kerzen-Extrem und
    // entsprechen ohne Angabe dem Preis. So lässt sich der Fall "alter Touch bleibt im High hängen"
    // (High >= Level, aber CurrentPrice darunter) explizit nachbilden.
    private static SierraIntrabarFrame FR(int completedBars, decimal price, decimal? high = null, decimal? low = null) => new()
    {
        CompletedBars = completedBars, CurrentPrice = price,
        High = high ?? price, Low = low ?? price, Open = low ?? price, Close = price,
        Volume = 0m, BidVolume = 0m, AskVolume = 0m, CumulativeDelta = 0m, BarProgressPercent = 0
    };

    [Fact]
    public void FindExitFrame_ignores_level_touched_before_exit_bar()
    {
        // Long TP @105. In Bar 0 wird 105 schon berührt -> darf NICHT zählen; Exit-Bar = 2.
        var t = ExitTrade(exitIndex: 2, exitPrice: 105m, sl: 97m, tp: 105m);
        var frames = new List<SierraIntrabarFrame>
        {
            FR(0, price: 106m),   // 0: Level früh berührt, aber vor Exit-Bar -> ignorieren
            FR(1, price: 103m),   // 1
            FR(2, price: 104m),   // 2: Exit-Bar, noch kein Touch
            FR(2, price: 105m),   // 3: Exit-Bar, Touch (Preis>=105)
        };
        SierraBacktestReplayService.FindExitFrameIndex(frames, t).Should().Be(3);
    }

    [Fact]
    public void FindExitFrame_same_candle_respects_entry_frame()
    {
        // Entry & Exit in derselben Kerze (Bar 0). 105 wird VOR dem Einstieg (Frame 0) berührt;
        // das laufende High bleibt danach kumuliert bei 105.
        var t = ExitTrade(exitIndex: 0, exitPrice: 105m, sl: 97m, tp: 105m, entryIndex: 0);
        var frames = new List<SierraIntrabarFrame>
        {
            FR(0, price: 105m, high: 105m, low: 100m),   // 0: Touch VOR Einstieg
            FR(0, price: 101m, high: 105m, low: 100m),   // 1: (Einstieg hier bestätigt), High bleibt 105
            FR(0, price: 105m, high: 105m, low: 100m),   // 2: Kurs-Touch NACH Einstieg
        };
        // ohne Entry-Grenze zählt der (frühe) Kurs-Touch bei Frame 0:
        SierraBacktestReplayService.FindExitFrameIndex(frames, t, entryFrameIndex: 0).Should().Be(0);
        // mit Entry-Grenze (ab Frame 2) zählt nur der Kurs-Touch nach Einstieg:
        SierraBacktestReplayService.FindExitFrameIndex(frames, t, entryFrameIndex: 2).Should().Be(2);
    }

    [Fact]
    public void FindExitFrame_retouch_after_entry_ignores_pre_entry_touch_in_running_high()
    {
        // Regression: Innerhalb DERSELBEN Kerze (Exit-Bar 0) wird zuerst das TP-Level (105) berührt,
        // danach erfolgt der Einstieg (Frame 1). Nach dem Einstieg fällt der Kurs (kein Re-Touch),
        // erst Frame 3 erreicht 105 erneut. Der Marker darf erst ab Frame 3 sichtbar werden.
        // Das laufende (kumulierte) High enthält den ALTEN Touch weiterhin (bleibt 105) -> die Suche
        // erst ab entryFrameIndex zu starten reicht allein NICHT; maßgeblich ist der Frame-Kurs.
        var t = ExitTrade(exitIndex: 0, exitPrice: 105m, sl: 97m, tp: 105m, entryIndex: 0);
        var frames = new List<SierraIntrabarFrame>
        {
            FR(0, price: 105m, high: 105m, low: 100m),   // 0: TP-Touch VOR Einstieg
            FR(0, price: 101m, high: 105m, low: 100m),   // 1: Einstieg; High bleibt kumuliert 105
            FR(0, price: 102m, high: 105m, low: 100m),   // 2: kein Re-Touch (Kurs < 105)
            FR(0, price: 105m, high: 105m, low: 100m),   // 3: Re-Touch NACH Einstieg
        };
        // Frame-Kurs-basiert: 105 erst wieder bei Frame 3 (nicht schon am Einstiegs-Frame 1).
        SierraBacktestReplayService.FindExitFrameIndex(frames, t, entryFrameIndex: 1).Should().Be(3);
    }

    [Fact]
    public void FindExitFrame_uses_recorded_level_when_sl_and_tp_in_same_candle()
    {
        // Long, recorded Exit = SL @97 (obwohl TP 110 in derselben Kerze theoretisch möglich wäre).
        var t = ExitTrade(exitIndex: 1, exitPrice: 97m, sl: 97m, tp: 110m);
        var frames = new List<SierraIntrabarFrame>
        {
            FR(1, price: 99m, high: 105m, low: 99m),   // 0: kein SL-Touch (Kurs 99 > 97)
            FR(1, price: 96m, high: 108m, low: 96m),   // 1: SL-Touch (Kurs<=97); TP 110 NICHT erreicht
        };
        SierraBacktestReplayService.FindExitFrameIndex(frames, t).Should().Be(1);   // SL-Level maßgeblich
    }

    [Fact]
    public void FindExitFrame_returns_minus_one_for_time_exit()
    {
        var t = ExitTrade(exitIndex: 1, exitPrice: 102m, sl: 97m, tp: 110m);  // weder SL noch TP
        var frames = new List<SierraIntrabarFrame> { FR(1, price: 102m, high: 105m, low: 100m) };
        SierraBacktestReplayService.FindExitFrameIndex(frames, t).Should().Be(-1);
    }

    [Fact]
    public void ExitVisible_is_monotonic_and_rewind_correct()
    {
        // vor dem Touch-Frame unsichtbar, ab dem Touch-Frame sichtbar, Rewind -> wieder unsichtbar.
        SierraBacktestReplayService.ExitVisibleAtFrame(currentFrameIndex: 4, exitFrameIndex: 5).Should().BeFalse();
        SierraBacktestReplayService.ExitVisibleAtFrame(5, 5).Should().BeTrue();
        SierraBacktestReplayService.ExitVisibleAtFrame(9, 5).Should().BeTrue();
        SierraBacktestReplayService.ExitVisibleAtFrame(3, 5).Should().BeFalse();   // zurückgespult
        SierraBacktestReplayService.ExitVisibleAtFrame(9, -1).Should().BeFalse();  // Zeit-Exit -> nie über Frame
    }
}
