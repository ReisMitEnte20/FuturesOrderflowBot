using FluentAssertions;
using TradingBot.Application.MarketState;
using TradingBot.Backtesting;
using TradingBot.DevDashboard.Services;
using TradingBot.DevDashboard.Services.Mtf;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Tests.Backtesting;
using TradingBot.Tests.Sessions;
using Xunit;

namespace TradingBot.Tests.MarketState;

/// <summary>Multi-Timeframe-Zustand (5m/1m/rangeXV) aus EINER Ereignisfolge – zeitliche Korrektheit und Abnahmen A–C.
/// Nur synthetische Daten.</summary>
public sealed class MultiTimeframeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 15, 14, 30, 0, TimeSpan.Zero);
    private static MarketTick Tk(double sec, decimal p, decimal v = 1, AggressorSide s = AggressorSide.Buy) =>
        new() { Symbol = "NQ", Timestamp = T0.AddSeconds(sec), Price = p, Volume = v, Aggressor = s };

    // ---------------------------------------------------------------- Bar-Semantik

    [Fact]
    public void Open_5m_bar_shows_values_so_far_and_is_closed_only_by_the_first_event_after_its_end()
    {
        var b = new TimeBarBuilder("5m", TimeSpan.FromMinutes(5));
        b.OnEvent(1, Tk(10, 100)); b.OnEvent(2, Tk(200, 103)); b.OnEvent(3, Tk(267, 101, 2, AggressorSide.Sell)); // 09:34:27
        var cur = b.Current!;
        (cur.Status, cur.High, cur.Volume, cur.Delta).Should().Be((BarStatus.Offen, 103m, 4m, 0m), "um 09:34:27 sind nur die bisherigen Werte sichtbar");
        b.LastClosed.Should().BeNull();
        b.OnEvent(4, Tk(300.5, 99));                    // 09:35:00.5 – erst jetzt ist die Bar 09:30–09:35 geschlossen
        b.LastClosed!.ClosedAtEvent.Should().Be(4);
        b.LastClosed.Close.Should().Be(101m, "das Ereignis 4 gehört schon zur neuen Bar");
        b.Current!.Open.Should().Be(99m);
    }

    [Fact]
    public void RangeXV_provisional_rule_closes_when_the_range_would_be_exceeded_and_the_trigger_trade_opens_the_next_bar()
    {
        var r = new RangeXvBarBuilder("rangeXV 4", 4, 0.25m);   // Spanne 1,00
        r.OnEvent(1, Tk(1, 100)); r.OnEvent(2, Tk(2, 100.75m)); r.OnEvent(3, Tk(3, 100.25m)); r.OnEvent(4, Tk(4, 101m));
        r.LastClosed.Should().BeNull("Spanne 100–101 = 1,00 ist noch erlaubt");
        r.OnEvent(5, Tk(5, 103m));                          // Preissprung
        r.LastClosed!.High.Should().Be(101m);
        r.Current!.Open.Should().Be(103m, "der auslösende Trade eröffnet die neue Bar zu seinem Preis – keine erfundenen Zwischenbars");
        r.All.Should().HaveCount(2);
        RangeXvBarBuilder.SemanticsNote.Should().Contain("vorläufig");
    }

    [Fact]
    public void Swing_extreme_time_and_later_confirmation_are_kept_apart()
    {
        var ctx = new SessionContext(0.25m, swingStrength: 2);
        var b = new TimeBarBuilder("5m", TimeSpan.FromMinutes(5));
        decimal[] highs = { 100, 101, 105, 102, 101, 100 };
        long ev = 0;
        for (int i = 0; i < highs.Length; i++)
        {
            ev++; b.OnEvent(ev, Tk(i * 300 + 1, highs[i]));
            if (b.ClosedOnLastEvent) ctx.OnBarClosed(b.All, b.LastClosed!);
            ctx.OnEvent(Tk(i * 300 + 1, highs[i]));
            if (i < 5) ctx.Values().LastSwingHigh.Should().BeNull($"vor dem Abschluss von Bar {i} ist das Hoch der Bar 2 noch nicht bestätigt");
        }
        var sw = ctx.Values().LastSwingHigh!;
        (sw.Price, sw.ExtremeBarIndex, sw.ExtremeBarStart).Should().Be((105m, 2, T0.AddMinutes(10)));
        sw.ConfirmedAtEvent.Should().Be(6, "bestätigt erst, als Bar 4 (2 Bars später) geschlossen bekannt wurde");
    }

    [Fact]
    public void Developing_profiles_poc_value_area_and_tpo_are_separate()
    {
        var vol = SessionContext.Profile(new Dictionary<decimal, decimal> { [100] = 10, [100.25m] = 50, [100.5m] = 20, [100.75m] = 5, [101] = 15 });
        (vol.Poc, vol.Vah, vol.Val).Should().Be((100.25m, 100.5m, 100.25m), "POC 50 + größere Nachbarstufe 20 = 70 % – Value Area erreicht");
        var ctx = new SessionContext(0.25m, tpoPeriod: TimeSpan.FromMinutes(30));
        ctx.Reset(T0);
        ctx.OnEvent(Tk(1, 100, 100)); ctx.OnEvent(Tk(2, 101, 1)); ctx.OnEvent(Tk(1801, 101, 1)); ctx.OnEvent(Tk(3601, 101, 1));
        var v = ctx.Values();
        v.VolumeProfile.Poc.Should().Be(100m, "Volumen-POC: größtes Volumen");
        v.TpoProfile.Poc.Should().Be(101m, "TPO-POC: in den meisten 30-min-Perioden gehandelt – nicht mit dem Volumen-POC verwechselt");
        v.TpoClock.Should().Contain("30 min");
    }

    [Fact]
    public void Absorption_is_a_measured_observation_with_side_window_volume_and_missing_progress()
    {
        var ctx = new SessionContext(0.25m, absorptionWindow: TimeSpan.FromSeconds(30), absorptionMinVolume: 50, absorptionMaxProgressTicks: 1);
        ctx.Reset(T0);
        for (int i = 0; i < 20; i++) ctx.OnEvent(Tk(i, i % 2 == 0 ? 100m : 100.25m, 5, AggressorSide.Sell));
        var a = ctx.Values().Absorption!;
        a.Side.Should().StartWith("Verkaufsabsorption");
        a.AggressiveVolume.Should().Be(100m);
        a.ProgressTicks.Should().Be(0m);
        a.Definition.Should().Contain("Fenster 30 s");
    }

    // ---------------------------------------------------------------- Lauf über die bestehende Engine

    private static async Task<(BacktestResult Res, MultiTimeframeHost Host, Dictionary<long, string> States)> RunHost(List<MarketTick> ticks, Func<long, bool>? capture = null)
    {
        var states = new Dictionary<long, string>();
        var host = new MultiTimeframeHost(new MtfFunctionTestStrategy(20m), 0.25m, 4);
        if (capture is not null)
            host.AfterEvent = (s, _) => { if (capture(s.EventIndex)) states[s.EventIndex] = StateKey(s); };
        var res = await new BacktestEngine().RunAsync(BacktestTestData.Request(host, ticks));
        return (res, host, states);
    }

    private static string StateKey(MarketSnapshot s)
    {
        string B(FlowBar? b) => b is null ? "-" : $"{b.Index}:{b.Open}/{b.High}/{b.Low}/{b.Close}/{b.Volume}/{b.Delta}/{b.Status}";
        var c = s.Context;
        return $"{s.EventIndex}|5m {B(s.M5.Current)} c{B(s.M5.LastClosed)}|1m {B(s.M1.Current)} c{B(s.M1.LastClosed)}|r {B(s.R4.Current)} c{B(s.R4.LastClosed)}|vwap {c.SessionVwap}|cvd {c.Cvd}|poc {c.VolumeProfile.Poc}/{c.VolumeProfile.Vah}/{c.VolumeProfile.Val}|tpo {c.TpoProfile.Poc}|sw {c.LastSwingHigh?.Price}@{c.LastSwingHigh?.ConfirmedAtEvent}/{c.LastSwingLow?.Price}";
    }

    private static List<MarketTick> Synthetic(int seed = 7, int minutes = 120) =>
        MtfSyntheticSession.Generate(seed, T0, minutes).Select(t => t with { Symbol = "NQ" }).ToList();

    [Fact]
    public async Task B_the_function_test_uses_5m_context_1m_confirmation_and_rangeXV_trigger_with_a_fired_and_a_blocked_case()
    {
        var (res, host, _) = await RunHost(Synthetic());
        var fired = host.Traces.Where(t => t.Kind == "einstieg").ToList();
        var blocked = host.Traces.Where(t => t.Kind == "kandidat_abgelehnt").ToList();
        fired.Should().NotBeEmpty(); blocked.Should().NotBeEmpty();
        foreach (var f in fired)
        {
            f.Checks.Select(c => c.View).Should().Contain(new[] { "5m", "1m", "rangeXV 4" });
            f.Checks.Should().OnlyContain(c => c.Result == RuleResult.Met && c.BarStatus == "geschlossen");
            f.M5Closed.Should().NotBeNull(); f.M1Closed.Should().NotBeNull(); f.R4Closed.Should().NotBeNull();
        }
        blocked.Should().OnlyContain(b => b.Checks.Any(c => c.Result != RuleResult.Met) && b.Checks.Single(c => c.Role == "Trigger").Result == RuleResult.Met);
        res.Trades.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_fast_run_and_replay_rerun_agree_on_event_positions_states_decisions_fills_and_fees()
    {
        var ticks = Synthetic();
        var (fast, fh, _) = await RunHost(ticks);
        var checkpoints = fh.Traces.Select(t => t.EventIndex).Concat(fh.Transitions.Select(t => t.EventIndex)).Concat(new long[] { 1, 500, 2000, ticks.Count }).ToHashSet();
        var (fast2, _, fastStates) = await RunHost(ticks, checkpoints.Contains);   // Schnelllauf mit Kontrollpunkten
        var (replay, rh, replayStates) = await RunHost(ticks, _ => true);          // „Replay“: jeder Zustand aufgezeichnet
        string? first = FirstDifference(fh, rh);
        first.Should().BeNull();
        foreach (var k in checkpoints) replayStates[k].Should().Be(fastStates[k], $"Chartzustand bei Ereignis {k}");
        replay.Trades.Should().BeEquivalentTo(fast.Trades, o => o.WithStrictOrdering());
        replay.Trades.Sum(t => t.Fees).Should().Be(fast.Trades.Sum(t => t.Fees));
        MtfRunService.Fingerprint(rh.Traces, rh.Transitions, replay.Trades).Should().Be(MtfRunService.Fingerprint(fh.Traces, fh.Transitions, fast.Trades));
        fast2.Trades.Should().HaveCount(fast.Trades.Count, "Aufzeichnen ändert nichts");
    }

    private static string? FirstDifference(MultiTimeframeHost a, MultiTimeframeHost b)
    {
        var ka = a.Traces.Select(MtfRunService.TraceKey).ToList(); var kb = b.Traces.Select(MtfRunService.TraceKey).ToList();
        for (int i = 0; i < Math.Min(ka.Count, kb.Count); i++) if (ka[i] != kb[i]) return $"erste Abweichung bei Entscheidung {i}, Ereignis {a.Traces[i].EventIndex}";
        if (ka.Count != kb.Count) return "Anzahl Entscheidungen";
        for (int i = 0; i < Math.Min(a.Transitions.Count, b.Transitions.Count); i++)
            if (a.Transitions[i] != b.Transitions[i]) return $"Fill-Abweichung bei Ereignis {a.Transitions[i].EventIndex}";
        return a.Transitions.Count == b.Transitions.Count ? null : "Anzahl Fills";
    }

    [Fact]
    public async Task C_changing_only_events_after_K_leaves_all_earlier_chart_values_and_decisions_identical()
    {
        var ticks = Synthetic();
        // K mitten in einer 5m-Bar (offene Kerze), nach mindestens einer Entscheidung.
        int k = ticks.FindIndex(t => t.Timestamp >= T0.AddMinutes(47).AddSeconds(30));
        long K = k + 1;
        var changed = ticks.Select((t, i) => i < k ? t : t with { Price = t.Price + 37.5m, Volume = t.Volume * 3, Aggressor = t.Aggressor == AggressorSide.Buy ? AggressorSide.Sell : AggressorSide.Buy }).ToList();
        var (_, ha, sa) = await RunHost(ticks, e => e <= K);
        var (_, hb, sb) = await RunHost(changed, e => e <= K - 1);
        for (long e = 1; e < K; e++) sb[e].Should().Be(sa[e], $"Ereignis {e} liegt vor der Änderung");
        sa[K - 1].Should().Contain("5m ").And.Contain("Offen", "bei K ist die 5m-Kerze noch offen – ihre Endwerte waren nicht sichtbar");
        hb.Traces.Where(t => t.EventIndex < K).Select(MtfRunService.TraceKey).Should().Equal(ha.Traces.Where(t => t.EventIndex < K).Select(MtfRunService.TraceKey));
        // Ein bei K noch unbestätigter Swing bleibt in beiden Läufen bis K unbestätigt; der Volumen-POC entwickelt sich identisch.
        var swingLater = ha.Bars5.Count > 0;
        swingLater.Should().BeTrue();
    }

    // ---------------------------------------------------------------- Service: Lauf, Spur, Rewind

    [Fact]
    public async Task Service_run_stores_decision_trace_per_trade_and_replay_window_reproduces_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mtf-tests", Guid.NewGuid().ToString("N"));
        var svc = new MtfRunService(new BacktestApiService(SessionFixtures.RepoRoot()), null, null, dir);
        var r = await svc.RunAsync(new MtfRunRequest { Dataset = "synthetic", Seed = 7, Minutes = 120 });
        r.Status.Should().Be("Completed", r.Error);
        r.IsDemonstration.Should().BeTrue();
        r.RunKind.Should().StartWith("Funktionstest");
        r.Trades.Should().NotBeEmpty();
        var t = r.Trades.First(x => x.EntryTraceId is not null);
        t.SignalEvent.Should().NotBeNull();
        t.EntryFillEvent.Should().Be(t.SignalEvent + 1, "frühester zulässiger Fill = nächstes Ereignis");
        t.Explanation.Should().Contain("Kontext 5m erfüllt").And.Contain("Bestätigung 1m erfüllt").And.Contain("Trigger rangeXV 4 erfüllt");
        r.Traces.Should().Contain(x => x.Kind == "kandidat_abgelehnt");
        var w = await svc.ReplayAsync(r.RunId, t.Index, 60, 30);
        w.Reproduced.Should().BeTrue(w.FirstDifference);
        w.FromEvent.Should().BeLessThan(t.SignalEvent!.Value);
        w.ToEvent.Should().BeGreaterThanOrEqualTo(t.ExitFillEvent!.Value);
        w.States.Should().HaveCount(w.Events.Count);
        w.Traces.Should().Contain(x => x.EventIndex == t.SignalEvent);
        w.ClosedBars["5m"].Should().NotBeEmpty();
    }

    [Fact]
    public void Generated_replay_data_is_only_runnable_as_explicit_demonstration()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mtf-tests", Guid.NewGuid().ToString("N"));
        var svc = new MtfRunService(new BacktestApiService(SessionFixtures.RepoRoot()), null, null, dir);
        var c = svc.Check(new MtfRunRequest { Dataset = "synthetic" });
        c.Statements.Should().Contain(s => s.Category == "vorläufig" && s.Text.Contains("rangeXV"));
        c.Requirements.Should().OnlyContain(x => x.Status == "nur_demonstration");
        c.Runnable.Should().BeTrue();
    }
}
