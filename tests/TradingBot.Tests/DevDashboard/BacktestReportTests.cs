using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluentAssertions;
using TradingBot.DevDashboard.Services;
using TradingBot.DevDashboard.Services.Quant;
using TradingBot.DevDashboard.Services.Quant.Report;
using Xunit;

namespace TradingBot.Tests.DevDashboard;

/// <summary>
/// Abnahme des Ein-Knopf-Berichts auf kleinen, deterministischen Daten: ein Auftrag = alle Abschnitte, Basislauf identisch
/// zum Einzellauf, Gebühren/Slippage genau einmal, Doppelklick/Reload/Abbruch, zu wenig Historie und ein Vergleich, der
/// nichts neu rechnet.
/// </summary>
[Collection("ResearchSerial")]
public class BacktestReportTests : IDisposable
{
    private readonly List<string> _dirs = new();
    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (var d in _dirs) try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        foreach (var f in _files) try { if (File.Exists(f)) File.Delete(f); } catch { }
        GC.SuppressFinalize(this);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradingBot.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo-Wurzel nicht gefunden.");
    }

    private (BacktestingService svc, ResearchOrchestrator orch, QuantApiService quant) New()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "bt-report-tests-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(tempRoot);
        var quant = new QuantApiService(new BacktestApiService(RepoRoot()), tempRoot);
        var orch = new ResearchOrchestrator(quant, new QuantJobManager(), new ResearchRunStore(quant.RegistryDir));
        return (new BacktestingService(quant, orch), orch, quant);
    }

    /// <summary>Werktage ab Montag 02.03.2026, je Tag 14:30–20:55 UTC in 5-Minuten-Kerzen (78 Kerzen), Sinus-Kurs.</summary>
    private string WriteCsv(int days, Func<int, double>? priceOverride = null, int lastDayBars = 78)
    {
        var path = Path.Combine(Path.GetTempPath(), "bt-report-" + Guid.NewGuid().ToString("N") + ".csv");
        _files.Add(path);
        var sb = new StringBuilder("timestamp,open,high,low,close,volume\n");
        var day = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);
        int k = 0, made = 0;
        double Price(int i) => priceOverride?.Invoke(i) ?? 5000 + 30 * Math.Sin(2 * Math.PI * i / 46.0) + 8 * Math.Sin(2 * Math.PI * i / 13.0);
        while (made < days)
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                int barsToday = made == days - 1 ? lastDayBars : 78;
                for (int b = 0; b < barsToday; b++, k++)
                {
                    var t = day.AddHours(14).AddMinutes(30 + 5 * b);
                    double o = Price(k - 1), c = Price(k), h = Math.Max(o, c) + 0.75, l = Math.Min(o, c) - 0.75;
                    sb.Append(string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-ddTHH:mm:ss}Z,{1:F2},{2:F2},{3:F2},{4:F2},100\n", t, o, h, l, c));
                }
                made++;
            }
            day = day.AddDays(1);
        }
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static BacktestingStartRequest Req(string csv, int iterations = 400) => new()
    {
        Strategy = "movingaverage", Params = new() { ["FastPeriod"] = "9", ["SlowPeriod"] = "21" },
        Symbol = "MES", DataSourceId = "csv", Path = csv, TimeframeMinutes = 5,
        Quantity = 1, InitialBalance = 10_000m, StopLossTicks = 16, TakeProfitTicks = 24, ApplyFees = true,
        HoldoutShare = 0.2, TrainSessions = 20, TestSessions = 5, Seed = 99, SimulationIterations = iterations, MinTrades = 30, MinTrainTrades = 3
    };

    private static async Task<ResearchRunRecord> Wait(ResearchOrchestrator orch, string runId, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var r = (await orch.GetAsync(runId)).Run!;
            if (r.Status is ResearchStepStatus.Completed or ResearchStepStatus.Failed or ResearchStepStatus.Cancelled) return r;
            await Task.Delay(80);
        }
        throw new TimeoutException("Bericht wurde nicht rechtzeitig fertig.");
    }

    [Fact]
    public async Task One_click_produces_a_complete_report_and_the_base_run_equals_a_single_run()
    {
        var (svc, orch, quant) = New();
        var csv = WriteCsv(40);

        var preview = await svc.PreviewAsync(Req(csv));
        preview.Ok.Should().BeTrue(preview.Error);
        preview.Sessions.Should().Be(40);
        preview.HoldoutSessions.Should().Be(8);
        preview.Checks.Single(c => c.Key == "oos-rolling").Status.Should().Be("possible");

        var start = await svc.StartAsync(Req(csv));
        start.Ok.Should().BeTrue(start.Error);
        var run = await Wait(orch, start.Run!.RunId, TimeSpan.FromMinutes(3));
        run.Status.Should().Be(ResearchStepStatus.Completed, run.StatusReason);
        string steps = string.Join(" | ", run.Steps.Select(s => $"{s.Key}:{s.Status}:{s.Reason}"));
        run.Steps.Should().OnlyContain(s => s.Status != ResearchStepStatus.Failed && s.Status != ResearchStepStatus.Pending, steps);

        var rep = run.Report!;
        rep.Data.Should().NotBeNull(); rep.Result.Should().NotBeNull(); rep.Equity.Should().NotBeNull(); rep.Risk.Should().NotBeNull();
        rep.Sensitivity!.Applicable.Should().BeTrue(); rep.Oos.Should().NotBeNull(); rep.Simulation.Should().NotBeNull();
        rep.Costs.Should().NotBeNull(); rep.Stability.Should().NotBeNull(); rep.Concentration.Should().NotBeNull();
        rep.Controls.Should().NotBeNull(); rep.Trades.Should().NotBeNull();
        rep.Overview.Figures.Should().HaveCount(6);
        rep.Provenance.DataSha256.Should().NotBeNullOrEmpty();
        rep.Provenance.SessionsUsed.Should().HaveCount(32);
        rep.Provenance.HoldoutSessions.Should().Be(8);

        // Zeitliche Korrektheit: Abschneidetest und „nicht rückwirkend gefüllt“ bestanden.
        rep.Data!.TemporalChecks.Where(c => c.Passed is not null).Should().OnlyContain(c => c.Passed == true,
            string.Join(" | ", rep.Data.TemporalChecks.Select(c => c.Name + ": " + c.Detail)));

        // Einzellauf (gleiche Konfiguration, gleicher Zeitraum) == Basislauf des Gesamtauftrags.
        var single = await quant.Backtest.RunAsync(run.DevRun!);
        single.Ok.Should().BeTrue();
        rep.Trades!.Trades.Should().HaveCount(single.Result!.Trades.Count);
        rep.Trades.Trades.Select(t => t.Net).Should().Equal(single.Result.Trades.Select(t => t.NetPnL));
        rep.Result!.Stats.NetPnL.Should().Be(single.Result.Statistics.NetProfit);

        // Gebühren und Slippage genau einmal: Netto = Brutto − Gebühren; Gebühren = 2 × Gebühr je Seite.
        decimal feeSide = rep.Provenance.CostModel!.FeePerSide;
        foreach (var t in rep.Trades.Trades)
        {
            t.Net.Should().Be(t.Gross - t.Fees);
            t.Fees.Should().Be(2 * feeSide);
        }
        rep.Result.Stats.PreCostPnL.Should().Be(rep.Result.Stats.NetPnL + rep.Result.Stats.Fees + rep.Result.Stats.ModeledSlippage);

        // Walk-forward: rollierend mit Ergebnis, festes Raster gespeichert.
        var rolling = rep.Oos!.Variants.Single(v => v.Key == "rolling");
        rolling.Status.Should().Be("Completed", rolling.Reason);
        rolling.Folds.Should().NotBeEmpty();
        rep.Oos.Grid.Should().NotBeEmpty();
        // Walk-forward-Holdout == Bericht-Holdout (erste Holdout-Kerze; der Planer führt die Schlusszeit).
        run.WalkForward!.HoldoutFromT.Should().Be(run.HoldoutFrom!.Value.AddMinutes(5).ToUnixTimeMilliseconds());
        // Kein Walk-forward-Testfenster und kein Trade reicht in den Holdout.
        rep.Oos.Variants.SelectMany(v => v.Folds).Should().OnlyContain(f => f.TestToT < run.HoldoutFrom!.Value.ToUnixTimeMilliseconds());
        rep.Trades!.Trades.Should().OnlyContain(t => t.ExitT <= run.HoldoutFrom!.Value.ToUnixTimeMilliseconds());

        // Permutation: End-Netto in allen Pfaden identisch.
        rep.Simulation!.Methods.Single(m => m.Key == "permutation").FinalNetInvariant.Should().BeTrue();
        rep.Simulation.Methods.Single(m => m.Key == "bootstrap").Iterations.Should().Be(400);

        // Reload: derselbe Auftrag ist mit Bericht wieder abrufbar, nichts wird neu gerechnet.
        long engine = quant.Backtest.EngineRuns;
        var again = (await orch.GetAsync(run.RunId)).Run!;
        again.Report!.Overview.Figures.Should().HaveCount(6);
        quant.Backtest.EngineRuns.Should().Be(engine);
    }

    [Fact]
    public async Task Same_seed_gives_identical_simulation_and_compare_starts_no_backtest()
    {
        var (svc, orch, quant) = New();
        var csv = WriteCsv(30);
        var a = await svc.StartAsync(Req(csv, 300));
        var ra = await Wait(orch, a.Run!.RunId, TimeSpan.FromMinutes(3));
        var b = await svc.StartAsync(Req(csv, 300));
        b.Run!.RunId.Should().NotBe(ra.RunId);
        var rb = await Wait(orch, b.Run.RunId, TimeSpan.FromMinutes(3));

        var ma = ra.Report!.Simulation!.Methods.Single(m => m.Key == "bootstrap");
        var mb = rb.Report!.Simulation!.Methods.Single(m => m.Key == "bootstrap");
        ma.MaxDrawdown!.P95.Should().Be(mb.MaxDrawdown!.P95);
        ma.FinalNet!.Median.Should().Be(mb.FinalNet!.Median);

        int runsBefore = (await orch.ListAsync()).Count;
        long engineBefore = quant.Backtest.EngineRuns;
        var cmp = await svc.CompareAsync(new[] { ra.RunId, rb.RunId });
        cmp.Ok.Should().BeTrue(cmp.Error);
        cmp.EngineRunsAfter.Should().Be(cmp.EngineRunsBefore);
        quant.Backtest.EngineRuns.Should().Be(engineBefore);
        (await orch.ListAsync()).Should().HaveCount(runsBefore);
        cmp.Rows.Should().HaveCount(2);
        cmp.Rows[0].Net.Should().Be(cmp.Rows[1].Net);   // gleiche Konfiguration ⇒ gleiche OOS-Ergebnisse
    }

    [Fact]
    public async Task Double_click_returns_the_running_job_and_cancel_keeps_finished_sections()
    {
        var (svc, orch, _) = New();
        var csv = WriteCsv(40);
        var first = await svc.StartAsync(Req(csv, 20_000));
        var second = await svc.StartAsync(Req(csv, 20_000));
        second.Ok.Should().BeTrue();
        second.AlreadyRunning.Should().BeTrue();
        second.Run!.RunId.Should().Be(first.Run!.RunId);

        // Warten, bis mindestens ein Abschnitt fertig ist, dann abbrechen.
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            var r = (await orch.GetAsync(first.Run.RunId)).Run!;
            if (r.Steps.Any(s => s.Key == "result" && s.Status == ResearchStepStatus.Completed)) break;
            await Task.Delay(30);
        }
        orch.Cancel(first.Run.RunId).Should().BeTrue();
        var done = await Wait(orch, first.Run.RunId, TimeSpan.FromMinutes(2));
        if (done.Status == ResearchStepStatus.Cancelled)
        {
            done.Steps.Single(s => s.Key == "result").Status.Should().Be(ResearchStepStatus.Completed);
            done.Report!.Result.Should().NotBeNull();
            done.Steps.Should().Contain(s => s.Status == ResearchStepStatus.Cancelled);
        }
        else done.Status.Should().Be(ResearchStepStatus.Completed);   // sehr schneller Rechner: schon fertig — dann kein Abbruch nötig
        (await orch.ListAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task Insufficient_history_is_reported_as_such_and_the_rest_stays_usable()
    {
        var (svc, orch, _) = New();
        var csv = WriteCsv(6);
        var preview = await svc.PreviewAsync(Req(csv));
        preview.Checks.Single(c => c.Key == "oos-rolling").Reason.Should().Be("25 Sessions benötigt, 5 vorhanden.");

        var start = await svc.StartAsync(Req(csv, 200));
        var run = await Wait(orch, start.Run!.RunId, TimeSpan.FromMinutes(2));
        run.Status.Should().Be(ResearchStepStatus.Completed, run.StatusReason);
        var oos = run.Steps.Single(s => s.Key == "oos");
        oos.Status.Should().Be(ResearchStepStatus.InsufficientData);
        run.Report!.Oos!.Variants.Single(v => v.Key == "rolling").Reason.Should().Be("25 Sessions benötigt, 5 vorhanden.");
        run.Steps.Single(s => s.Key == "result").Status.Should().Be(ResearchStepStatus.Completed);
        run.Report.Result.Should().NotBeNull();
        run.Report.Overview.Figures.Single(f => f.Key == "oos").Value.Should().BeNull();
    }

    private static ResearchRunRecord Stored(string id, string symbol, params (string Key, decimal Net)[] oos) => new()
    {
        RunId = id,
        Config = new ResearchStartRequest
        {
            Run = new BacktestRunRequest { Symbol = symbol }, Campaign = new CampaignInput { Id = id }, Profile = "full"
        },
        Report = new BacktestReport
        {
            Provenance = new ReportProvenance { Symbol = symbol, Quantity = 1, TimeframeMinutes = 5, StrategyName = id, FillModel = new[] { "x" },
                CostModel = new CostProfileDto(0.25m, 1.25m, 5m, "USD", 0.39m, 0.78m, 1m, 1.25m, true, true, true) },
            Oos = new ReportOosSection
            {
                PrimaryVariant = "rolling",
                Variants = new[] { new OosVariant { Key = "rolling", Label = "r", Status = "Completed",
                    OosSessionResults = oos.Select(o => new SessionResult(o.Key, o.Net, 1)).ToList() } }
            }
        }
    };

    [Fact]
    public void Comparison_uses_only_common_oos_sessions_and_explains_when_there_is_no_fair_basis()
    {
        var a = Stored("a", "MES", ("2026-06-01", 100), ("2026-06-02", -50), ("2026-06-03", 30));
        var b = Stored("b", "MES", ("2026-06-02", 20), ("2026-06-03", 10), ("2026-06-04", 999));
        var cmp = BacktestingService.BuildComparison(new[] { a, b });
        cmp.Fair.Should().BeTrue();
        cmp.CommonSessions.Should().Equal("2026-06-02", "2026-06-03");
        cmp.Rows.Single(r => r.RunId == "a").Net.Should().Be(-20m);       // nur gemeinsame Tage
        cmp.Rows.Single(r => r.RunId == "b").Net.Should().Be(30m);        // 999 am nicht gemeinsamen Tag zählt nicht
        cmp.Rows.Single(r => r.RunId == "a").MaxDrawdown.Should().Be(50);

        var c = Stored("c", "MNQ", ("2026-07-01", 5));
        var none = BacktestingService.BuildComparison(new[] { a, c });
        none.Fair.Should().BeFalse();
        none.Problems.Should().Contain(p => p.StartsWith("Instrument unterscheidet sich"));
        none.Problems.Should().Contain(p => p.StartsWith("Keine gemeinsamen Walk-forward-Testsessions"));
    }

    [Fact]
    public async Task Report_holdout_and_walk_forward_holdout_cover_exactly_the_same_sessions()
    {
        // 31 Handelstage, der letzte nur angeschnitten (6 Kerzen) — genau die Lage der echten MES-Daten (Datenende 12.06. 00:00 UTC).
        // Früher: Bericht zählte Kerzen (6 Sessions Holdout), Walk-forward-Planer Sessions (5) → eine Session Versatz.
        var (svc, _, quant) = New();
        var csv = WriteCsv(31, lastDayBars: 6);
        var req = await svc.BuildStartRequestAsync(Req(csv));
        var split = await quant.ComputeHoldoutSplitAsync(req.Run, req.HoldoutFraction, req.SessionCalendar);
        var ctx = await quant.Backtest.LoadContextAsync(req.Run);
        var plan = TradingBot.Quant.Validation.WalkForwardPlanner.Plan(ctx.Candles.Select(c => c.CloseTime).ToList(),
            new TradingBot.Quant.Validation.WalkForwardOptions
            {
                SessionStarts = TradingBot.Quant.Report.TradeDateCalendar.SessionStartIndices(ctx.Candles.Select(c => c.OpenTime).ToList()),
                TrainSessions = 20, TestSessions = 5, TrainBars = 1, TestBars = 1, HoldoutFraction = req.HoldoutFraction
            });
        plan.Holdout.Should().NotBeNull();
        plan.Holdout!.Start.Should().Be(split.UsableBars, "Bericht und Walk-forward müssen dieselbe Holdout-Grenze haben");
        int holdoutSessions = ctx.Candles.Skip(split.UsableBars).Select(c => TradingBot.Quant.Report.TradeDateCalendar.Key(c.OpenTime)).Distinct().Count();
        holdoutSessions.Should().Be(6);   // round(31 · 0,2) = 6 ganze Sessions
        (await svc.PreviewAsync(Req(csv))).HoldoutSessions.Should().Be(6);
    }

    [Fact]
    public void Pbo_block_rule_keeps_two_periods_per_block()
    {
        ResearchOrchestrator.PboBlocks(30, 16).Should().Be(14);
        ResearchOrchestrator.PboBlocks(28, 16).Should().Be(14);
        ResearchOrchestrator.PboBlocks(100, 16).Should().Be(16);
        ResearchOrchestrator.PboBlocks(6, 16).Should().Be(4);   // darunter weist die CSCV-Bibliothek PBO ehrlich als nicht berechenbar aus
    }

    [Fact]
    public async Task Changing_future_test_data_does_not_change_an_earlier_parameter_choice()
    {
        var (_, _, quant) = New();
        var csvA = WriteCsv(36);
        // Gleiche Daten bis Kerze 2340 (30 Tage); danach völlig anderer Verlauf.
        var csvB = WriteCsv(36, i => i < 30 * 78 ? 5000 + 30 * Math.Sin(2 * Math.PI * i / 46.0) + 8 * Math.Sin(2 * Math.PI * i / 13.0)
                                                  : 5200 - 3 * (i - 30 * 78) + 25 * Math.Sin(i / 3.0));
        QuantWalkForwardRequest Wf(string csv, string id) => new()
        {
            Run = new BacktestRunRequest
            {
                DataSourceId = "csv", Path = csv, Symbol = "MES", TimeframeMinutes = 5, Strategy = "movingaverage",
                Params = new() { ["FastPeriod"] = "9", ["SlowPeriod"] = "21" }, StopLossTicks = 16, TakeProfitTicks = 24, ApplyFees = true
            },
            Options = new QuantEvaluationOptions { Frequency = "Daily" },
            Mode = "Rolling", SessionAligned = true, SessionCalendar = "tradedate", TrainSessions = 10, TestSessions = 5, HoldoutFraction = 0,
            WarmupBars = 26, SelectionMetric = "netprofit",
            Candidates = new[]
            {
                new Dictionary<string, string> { ["FastPeriod"] = "7", ["SlowPeriod"] = "21" },
                new Dictionary<string, string> { ["FastPeriod"] = "9", ["SlowPeriod"] = "21" },
                new Dictionary<string, string> { ["FastPeriod"] = "11", ["SlowPeriod"] = "25" }
            },
            Campaign = new CampaignInput { Id = id, Name = id, SelectionMetric = "netprofit" }
        };
        var a = await quant.WalkForwardAsync(Wf(csvA, "fa"));
        var b = await quant.WalkForwardAsync(Wf(csvB, "fb"));
        a.Ok.Should().BeTrue(a.Error); b.Ok.Should().BeTrue(b.Error);
        a.Folds.Should().HaveCount(b.Folds.Count);
        // Erster veränderter Handelstag = 31. Werktag ab 02.03.2026 (Index 30) → 13.04.2026, 14:30 UTC.
        long changeT = new DateTimeOffset(2026, 4, 13, 14, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var early = a.Folds.Where(f => f.TrainToT < changeT).ToList();
        early.Should().HaveCountGreaterThan(2);
        foreach (var f in early)
        {
            var fb = b.Folds.Single(x => x.Index == f.Index);
            fb.SelectedCandidate.Should().Be(f.SelectedCandidate, $"Fold {f.Index}: Training endet vor den veränderten Daten");
            fb.TrainValue.Should().Be(f.TrainValue);
        }
        // Kontrolle: die veränderten Daten wirken sich tatsächlich aus (sonst wäre der Test wertlos).
        a.Folds.Zip(b.Folds).Any(p => p.First.TestValue != p.Second.TestValue).Should().BeTrue();
    }
}
