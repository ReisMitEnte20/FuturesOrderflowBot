using FluentAssertions;
using TradingBot.Application.Sessions;
using TradingBot.Application.Strategies.OrderFlow;
using TradingBot.Backtesting;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Tests.Backtesting;
using Xunit;

namespace TradingBot.Tests.Sessions;

/// <summary>
/// „London + New York“: Sessionplan (Sommerzeit), Session-Gate an der ECHTEN BacktestEngine – Sprung über die Pause ohne
/// PnL/Trade, Close-Out-Regel mit dem vorhandenen Fillmodell, Tageslimit über die Session hinweg, Teilbar/CVD-Reset und
/// Ausweis eines Übertrags, wenn vor der Grenze kein Fill möglich ist. Alle Daten synthetisch (keine Marktdaten).
/// </summary>
public sealed class LondonNewYorkSessionTests
{
    private static readonly IReadOnlyList<SessionDefinition> Defs = new[]
    {
        new SessionDefinition("London", "London", "Europe/London", new TimeOnly(8, 0), new TimeOnly(11, 0)),
        new SessionDefinition("NewYork", "New York", "America/New_York", new TimeOnly(9, 30), new TimeOnly(12, 0)),
    };
    private static readonly TradingSessionSchedule Schedule = new(Defs, TimeSpan.FromMinutes(5));

    // ------------------------------------------------------------------ Sessionplan / Sommerzeit

    [Theory]
    [InlineData("2026-01-15", "08:00", "11:00", "14:30", "17:00", 210)] // Winter: GMT / EST
    [InlineData("2026-03-20", "08:00", "11:00", "13:30", "16:00", 150)] // USA schon Sommerzeit, UK noch nicht
    [InlineData("2026-03-31", "07:00", "10:00", "13:30", "16:00", 210)] // beide Sommerzeit
    [InlineData("2026-10-30", "08:00", "11:00", "13:30", "16:00", 150)] // UK schon zurück, USA noch Sommerzeit
    public void Utc_windows_and_pause_follow_both_daylight_saving_rules(string day, string ls, string le, string ns, string ne, int pauseMin)
    {
        var w = Schedule.WindowsFor(DateOnly.Parse(day));
        w.Should().HaveCount(2);
        w[0].Key.Should().Be("London");
        w[0].StartUtc.ToString("HH:mm").Should().Be(ls);
        w[0].EndUtc.ToString("HH:mm").Should().Be(le);
        w[1].StartUtc.ToString("HH:mm").Should().Be(ns);
        w[1].EndUtc.ToString("HH:mm").Should().Be(ne);
        (w[1].StartUtc - w[0].EndUtc).TotalMinutes.Should().Be(pauseMin, "die Pause ist nicht fest, sondern folgt den Zeitumstellungen");
        w[0].CloseOutUtc.Should().Be(w[0].EndUtc.AddMinutes(-5));
    }

    [Fact]
    public void Rule_blocks_new_entries_during_the_real_pause_and_after_close_out()
    {
        var pause = Schedule.StateAt(SessionFixtures.Utc("2026-03-20T12:00:00Z"));
        pause.Phase.Should().Be(SessionPhase.Pause);
        pause.NewEntriesAllowed.Should().BeFalse();
        pause.Next!.Key.Should().Be("NewYork");
        Schedule.StateAt(SessionFixtures.Utc("2026-03-20T09:00:00Z")).NewEntriesAllowed.Should().BeTrue();
        var co = Schedule.StateAt(SessionFixtures.Utc("2026-03-20T10:57:00Z"));
        co.Phase.Should().Be(SessionPhase.CloseOut);
        co.NewEntriesAllowed.Should().BeFalse();
        Schedule.WindowsFor(DateOnly.Parse("2026-03-21")).Should().BeEmpty("Samstag");
    }

    // ------------------------------------------------------------------ Gate an der echten Engine

    private static readonly DateTimeOffset LStart = SessionFixtures.Utc("2026-03-20T08:00:00Z"), LEnd = SessionFixtures.Utc("2026-03-20T11:00:00Z");
    private static readonly DateTimeOffset NStart = SessionFixtures.Utc("2026-03-20T13:30:00Z"), NEnd = SessionFixtures.Utc("2026-03-20T16:00:00Z");

    private static IReadOnlyList<SessionSegment> Segments() => new[]
    {
        new SessionSegment("L", "London", "London", new DateOnly(2026, 3, 20), LStart, LEnd, LEnd.AddMinutes(-5)),
        new SessionSegment("N", "NewYork", "New York", new DateOnly(2026, 3, 20), NStart, NEnd, NEnd.AddMinutes(-5)),
    };

    /// <summary>Ein Tick je Minute (hh:mm:30) im Fenster, Preis = f(Zeit), Aggressor wählbar.</summary>
    private static List<MarketTick> Minutes(DateTimeOffset from, DateTimeOffset toExclusive, Func<int, decimal> price, AggressorSide side)
    {
        var l = new List<MarketTick>();
        int i = 0;
        for (var t = from.AddSeconds(30); t < toExclusive; t = t.AddMinutes(1), i++)
            l.Add(new MarketTick { Symbol = "NQ", Timestamp = t, Price = price(i), Volume = 1, Aggressor = side });
        return l;
    }

    private sealed class ScriptByTime : IStrategy
    {
        private readonly Dictionary<DateTimeOffset, SignalDirection> _s;
        public ScriptByTime(Dictionary<DateTimeOffset, SignalDirection> s) => _s = s;
        public string Name => "Scripted";
        public TradeSignal? OnTick(MarketTick tick) => _s.TryGetValue(tick.Timestamp, out var d)
            ? new TradeSignal { StrategyName = Name, Symbol = tick.Symbol, Direction = d, Timestamp = tick.Timestamp, ReferencePrice = tick.Price, SuggestedQuantity = 1 }
            : null;
    }

    private static DateTimeOffset At(string hhmmss) => SessionFixtures.Utc("2026-03-20T" + hhmmss + "Z");

    /// <summary>Lauf wie im SessionRunService: Session-Gate + Sessiongrenzen-Guard der Engine.</summary>
    private static async Task<(BacktestResult Res, SessionGateStrategy Gate)> RunGuarded(IStrategy inner, List<MarketTick> ticks, RiskConfig? risk = null)
    {
        var gate = new SessionGateStrategy(inner, Segments());
        var res = await new BacktestEngine().RunAsync(BacktestTestData.Request(gate, ticks, risk: risk) with
        {
            TickGuard = c => gate.BeforeTick(c.NextTick.Timestamp, c.PositionQuantity, c.PendingOrders)
        });
        return (res, gate);
    }

    private static async Task<(BacktestResult Res, SessionGateStrategy Gate)> Run(IStrategy inner, List<MarketTick> ticks, RiskConfig? risk = null)
    {
        var (res, gate) = await RunGuarded(inner, ticks, risk);
        res.Status.Should().Be(BacktestRunStatus.Completed, res.Message);
        return (res, gate);
    }

    [Fact]
    public async Task Jump_over_the_pause_creates_neither_pnl_nor_a_trade_and_close_out_uses_the_fill_model_before_the_end()
    {
        var ticks = Minutes(LStart, LEnd, i => 20000m + i, AggressorSide.Buy);
        ticks.AddRange(Minutes(NStart, NEnd, i => 20300m - i, AggressorSide.Sell));
        var strat = new ScriptByTime(new()
        {
            [At("08:10:30")] = SignalDirection.Long, [At("09:00:30")] = SignalDirection.Flat,
            [At("10:00:30")] = SignalDirection.Long,               // bleibt offen → Session-Close-Regel
            [At("10:57:30")] = SignalDirection.Short,              // nach Close-Out: unterdrückt
            [At("14:00:30")] = SignalDirection.Short, [At("14:30:30")] = SignalDirection.Flat,
        });
        var (res, gate) = await Run(strat, ticks);

        res.Trades.Should().HaveCount(3);
        var london = gate.Observations[0];
        london.SessionCloseSignalUtc.Should().Be(At("10:55:30"), "erster Tick ab Sessionende − 5 min (vorab festgelegt, kein Wissen über den letzten Tick)");
        res.Trades[1].ExitTime.Should().Be(At("10:56:30"), "Fill am nächsten Tick über das vorhandene Fillmodell");
        res.Trades[1].ExitTime.Should().BeBefore(LEnd);
        london.EntrySignalsSuppressedAfterCloseOut.Should().Be(1);
        res.Trades.Should().NotContain(t => t.EntryTime > At("10:55:00") && t.EntryTime < NStart);

        var lastLondon = london.LastTickUtc!.Value; var firstNy = gate.Observations[1].FirstTickUtc!.Value;
        lastLondon.Should().Be(At("10:59:30"));
        firstNy.Should().Be(At("13:30:30"));
        (firstNy - lastLondon).Should().Be(TimeSpan.FromMinutes(151), "tatsächliche Pause zwischen letztem London- und erstem NY-Tick");
        london.PositionAtLastTick.Should().Be("Flat");
        res.Trades.Should().NotContain(t => (t.ExitTime > lastLondon && t.ExitTime <= firstNy) || (t.EntryTime > lastLondon && t.EntryTime <= firstNy),
            "der Sprung selbst erzeugt keinen Trade und damit kein PnL");
        decimal Bal(DateTimeOffset t) => res.Trades.Where(x => x.ExitTime <= t).Sum(x => x.NetPnL);
        Bal(firstNy).Should().Be(Bal(lastLondon));
        res.Trades[2].EntryTime.Should().BeAfter(NStart);
        res.FinalPositionSide.Should().Be(PositionSide.Flat);
        gate.Observations[1].Incidents.Should().BeEmpty();
    }

    [Fact]
    public async Task Daily_loss_limit_hit_in_London_blocks_new_entries_in_New_York_that_day()
    {
        var ticks = Minutes(LStart, LEnd, i => 20500m - i, AggressorSide.Sell);   // fallend → Long verliert
        ticks.AddRange(Minutes(NStart, NEnd, i => 20000m + i, AggressorSide.Buy));
        var strat = new ScriptByTime(new()
        {
            [At("08:10:30")] = SignalDirection.Long, [At("09:00:30")] = SignalDirection.Flat, // −50 Punkte × 20 USD
            [At("14:00:30")] = SignalDirection.Long, [At("14:30:30")] = SignalDirection.Flat,
        });
        var (res, gate) = await Run(strat, ticks, BacktestTestData.Risk(maxDailyLoss: 500m));

        res.Trades.Should().ContainSingle("nur der London-Trade; New York darf an diesem Tag nicht mehr einsteigen");
        res.Trades[0].NetPnL.Should().BeLessThan(-500m);
        res.OrdersRejectedByRisk.Should().BeGreaterThanOrEqualTo(1);
        gate.Observations[1].EntrySignalsPassed.Should().Be(1, "das Signal kam an, der RiskManager lehnte es ab");
    }

    private sealed class BarRecorder : IOrderFlowStrategy
    {
        public List<OrderFlowBar> Bars { get; } = new();
        public string Name => "BarRecorder";
        public StrategyDataRequirements DataRequirements => new() { NeedsOrderFlowBars = true };
        public TradeSignal? OnOrderFlowBar(OrderFlowBar bar) { Bars.Add(bar); return null; }
    }

    [Fact]
    public async Task New_session_discards_the_partial_bar_and_restarts_session_cvd_and_vwap()
    {
        var ticks = Minutes(LStart, LEnd, i => 20000m, AggressorSide.Buy);       // 180 Ticks
        ticks.AddRange(Minutes(NStart, NEnd, i => 21000m, AggressorSide.Sell));  // 150 Ticks
        var rec = new BarRecorder();
        var (_, gate) = await Run(new OrderFlowBarAggregatorStrategy(rec, ticksPerBar: 7), ticks);

        gate.Observations[1].DiscardedPartialBarTicksAtStart.Should().Be(180 % 7);
        var firstNy = rec.Bars.First(b => b.OpenTime >= NStart);
        firstNy.OpenTime.Should().Be(At("13:30:30"), "keine Bar spannt über die Pause");
        rec.Bars.Should().NotContain(b => b.OpenTime < LEnd && b.CloseTime >= NStart);
        firstNy.CumulativeDelta.Should().Be(firstNy.Delta, "Session-CVD startet in New York neu");
        gate.Observations[0].SessionCvd.Should().Be(180);
        gate.Observations[1].SessionCvd.Should().Be(-150);
        gate.Observations[0].SessionVwap.Should().Be(20000m);
        gate.Observations[1].SessionVwap.Should().Be(21000m);
    }

    [Fact]
    public async Task Open_position_without_any_tick_after_close_out_stops_the_run_before_the_first_new_york_tick()
    {
        var ticks = Minutes(LStart, LEnd.AddMinutes(-10), i => 20000m + i, AggressorSide.Buy); // London-Daten enden vor der Close-Out-Grenze
        ticks.AddRange(Minutes(NStart, NEnd, i => 20100m, AggressorSide.Buy));
        var strat = new ScriptByTime(new() { [At("10:00:30")] = SignalDirection.Long });
        var (res, gate) = await RunGuarded(strat, ticks);

        res.Status.Should().Be(BacktestRunStatus.Stopped);
        res.StopReason.Should().Be(SessionGateStrategy.NotFlattenableReason);
        res.StoppedBeforeTickUtc.Should().Be(At("13:30:30"), "gestoppt VOR dem ersten New-York-Tick");
        gate.Observations.Should().ContainSingle("New York wurde nicht gestartet");
        gate.Observations[0].CloseOutReachedUtc.Should().BeNull("kein Tick ab der Grenze – kein zulässiger Fill vor dem Sessionende möglich");
        res.Trades.Should().BeEmpty("weder rückwirkender Fill auf dem letzten London-Tick noch Glattstellung nach der Pause");
        res.FinalPositionSide.Should().Be(PositionSide.Long);
        res.LastTickUtc.Should().Be(At("10:49:30"));
        res.LastTickPrice.Should().Be(20000m + 169);
        // Offene Bewertung zum letzten beobachteten Preis – getrennt vom (leeren) realisierten Ergebnis.
        res.FinalPositionUnrealizedAtLastTick.Should().Be((res.LastTickPrice!.Value - res.FinalPositionAvgPrice) * 20m);
        gate.BoundaryStop!.Segment.Id.Should().Be("L");
        gate.BoundaryStop.LastTickPrice.Should().Be(20169m);
    }

    /// <summary>London: Long 09:00, Flat 09:30 (geschlossen), Long 10:00 bleibt offen; London-Ticks enden genau mit dem Close-Out-Tick
    /// 10:55:30 → Flat-Signal dort, aber KEIN ausführbarer Tick mehr vor 11:00.</summary>
    private static List<MarketTick> FlattenWithoutFollowTick(decimal firstNewYorkPrice)
    {
        var ticks = Minutes(LStart, LEnd.AddMinutes(-4), i => 20000m + i, AggressorSide.Buy); // letzter Tick 10:55:30
        var ny = Minutes(NStart, NEnd, i => 20100m - i, AggressorSide.Sell);
        ny[0] = ny[0] with { Price = firstNewYorkPrice };
        ticks.AddRange(ny);
        return ticks;
    }

    private static ScriptByTime LondonScript() => new(new()
    {
        [At("09:00:30")] = SignalDirection.Long, [At("09:30:30")] = SignalDirection.Flat, [At("10:00:30")] = SignalDirection.Long,
        [At("13:35:30")] = SignalDirection.Short,
    });

    [Fact]
    public async Task Flatten_signal_on_the_last_london_tick_is_not_filled_after_the_pause_and_the_order_does_not_reach_new_york()
    {
        var (res, gate) = await RunGuarded(LondonScript(), FlattenWithoutFollowTick(20100m));

        res.Status.Should().Be(BacktestRunStatus.Stopped);
        res.StopReason.Should().Be(SessionGateStrategy.NotFlattenableReason);
        gate.Observations[0].SessionCloseSignalUtc.Should().Be(At("10:55:30"));
        res.OrdersUnfilledAtEnd.Should().Be(1, "die Glattstellungs-Order bleibt offen – sie wird NICHT am ersten New-York-Tick gefüllt");
        gate.BoundaryStop!.PendingOrders.Should().Be(1);
        res.Trades.Should().ContainSingle("nur der vor der Close-Out-Grenze geschlossene London-Trade");
        res.Trades.Should().OnlyContain(t => t.ExitTime < LEnd);
        res.FinalPositionSide.Should().Be(PositionSide.Long);
        res.LastTickUtc.Should().Be(At("10:55:30"));
        gate.Observations.Should().ContainSingle();
        gate.Observations[0].Incidents.Should().ContainSingle(i => i.Contains("Position vor Sessionende nicht glattstellbar"));
    }

    [Fact]
    public async Task Regression_changing_only_the_first_new_york_price_does_not_change_the_stopped_london_result()
    {
        var (a, _) = await RunGuarded(LondonScript(), FlattenWithoutFollowTick(20100m));
        var (b, _) = await RunGuarded(LondonScript(), FlattenWithoutFollowTick(5_000m)); // drastisch anderer erster NY-Preis

        b.Status.Should().Be(BacktestRunStatus.Stopped);
        b.Trades.Should().BeEquivalentTo(a.Trades, o => o.WithStrictOrdering());
        b.Trades.Sum(t => t.NetPnL).Should().Be(a.Trades.Sum(t => t.NetPnL));
        b.Trades.Sum(t => t.Fees).Should().Be(a.Trades.Sum(t => t.Fees));
        b.LastTickPrice.Should().Be(a.LastTickPrice);
        b.FinalPositionUnrealizedAtLastTick.Should().Be(a.FinalPositionUnrealizedAtLastTick);
        b.FinalPositionAvgPrice.Should().Be(a.FinalPositionAvgPrice);
        b.TicksProcessed.Should().Be(a.TicksProcessed);
        b.StopReason.Should().Be(a.StopReason);
        a.Trades.Should().NotContain(t => t.ExitPrice == 20100m || t.ExitPrice == 5_000m, "kein Fill zum New-York-Preis");
    }

    [Fact]
    public async Task Without_the_boundary_guard_a_carried_position_fails_the_run_closed_instead_of_flattening_after_the_pause()
    {
        var gate = new SessionGateStrategy(LondonScript(), Segments());
        var res = await new BacktestEngine().RunAsync(BacktestTestData.Request(gate, FlattenWithoutFollowTick(20100m)));
        res.Status.Should().Be(BacktestRunStatus.Failed);
        res.Message.Should().Contain("über die Pause");
    }

    [Fact]
    public async Task Flat_account_at_the_london_end_still_jumps_to_new_york_with_the_guard_active()
    {
        var ticks = Minutes(LStart, LEnd, i => 20000m + i, AggressorSide.Buy);
        ticks.AddRange(Minutes(NStart, NEnd, i => 20300m - i, AggressorSide.Sell));
        var strat = new ScriptByTime(new() { [At("09:00:30")] = SignalDirection.Long, [At("09:30:30")] = SignalDirection.Flat,
            [At("14:00:30")] = SignalDirection.Short, [At("14:30:30")] = SignalDirection.Flat });
        var (res, gate) = await Run(strat, ticks);

        gate.BoundaryStop.Should().BeNull();
        gate.Observations.Should().HaveCount(2);
        res.Trades.Should().HaveCount(2);
        res.StopReason.Should().BeNull();
        res.TradeLinks.Should().HaveCount(2);
        res.Orders.Should().HaveCount(4);
        res.TradeLinks.Select(l => l.EntryFillId).Should().OnlyHaveUniqueItems().And.NotIntersectWith(res.TradeLinks.Select(l => l.ExitFillId));
    }

    [Fact]
    public void Overlapping_segments_are_rejected_so_no_market_event_runs_twice()
    {
        var s = Segments();
        var bad = new[] { s[0], s[1] with { StartUtc = LEnd.AddMinutes(-1) } };
        FluentActions.Invoking(() => new SessionGateStrategy(new ScriptByTime(new()), bad)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Tick_outside_any_planned_window_fails_the_run_closed()
    {
        var ticks = Minutes(LStart, LEnd, i => 20000m, AggressorSide.Buy);
        ticks.Add(new MarketTick { Symbol = "NQ", Timestamp = At("12:00:00"), Price = 20000m, Volume = 1, Aggressor = AggressorSide.Buy });
        var gate = new SessionGateStrategy(new ScriptByTime(new()), Segments());
        var res = await new BacktestEngine().RunAsync(BacktestTestData.Request(gate, ticks));
        res.Status.Should().Be(BacktestRunStatus.Failed);
        res.Message.Should().Contain("keinem geplanten Sessionfenster");
    }
}
