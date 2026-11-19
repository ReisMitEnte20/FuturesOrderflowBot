using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using TradingBot.Application.Strategies.OrderFlow;
using TradingBot.DevDashboard.Services;
using TradingBot.DevDashboard.Services.Quant;
using TradingBot.DevDashboard.Services.Quant.Report;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.MarketData.Import;
using TradingBot.Quant.Report;
using TradingBot.Research.Orderflow;
using Xunit;
using Xunit.Abstractions;

namespace TradingBot.Tests.DevDashboard;

/// <summary>
/// Abnahme des Tick-Walk-forwards für DeltaFlow auf einer SYNTHETISCHEN, im Code erzeugten Mehrtages-Fixture
/// (11 Handelstage, 10.06. fehlt, letzter 2 Tage = finaler Holdout; Training 4 / Test 2 Handelstage).
/// Vier Nachweise: Parameter wirken · kein Blick in die Zukunft · Ausführung = direkter Orderflow-Pfad · Holdout bleibt zu.
/// </summary>
[Collection("ResearchSerial")]
public class OrderflowWalkForwardTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<string> _paths = new();
    public OrderflowWalkForwardTests(ITestOutputHelper output) => _out = output;
    public void Dispose()
    {
        foreach (var p in _paths) try { if (Directory.Exists(p)) Directory.Delete(p, true); else if (File.Exists(p)) File.Delete(p); } catch { }
        GC.SuppressFinalize(this);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradingBot.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private string Csv(IReadOnlyCollection<DateOnly>? mutate = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "mnq-wf-synthetic-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, SyntheticTickFixtures.MultiDayCsv(mutate));
        _paths.Add(path);
        return path;
    }

    private (BacktestingService svc, ResearchOrchestrator orch) New()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "of-wf-tests-" + Guid.NewGuid().ToString("N"));
        _paths.Add(tempRoot);
        var quant = new QuantApiService(new BacktestApiService(RepoRoot()), tempRoot);
        var orch = new ResearchOrchestrator(quant, new QuantJobManager(), new ResearchRunStore(quant.RegistryDir));
        return (new BacktestingService(quant, orch), orch);
    }

    private static BacktestingStartRequest Req(string csv) => new()
    {
        Strategy = "deltaflow", Params = new() { ["DeltaThreshold"] = "30", ["TicksPerBar"] = "500" },
        Symbol = "MNQ", DataSourceId = "ticks-csv:wf", Path = csv, Quantity = 1, InitialBalance = 10_000m, ApplyFees = true,
        HoldoutShare = 0.2, TrainSessions = 4, TestSessions = 2, Seed = 7, SimulationIterations = 200, MinTrades = 30, MinTrainTrades = 2
    };

    private async Task<ResearchRunRecord> ReportAsync(string csv)
    {
        var (svc, orch) = New();
        var start = await svc.StartAsync(Req(csv));
        start.Ok.Should().BeTrue(start.Error);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMinutes(3))
        {
            var r = (await orch.GetAsync(start.Run!.RunId)).Run!;
            if (r.Status is ResearchStepStatus.Completed or ResearchStepStatus.Failed or ResearchStepStatus.Cancelled)
            {
                r.Status.Should().Be(ResearchStepStatus.Completed, r.StatusReason + " | " + string.Join(" | ", r.Steps.Select(s => $"{s.Key}:{s.Status}:{s.Reason}")));
                return r;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException();
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private static T Cfg<T>(string rel) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(RepoRoot(), "config", rel)), Json)!;

    /// <summary>Direkter Orderflow-Pfad wie die Orderflow-Konsole, nur DeltaFlow mit der angegebenen Schwelle.</summary>
    private static async Task<OrderflowRunResult> DirectAsync(IReadOnlyList<MarketTick> ticks, decimal threshold)
    {
        var instrument = Cfg<InstrumentProfile>(@"instruments\mnq.example.json");
        var fee = Cfg<FeeProfile>(@"fees\amp-rithmic-mnq.example.json");
        var broker = Cfg<BrokerProfile>(@"brokers\amp-rithmic.example.json");
        var risk = Cfg<RiskConfig>(@"risk\research-sim.example.json");
        var cand = OrderflowProofCandidates.BuildGated("MNQ", threshold).Single(c => c.Name == "DeltaFlow(OF)");
        return await new OrderflowMultiStrategyRunner().RunAsync(new OrderflowRunRequest
        {
            Ticks = ticks, Symbol = "MNQ", Instrument = instrument, Fee = fee, Broker = broker, Risk = risk,
            Account = new TradingAccount { AccountId = "research-sim", BrokerName = broker.BrokerName, AccountType = broker.AccountType, Currency = instrument.Currency, StartingBalance = 10_000m },
            TicksPerBar = 500, Candidates = new[] { cand }, DataSource = "direkt", DataSha256 = "x", CapabilitiesSufficient = true, DeltaThreshold = threshold
        });
    }

    private static IReadOnlyList<MarketTick> Import(string csv) =>
        new AtasTickCsvImporter(Cfg<CsvImportProfile>(@"import-profiles\atas-tick.example.json")).ImportFile(csv).Ticks;

    private static OosVariant Rolling(ResearchRunRecord r) => r.Report!.Oos!.Variants.Single(v => v.Key == "rolling");

    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Parameters_act_two_thresholds_produce_different_signals_and_the_grid_is_not_one_hardwired_candidate()
    {
        var csv = Csv();
        var ticks = Import(csv);
        // Direkt über den bestehenden Pfad: dieselben Ticks, nur die Schwelle verschieden.
        var sig = new Dictionary<decimal, List<string>>();
        foreach (var th in new[] { 24m, 30m, 36m })
        {
            var r = await DirectAsync(ticks, th);
            sig[th] = r.Workspace.Strategies.Single().Trades.Select(t => $"{t.EntrySignal?.TimeUtc}|{t.EntrySignal?.Direction}").ToList();
            _out.WriteLine($"Schwelle {th}: {sig[th].Count} Einstiege, Signale gesamt {r.RunResults.Single().Diagnostics!.SignalsGenerated}");
        }
        sig.Values.Select(s => string.Join(",", s)).Distinct().Count().Should().BeGreaterThanOrEqualTo(2, "mindestens zwei Schwellen müssen unterschiedliche Signale erzeugen");

        // Im Bericht: Raster mit drei verschiedenen Schwellen, und die Kandidaten liefern im Training unterschiedliche Werte.
        var rep = await ReportAsync(csv);
        rep.Report!.Oos!.Grid.Select(g => g["DeltaThreshold"]).Should().BeEquivalentTo(new[] { "24", "30", "36" });
        var folds = Rolling(rep).Folds;
        folds.Should().HaveCountGreaterThanOrEqualTo(2);
        folds.Any(f => f.CandidateTrainValues.Values.Distinct().Count() >= 2 || f.CandidateTrainTrades.Values.Distinct().Count() >= 2)
            .Should().BeTrue("ein Raster, das intern immer denselben Kandidaten rechnet, hätte identische Trainingswerte");
    }

    [Fact]
    public async Task No_look_ahead_changing_only_a_later_test_window_keeps_earlier_choices_and_training()
    {
        var baseRep = await ReportAsync(Csv());
        var f = Rolling(baseRep).Folds;
        f.Should().HaveCountGreaterThanOrEqualTo(2);
        var laterTest = f[1].TestSessionIds.Select(d => DateOnly.Parse(d)).ToList();     // nur die Ticks des ZWEITEN Testfensters ändern
        var changed = await ReportAsync(Csv(laterTest));
        var g = Rolling(changed).Folds;

        for (int i = 0; i < 2; i++)
        {
            g[i].SelectedCandidate.Should().Be(f[i].SelectedCandidate, $"Fold {i}: Wahl hängt nur am Training");
            g[i].TrainValue.Should().Be(f[i].TrainValue);
            g[i].CandidateTrainValues.Should().BeEquivalentTo(f[i].CandidateTrainValues);
            g[i].TrainSessionIds.Should().Equal(f[i].TrainSessionIds);
        }
        g[0].TestNet.Should().Be(f[0].TestNet, "das erste Testfenster ist unverändert");
        g[1].TestNet.Should().NotBe(f[1].TestNet, "nur das geänderte (spätere) Testfenster darf sich ändern");
        _out.WriteLine($"Fold 2 Test-Netto vorher {f[1].TestNet} / nach Änderung {g[1].TestNet}");
    }

    [Fact]
    public async Task Execution_a_test_window_replayed_directly_with_the_frozen_winner_matches_the_report()
    {
        var csv = Csv();
        var rep = await ReportAsync(csv);
        var ticks = Import(csv);
        var v = Rolling(rep);
        _out.WriteLine("SYNTHETISCHER FUNKTIONSNACHWEIS — Training | Test | gewählte Schwelle | Trades | Netto | Netto Standard (30)");
        foreach (var f in v.Folds)
        {
            f.SelectedParameters.Should().NotBeNull($"Fold {f.Index} braucht einen Gewinner");
            decimal th = decimal.Parse(f.SelectedParameters!["DeltaThreshold"], System.Globalization.CultureInfo.InvariantCulture);
            var testTicks = ticks.Where(t => f.TestSessionIds.Contains(TradeDateCalendar.Key(t.Timestamp))).ToList();
            var direct = await DirectAsync(testTicks, th);
            var dTrades = direct.Workspace.Strategies.Single().Trades;
            var diag = direct.RunResults.Single().Diagnostics!;

            string? diff = null;
            if (dTrades.Count != f.TestTradeRows.Count) diff = $"Tradeanzahl direkt {dTrades.Count} vs. Bericht {f.TestTradeRows.Count}";
            for (int i = 0; diff is null && i < dTrades.Count; i++)
            {
                var d = dTrades[i]; var r = f.TestTradeRows[i];
                (string N, object? A, object? B)[] fields =
                {
                    ("Einstiegssignal", d.EntrySignal?.TimeUtc, r.EntrySignalUtc), ("Richtung", d.EntrySignal?.Direction, r.EntrySignalDirection),
                    ("Ausstiegssignal", d.ExitSignal?.TimeUtc, r.ExitSignalUtc), ("Entry-Fill", d.EntryTimeUtc, r.EntryTimeUtc), ("Exit-Fill", d.ExitTimeUtc, r.ExitTimeUtc),
                    ("Entry-Preis", d.EntryPrice, r.EntryPrice), ("Exit-Preis", d.ExitPrice, r.ExitPrice), ("Gebühren", d.Fees, r.Fees), ("Netto", d.NetPnL, r.Net)
                };
                foreach (var x in fields) if (!Equals(x.A, x.B)) { diff = $"Trade #{i + 1} {x.N}: direkt={x.A} vs. Bericht={x.B}"; break; }
            }
            diff.Should().BeNull($"Fold {f.Index}");
            f.TestSignals.Should().Be(diag.SignalsGenerated);
            f.TestFees.Should().Be(dTrades.Sum(t => t.Fees ?? 0m));
            f.TestNet.Should().Be(dTrades.Sum(t => t.NetPnL));
            // Kein vor dem Test eröffneter Trade: jeder Einstieg liegt nach dem ersten Tick des Testfensters.
            f.TestTradeRows.Should().OnlyContain(t => t.EntryT >= f.TestFromT);
            // Getestete Tage ohne Trade bleiben als Tag mit 0 Trades stehen.
            f.TestSessionResults.Select(s => s.Key).Should().Equal(f.TestSessionIds);
            _out.WriteLine($"{f.TrainSessionIds[0]}–{f.TrainSessionIds[^1]} ({f.TrainSessions} Tage) | {string.Join(", ", f.TestSessionIds)} | {th} | {f.TestTrades} | {f.TestNet} | {f.BaseNet}" +
                           (f.MissingDataDays.Count > 0 ? $" | fehlender Datentag {string.Join(", ", f.MissingDataDays)}" : "") +
                           $" | je Tag: {string.Join(", ", f.TestSessionResults.Select(s => $"{s.Key}:{s.Trades}T/{s.Net}"))}");
        }
    }

    [Fact]
    public async Task Holdout_stays_closed_changing_only_holdout_ticks_changes_nothing_and_no_holdout_performance_is_reported()
    {
        var baseRep = await ReportAsync(Csv());
        var holdout = baseRep.Report!.Oos!.HoldoutSessionIds;
        holdout.Should().Equal("2026-06-15", "2026-06-16");
        var changed = await ReportAsync(Csv(holdout.Select(d => DateOnly.Parse(d)).ToList()));

        foreach (var key in new[] { "split", "rolling", "anchored" })
        {
            var a = baseRep.Report.Oos.Variants.Single(v => v.Key == key);
            var b = changed.Report!.Oos!.Variants.Single(v => v.Key == key);
            b.Status.Should().Be(a.Status);
            b.Folds.Select(f => (f.SelectedCandidate, f.TrainValue, f.TestNet, f.TestTrades, f.BaseNet))
                .Should().Equal(a.Folds.Select(f => (f.SelectedCandidate, f.TrainValue, f.TestNet, f.TestTrades, f.BaseNet)));
            b.OosNet.Should().Be(a.OosNet);
        }
        changed.Report!.Overview.Figures.Select(x => (x.Key, x.Value)).Should().Equal(baseRep.Report.Overview.Figures.Select(x => (x.Key, x.Value)));
        // Keine Holdout-Performance: kein Trade, kein Fenster, keine Session-Zeile im Holdout.
        long hStart = baseRep.HoldoutFrom!.Value.ToUnixTimeMilliseconds();
        baseRep.Report.Trades!.Trades.Should().OnlyContain(t => t.EntryT < hStart);
        baseRep.Report.Oos.Variants.SelectMany(v => v.Folds).Should().OnlyContain(f => f.TestToT < hStart && !f.TestSessionIds.Intersect(holdout).Any());
        baseRep.Steps.Single(s => s.Key == "holdout").Status.Should().Be(ResearchStepStatus.NotApplicable);
        baseRep.HoldoutProposal.Should().BeNull();
        // Session-IDs sind gespeichert; der fehlende Werktag 10.06. ist als fehlender Datentag ausgewiesen.
        baseRep.Report.Oos.DevelopmentSessionIds.Should().HaveCount(9);
        baseRep.Report.Oos.MissingDataDays.Should().Equal("2026-06-10");
    }
}
