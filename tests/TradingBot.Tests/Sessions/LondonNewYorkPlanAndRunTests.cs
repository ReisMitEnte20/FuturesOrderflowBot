using FluentAssertions;
using TradingBot.Application.Sessions;
using TradingBot.DevDashboard.Services;
using TradingBot.DevDashboard.Services.Sessions;
using Xunit;
using static TradingBot.Tests.Sessions.SessionFixtures;

namespace TradingBot.Tests.Sessions;

/// <summary>
/// Sessionplan und gemeinsamer Lauf „London + New York“ auf kleinen SYNTHETISCHEN Collections in zwei getrennten Datenordnern:
/// vollständiger Tag, fehlende Session, Drops, Tag ohne Daten, Überlappung (gleiche/verschiedene Aufnahme), Sommerzeit-Pause,
/// zwei Handelstage am Stück mit einer Strategie und einem Konto, Tageslimit über die Session hinweg.
/// </summary>
public sealed class LondonNewYorkPlanAndRunTests
{
    private static readonly IReadOnlyList<SessionDefinition> Defs = SessionScheduleFile.Load(RepoRoot()).File.Definitions();

    private static JobSpec London(string day, string rec, int n = 1500, long drops = 0, int seed = 1)
    {
        var s = Utc(day + "T08:00:00Z"); var e = Utc(day + "T11:00:00Z");
        if (day.CompareTo("2026-03-29") >= 0 && day.CompareTo("2026-10-25") < 0) { s = s.AddHours(-1); e = e.AddHours(-1); } // BST
        return new JobSpec("London", s, e, rec, Ticks(s, e, n, seed), drops);
    }

    private static JobSpec NewYork(string day, string rec, int n = 2500, int seed = 2)
    {
        bool edt = day.CompareTo("2026-03-08") >= 0 && day.CompareTo("2026-11-01") < 0;
        var s = Utc(day + (edt ? "T13:30:00Z" : "T14:30:00Z")); var e = s.AddHours(2.5);
        return new JobSpec("NewYork", s, e, rec, Ticks(s, e, n, seed, 20010m));
    }

    private sealed record Env(SessionPlanService Plan, SessionRunService Run, TickReplayService Replay, string Root);

    private static Env Setup(IEnumerable<JobSpec> london, IEnumerable<JobSpec> ny, string? repo = null, IReadOnlyList<SessionDefinition>? defs = null)
    {
        string root = NewRoot();
        string ldnRoot = WriteCollection(Path.Combine(root, "London"), "MNQ_London_0800-1100_synth", "London", "GMT Standard Time", london);
        string nyRoot = WriteCollection(Path.Combine(root, "NewYork"), "MNQ_NewYork_0930-1200_synth", "NewYork", "America/New_York", ny);
        var replay = TickReplayService.ForCollectionsRoots(new[] { nyRoot, ldnRoot }, Path.Combine(root, "cache"), defs ?? Defs);
        var plan = new SessionPlanService(replay, repo ?? RepoRoot());
        var bt = new BacktestApiService(RepoRoot());
        return new Env(plan, new SessionRunService(plan, bt, Path.Combine(root, "runs")), replay, root);
    }

    [Fact]
    public void Plan_distinguishes_complete_days_missing_and_defective_sessions_and_days_without_data()
    {
        var env = Setup(
            new[] { London("2026-03-19", "L19"), London("2026-03-20", "L20", drops: 12), London("2026-03-23", "L23"), London("2026-03-31", "L31") },
            new[] { NewYork("2026-03-19", "N19"), NewYork("2026-03-20", "N20"), NewYork("2026-03-31", "N31") });
        var plan = env.Plan.Plan(new DateOnly(2026, 3, 19), new DateOnly(2026, 3, 31));

        var d = plan.Days.ToDictionary(x => x.Day);
        d["2026-03-19"].Status.Should().Be("complete");
        d["2026-03-19"].PlannedPauseMinutes.Should().Be(150, "USA bereits Sommerzeit, UK noch nicht");
        d["2026-03-31"].Status.Should().Be("complete");
        d["2026-03-31"].PlannedPauseMinutes.Should().Be(210);
        d["2026-03-20"].Status.Should().Be("incomplete", "London mit Drops ist fehlerhaft – der Tag darf nicht als vollständig gelten");
        d["2026-03-20"].Sessions.Single(s => s.Key == "London").Status.Should().Be("defective");
        d["2026-03-23"].Status.Should().Be("incomplete", "New York fehlt – das ist keine normale Pause");
        d["2026-03-23"].Sessions.Single(s => s.Key == "NewYork").Status.Should().Be("missing");
        d["2026-03-24"].Status.Should().Be("no-data");
        d.Should().NotContainKey("2026-03-21", "Wochenende ist nicht geplant");
        plan.CompleteDays.Should().Be(2);
        d["2026-03-19"].Sessions.Should().OnlyContain(s => s.Status == "ok" && s.RecordingId != null);
    }

    [Fact]
    public void Overlapping_windows_from_the_same_recording_are_split_and_from_different_recordings_are_ambiguous()
    {
        // Sonderplan: „NewYork“ 05:30–08:00 America/New_York = 10:30–13:00 UTC im Januar → überlappt London 08:00–11:00 UTC.
        const string sched = """
        { "name": "Overlap-Test", "closeOutMinutesBeforeEnd": 5, "sessions": [
          { "key": "London", "label": "London", "timeZone": "Europe/London", "start": "08:00", "end": "11:00" },
          { "key": "NewYork", "label": "New York", "timeZone": "America/New_York", "start": "05:30", "end": "08:00" } ] }
        """;
        string repo = RepoWithSchedule(sched);
        var defs = SessionScheduleFile.Load(repo).File.Definitions();
        JobSpec Ny(string day, string rec) { var s = Utc(day + "T10:30:00Z"); var e = Utc(day + "T13:00:00Z"); return new("NewYork", s, e, rec, Ticks(s, e, 300, 5)); }
        JobSpec Ldn(string day, string rec) { var s = Utc(day + "T08:00:00Z"); var e = Utc(day + "T11:00:00Z"); return new("London", s, e, rec, Ticks(s, e, 300, 6)); }
        var env = Setup(new[] { Ldn("2026-01-15", "SAME"), Ldn("2026-01-16", "LX") }, new[] { Ny("2026-01-15", "SAME"), Ny("2026-01-16", "NX") }, repo, defs);

        var plan = env.Plan.Plan(new DateOnly(2026, 1, 15), new DateOnly(2026, 1, 16));
        var same = plan.Days.Single(x => x.Day == "2026-01-15");
        same.Status.Should().Be("complete");
        same.Sessions.Single(s => s.Key == "NewYork").EffectiveStartUtc.Should().Be("2026-01-15T11:00:00Z", "gleiche Aufnahme: Ticks vor London-Ende nicht doppelt");
        var diff = plan.Days.Single(x => x.Day == "2026-01-16");
        diff.Status.Should().Be("ambiguous");
        diff.Reasons.Should().ContainSingle(r => r.Contains("nicht eindeutig"));
    }

    [Fact]
    public async Task Two_trading_days_run_as_one_strategy_and_one_account_and_the_jumps_create_no_pnl()
    {
        var env = Setup(
            new[] { London("2026-03-19", "L19", seed: 11), London("2026-03-20", "L20", seed: 12), London("2026-03-23", "L23", seed: 13) },
            new[] { NewYork("2026-03-19", "N19", seed: 21), NewYork("2026-03-20", "N20", seed: 22) });
        var r = await env.Run.RunAsync(new SessionRunRequest { From = "2026-03-19", To = "2026-03-23", TicksPerBar = 50, DeltaThreshold = 5 });

        r.Status.Should().Be("Completed", r.Error);
        r.Segments.Select(s => s.Id).Should().HaveCount(4, "19. und 20. je London + New York; der 23. ist unvollständig (NY fehlt) und ausgelassen");
        r.Plan!.Days.Single(d => d.Day == "2026-03-23").Status.Should().Be("incomplete");
        r.TicksProcessed.Should().Be(r.Segments.Sum(s => s.TicksUsed), "ein einziger Engine-Lauf über alle Sessions");
        r.Segments.Sum(s => s.TicksUsed).Should().Be(1500 * 2 + 2500 * 2);

        r.Transitions.Should().HaveCount(3);
        r.Transitions[0].Kind.Should().Contain("gleicher Handelstag");
        r.Transitions[1].Kind.Should().Be("Tageswechsel");
        foreach (var t in r.Transitions)
        {
            t.PnlChangeInJump.Should().Be(0m);
            t.TradesClosedInJump.Should().Be(0);
            t.TradesOpenedInJump.Should().Be(0);
            t.BalanceAfter.Should().Be(t.BalanceBefore);
            t.PositionAtLastTick.Should().Be("Flat");
        }
        r.Transitions[0].SkippedSeconds.Should().BeGreaterThan(2 * 3600).And.BeLessThan(2.6 * 3600);

        r.Trades.Should().NotBeEmpty();
        r.Trades.Should().OnlyContain(t => !t.CrossedPause);
        r.BySession.Sum(s => s.Trades).Should().Be(r.Total!.Trades);
        r.BySession.Sum(s => s.NetPnL).Should().Be(r.Total.NetPnL);
        r.BySession.Sum(s => s.Fees).Should().Be(r.Total.Fees);
        r.Total.NetPnL.Should().Be(r.Total.GrossPnL - r.Total.Fees);
        r.EndBalance.Should().Be(r.StartBalance + r.Total.NetPnL);
        r.Trades.Last().BalanceAfter.Should().Be(r.EndBalance);
        foreach (var t in r.Trades)
        {
            var seg = r.Segments.Single(s => s.Id == t.SegmentId);
            seg.Session.Should().Be(t.Session);
            string.CompareOrdinal(t.EntryTimeUtc, seg.WindowStartUtc).Should().BeGreaterThanOrEqualTo(0);
            string.CompareOrdinal(t.ExitTimeUtc, seg.WindowEndUtc).Should().BeLessThan(0, "jeder Trade endet in seiner Session (Close-Out vor dem Ende)");
            t.RecordingId.Should().NotBeNullOrEmpty();
        }
        r.Segments.Should().OnlyContain(s => s.SessionCloseStatus.Length > 0);
        r.FinalPosition.Should().Be("Flat");

        // Gespeichert und wieder lesbar (Trades im Chart öffnen den Lauf über die Lauf-ID).
        env.Run.Get(r.RunId)!.Trades.Should().HaveCount(r.Trades.Count);
        env.Run.List().Should().ContainSingle(x => x.RunId == r.RunId);
    }

    [Fact]
    public async Task Daily_limit_reached_in_London_means_no_new_entries_in_New_York_that_day()
    {
        var env = Setup(new[] { London("2026-03-19", "L19", seed: 31) }, new[] { NewYork("2026-03-19", "N19", seed: 32) });
        var r = await env.Run.RunAsync(new SessionRunRequest { From = "2026-03-19", To = "2026-03-19", TicksPerBar = 50, DeltaThreshold = 5, MaxDailyLoss = 1m });

        r.Status.Should().Be("Completed", r.Error);
        var day = r.ByDay.Single();
        day.DailyLimitHitUtc.Should().NotBeNull("mit 1 USD Limit löst der erste Verlust das Tageslimit aus");
        day.DailyLimitHitSession.Should().Be("London");
        day.EntriesAfterLimit.Should().Be(0);
        r.BySession.Single(s => s.Key == "NewYork").Trades.Should().Be(0, "null Trades in einer Session ist ein gültiges Ergebnis");
        r.OrdersRejectedByRisk.Should().BeGreaterThan(0);
        r.Transitions.Single().BalanceAfter.Should().Be(r.Transitions.Single().BalanceBefore, "Kontostand bleibt beim Übergang erhalten");
    }

    /// <summary>London-Aufnahme 08:00–11:00, aber Ticks nur bis 10:40 (vor der Close-Out-Grenze): eine offene Position ist vor dem
    /// Sessionende nicht mehr glattstellbar. Optional wird NUR der erste New-York-Preis verändert.</summary>
    private static async Task<SessionRunResult> StoppedRun(decimal? firstNyPrice)
    {
        var s = Utc("2026-01-15T08:00:00Z"); var e = Utc("2026-01-15T11:00:00Z");
        var ldn = new JobSpec("London", s, e, "LS", Ticks(s, Utc("2026-01-15T10:40:00Z"), 1500, 41));
        var ny = NewYork("2026-01-15", "NS", seed: 42);
        if (firstNyPrice is decimal p)
        {
            var t = ny.Ticks.ToList();
            t[0] = (t[0].T, p, t[0].Vol, t[0].Buy);
            ny = ny with { Ticks = t };
        }
        var env = Setup(new[] { ldn }, new[] { ny });
        return await env.Run.RunAsync(new SessionRunRequest { From = "2026-01-15", To = "2026-01-15", TicksPerBar = 50, DeltaThreshold = 0 });
    }

    [Fact]
    public async Task Open_position_that_cannot_be_flattened_before_the_session_end_stops_the_run_as_incomplete()
    {
        var r = await StoppedRun(null);

        r.Status.Should().Be("Incomplete");
        r.Stop.Should().NotBeNull();
        r.Stop!.Reason.Should().Be("unvollständig – Position vor Sessionende nicht glattstellbar");
        r.Stop.Session.Should().Be("London");
        r.Stop.LastTickUtc.Should().StartWith("2026-01-15T10:39", "letzter beobachteter London-Tick");
        r.Stop.OpenQuantity.Should().Be(1);
        r.Stop.LastObservedPrice.Should().NotBeNull();
        r.Stop.RealizedNet.Should().Be(r.Trades.Sum(t => t.NetPnL), "realisiert = nur geschlossene Trades");
        r.Stop.BalanceRealized.Should().Be(r.StartBalance + r.Stop.RealizedNet);
        r.Stop.StoppedBeforeTickUtc.Should().StartWith("2026-01-15T14:30", "vor dem ersten New-York-Tick angehalten");
        r.Stop.NotRunSegments.Should().ContainSingle(x => x.StartsWith("NewYork"));
        r.Segments.Should().ContainSingle("New York wurde nicht gestartet");
        r.Trades.Should().OnlyContain(t => t.Session == "London" && string.CompareOrdinal(t.ExitTimeUtc, "2026-01-15T10:40") < 0);
        r.ContractCheck.Should().StartWith("Kontraktidentität ungeprüft");
        r.Integrity!.FillsSharedByTwoTrades.Should().Be(0);
        r.Integrity.OverlappingTrades.Should().Be(0);
    }

    [Fact]
    public async Task Regression_a_drastically_different_first_new_york_price_leaves_the_stopped_london_result_unchanged()
    {
        var a = await StoppedRun(null);
        var b = await StoppedRun(1_000m);

        b.Status.Should().Be("Incomplete");
        b.Stop!.LastObservedPrice.Should().Be(a.Stop!.LastObservedPrice);
        b.Stop.UnrealizedGrossAtLastPrice.Should().Be(a.Stop.UnrealizedGrossAtLastPrice);
        b.Stop.RealizedNet.Should().Be(a.Stop.RealizedNet);
        b.Stop.RealizedFees.Should().Be(a.Stop.RealizedFees);
        b.Stop.OpenPosition.Should().Be(a.Stop.OpenPosition);
        b.Stop.PendingOrders.Should().Be(a.Stop.PendingOrders);
        b.EndBalance.Should().Be(a.EndBalance);
        b.TicksProcessed.Should().Be(a.TicksProcessed);
        b.Trades.Select(t => (t.EntryTimeUtc, t.EntryPrice, t.ExitTimeUtc, t.ExitPrice, t.NetPnL))
            .Should().Equal(a.Trades.Select(t => (t.EntryTimeUtc, t.EntryPrice, t.ExitTimeUtc, t.ExitPrice, t.NetPnL)));
    }

    [Fact]
    public async Task Range_without_any_complete_day_runs_nothing_and_says_why()
    {
        var env = Setup(new[] { London("2026-03-23", "L23") }, Array.Empty<JobSpec>());
        var r = await env.Run.RunAsync(new SessionRunRequest { From = "2026-03-23", To = "2026-03-23" });
        r.Status.Should().Be("NoData");
        r.Trades.Should().BeEmpty();
        r.Plan!.Days.Single().Status.Should().Be("incomplete");
    }
}
