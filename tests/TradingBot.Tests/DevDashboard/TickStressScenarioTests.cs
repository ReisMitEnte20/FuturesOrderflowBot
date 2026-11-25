using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using TradingBot.Application.Strategies.OrderFlow;
using TradingBot.DevDashboard.Services;
using TradingBot.DevDashboard.Services.Quant;
using TradingBot.DevDashboard.Services.Quant.Report;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Research.Orderflow;
using Xunit;
using Xunit.Abstractions;

namespace TradingBot.Tests.DevDashboard;

/// <summary>
/// Kosten-/Verzögerungs-Stress als echte Neuläufe der bestehenden Tick-Engine (DeltaFlow, MNQ). SYNTHETISCHE, im Code erzeugte Ticks;
/// Szenarien sind Annahmen (kein gemessener Spread, keine gemessene Latenz). Erwartungswerte von Hand gerechnet:
/// MNQ Tick 0,25 · Punktwert 2,00 · Gebühr 0,39 je Seite · Slippage 1 Tick.
/// </summary>
[Collection("ResearchSerial")]
public class TickStressScenarioTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<string> _paths = new();
    public TickStressScenarioTests(ITestOutputHelper output) => _out = output;
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

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private static T Cfg<T>(string rel) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(RepoRoot(), "config", rel)), Json)!;

    /// <summary>
    /// 61 Ticks à 100 ms, Preis je Tick +0,25 ab 20 000,00 (Tick i = 20 000 + 0,25·i). Bars à 10 Ticks, Aggressor bestimmt das Delta:
    /// b0 Δ+10 → Long · b1 Δ+10 halten · b2 Δ0 → Flat · b3 Δ−10 → Short · b4 Δ0 → Flat · b5 Δ+10 → Long auf dem vorletzten Tick (59) ·
    /// Tick 60 = einziger Folge-Tick (Teilbar). Basis füllt das letzte Signal an Tick 60; mit +1 Tick Verzögerung fehlt Tick 61 → kein Fill.
    /// </summary>
    private static List<MarketTick> HandTicks()
    {
        var t0 = new DateTimeOffset(2026, 6, 1, 13, 30, 0, TimeSpan.Zero);
        int[] buysPerBar = { 10, 10, 5, 0, 5, 10 };
        var list = new List<MarketTick>();
        for (int i = 0; i <= 60; i++)
        {
            int bar = Math.Min(i / 10, 5); int k = i % 10;
            bool buy = i == 60 || buysPerBar[bar] == 10 || (buysPerBar[bar] == 5 && k % 2 == 0);
            list.Add(new MarketTick { Symbol = "MNQ", Timestamp = t0.AddMilliseconds(100 * i), Price = 20000m + 0.25m * i, Volume = 1, Aggressor = buy ? AggressorSide.Buy : AggressorSide.Sell });
        }
        return list;
    }

    private static async Task<OrderflowRunResult> RunAsync(IReadOnlyList<MarketTick> ticks, int delay = 0, decimal? feePerSide = null, decimal? slip = null)
    {
        var instrument = Cfg<InstrumentProfile>(@"instruments\mnq.example.json");
        var fee = Cfg<FeeProfile>(@"fees\amp-rithmic-mnq.example.json");
        if (feePerSide is decimal f) fee = fee with { CommissionPerSide = f, ExchangeFeePerSide = 0, ClearingFeePerSide = 0, RoutingFeePerSide = 0, NfaFeePerSide = 0, OtherFeePerSide = 0 };
        var broker = Cfg<BrokerProfile>(@"brokers\amp-rithmic.example.json");
        var risk = Cfg<RiskConfig>(@"risk\research-sim.example.json");
        var cand = OrderflowProofCandidates.BuildGated("MNQ", 5m).Single(c => c.Name == "DeltaFlow(OF)");
        return await new OrderflowMultiStrategyRunner().RunAsync(new OrderflowRunRequest
        {
            Ticks = ticks, Symbol = "MNQ", Instrument = instrument, Fee = fee, Broker = broker, Risk = risk,
            Account = new TradingAccount { AccountId = "research-sim", BrokerName = broker.BrokerName, AccountType = broker.AccountType, Currency = instrument.Currency, StartingBalance = 10_000m },
            TicksPerBar = 10, SlippageTicksOverride = slip, FillDelayTicks = delay, Candidates = new[] { cand }, DataSource = "direkt", DataSha256 = "x",
            CapabilitiesSufficient = true, DeltaThreshold = 5m
        });
    }

    private sealed record Row(string Name, int Closed, int Open, decimal Fees, decimal ClosedNet, decimal OpenVal, decimal Value, int Unfilled);
    private static Row Summ(string name, OrderflowRunResult r)
    {
        var ws = r.Workspace.Strategies.Single(); var rr = r.RunResults.Single();
        decimal open = ws.OpenPositionQty > 0 ? ws.UnrealizedGrossAtLastTick - ws.OpenPositionEntryFees : 0m;
        return new Row(name, rr.Trades.Count, ws.OpenPositionQty > 0 ? 1 : 0, ws.TotalFeesPaid, ws.RealizedNetPnL, open, ws.RealizedNetPnL + open, rr.Diagnostics!.OrdersUnfilledAtEnd);
    }

    [Fact]
    public async Task Hand_fixture_long_and_short_under_fee_slippage_and_delay_scenarios_matches_hand_calculation_and_invents_no_fill()
    {
        var ticks = HandTicks();
        var b = await RunAsync(ticks);
        var b0 = await RunAsync(ticks, delay: 0);
        var fee = await RunAsync(ticks, feePerSide: 0.89m);
        var slip = await RunAsync(ticks, slip: 2m);
        var del = await RunAsync(ticks, delay: 1);

        // Basis: Verzögerung 0 ist exakt das bisherige Verhalten.
        b0.RunResults.Single().Trades.Should().BeEquivalentTo(b.RunResults.Single().Trades);
        Summ("b0", b0).Should().Be(Summ("b0", b));

        var rows = new[] { Summ("Basis", b), Summ("Gebühr +0,50/Seite", fee), Summ("Slippage +1 Tick", slip), Summ("Verzögerung +1 Tick", del) };
        _out.WriteLine("SYNTHETISCH — Szenario | geschlossene Trades | offene Endposition | Gebühren | geschlossen | offen bewertet | Netto inkl. offen | Diff. zur Basis | ungefüllt");
        foreach (var r in rows) _out.WriteLine($"{r.Name} | {r.Closed} | {r.Open} | {r.Fees} | {r.ClosedNet} | {r.OpenVal} | {r.Value} | {r.Value - rows[0].Value} | {r.Unfilled}");

        // Handrechnung (siehe Klassenkommentar):
        // Basis:  Long 20002,75→20007,25 = +9,00−0,78 = 8,22 · Short 20009,75→20012,75 = −6,00−0,78 = −6,78 · offen Long 20015,25, letzter Tick 20015,00 → −0,50−0,39 = −0,89
        rows[0].Should().Be(new Row("Basis", 2, 1, 1.95m, 1.44m, -0.89m, 0.55m, 0));
        // Gebühr 0,89/Seite: 7,22 − 7,78 = −0,56; offen −0,50 − 0,89 = −1,39; Gebühren 5 Seiten · 0,89 = 4,45 → −1,95 (= −0,50 · 5 Seiten)
        rows[1].Should().Be(new Row("Gebühr +0,50/Seite", 2, 1, 4.45m, -0.56m, -1.39m, -1.95m, 0));
        // Slippage 2 Ticks: Long 20003,00→20007,00 = 8,00−0,78 = 7,22 · Short 20009,50→20013,00 = −7,00−0,78 = −7,78 · offen @20015,50 → −1,00−0,39 = −1,39
        rows[2].Should().Be(new Row("Slippage +1 Tick", 2, 1, 1.95m, -0.56m, -1.39m, -1.95m, 0));
        // Verzögerung 1 Tick: Long 20003,00→20007,50 = 9,00−0,78 = 8,22 · Short 20010,00→20013,00 = −6,00−0,78 = −6,78 ·
        // letztes Long-Signal (Tick 59) bräuchte Tick 61 → existiert nicht → KEIN Fill, flach, 1 Order ungefüllt, keine Einstiegsgebühr.
        rows[3].Should().Be(new Row("Verzögerung +1 Tick", 2, 0, 1.56m, 1.44m, 0m, 1.44m, 1));

        // Konkrete Fills: Basis am Folge-Tick des Signals, verzögert genau einen Trade-Tick später (Long UND Short).
        var bt = b.Workspace.Strategies.Single().Trades; var dt = del.Workspace.Strategies.Single().Trades;
        bt.Select(t => (t.Side, t.EntryTickIndex, t.ExitTickIndex, t.EntryPrice, t.ExitPrice)).Should().Equal(
            ("Long", 10, 30, 20002.75m, 20007.25m), ("Short", 40, 50, 20009.75m, 20012.75m));
        dt.Select(t => (t.Side, t.EntryTickIndex, t.ExitTickIndex, t.EntryPrice, t.ExitPrice)).Should().Equal(
            ("Long", 11, 31, 20003.00m, 20007.50m), ("Short", 41, 51, 20010.00m, 20013.00m));
        dt.Should().OnlyContain(t => t.SignalLink == TradeSignalLinker.Linked, "Signale bleiben mit der Verzögerung eindeutig zuordenbar");
        for (int i = 0; i < 2; i++)
        {
            dt[i].EntrySignal!.TimeUtc.Should().Be(bt[i].EntrySignal!.TimeUtc, "gleiches Signal, nur der Fill verschiebt sich");
            DateTimeOffset.Parse(dt[i].EntryTimeUtc!).Should().Be(DateTimeOffset.Parse(bt[i].EntryTimeUtc!).AddMilliseconds(100));
            _out.WriteLine($"{bt[i].Side}: Signal {bt[i].EntrySignal!.TimeUtc} | Basis-Fill Tick {bt[i].EntryTickIndex} {bt[i].EntryTimeUtc} @ {bt[i].EntryPrice} | " +
                           $"verzögert Tick {dt[i].EntryTickIndex} {dt[i].EntryTimeUtc} @ {dt[i].EntryPrice} | Netto {bt[i].NetPnL} → {dt[i].NetPnL}");
        }
    }

    // ---- Bericht (Ein-Knopf) auf der synthetischen Mehrtages-Fixture --------------------------------------------------------------

    private string Csv()
    {
        var path = Path.Combine(Path.GetTempPath(), "mnq-stress-synthetic-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, SyntheticTickFixtures.MultiDayCsv());
        _paths.Add(path);
        return path;
    }

    private async Task<ResearchRunRecord> ReportAsync(string csv)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "of-stress-tests-" + Guid.NewGuid().ToString("N"));
        _paths.Add(tempRoot);
        var quant = new QuantApiService(new BacktestApiService(RepoRoot()), tempRoot);
        var orch = new ResearchOrchestrator(quant, new QuantJobManager(), new ResearchRunStore(quant.RegistryDir));
        var svc = new BacktestingService(quant, orch);
        var start = await svc.StartAsync(new BacktestingStartRequest
        {
            Strategy = "deltaflow", Params = new() { ["DeltaThreshold"] = "30", ["TicksPerBar"] = "500" },
            Symbol = "MNQ", DataSourceId = "ticks-csv:stress", Path = csv, Quantity = 1, InitialBalance = 10_000m, ApplyFees = true,
            HoldoutShare = 0.2, TrainSessions = 4, TestSessions = 2, Seed = 7, SimulationIterations = 200, MinTrades = 30, MinTrainTrades = 2
        });
        start.Ok.Should().BeTrue(start.Error);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMinutes(3))
        {
            var r = (await orch.GetAsync(start.Run!.RunId)).Run!;
            if (r.Status is ResearchStepStatus.Completed or ResearchStepStatus.Failed or ResearchStepStatus.Cancelled)
            {
                r.Status.Should().Be(ResearchStepStatus.Completed, r.StatusReason);
                return r;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException();
    }

    [Fact]
    public async Task Report_stress_table_reruns_the_frozen_winners_base_reproduces_walk_forward_and_costs_scale_per_executed_side()
    {
        var rep = await ReportAsync(Csv());
        var v = rep.Report!.Oos!.Variants.Single(x => x.Key == "rolling");
        var s = rep.Report.Oos.TickStress!;
        s.Status.Should().Be("Completed", s.Reason);
        s.BaseReproducesWalkForward.Should().BeTrue();
        s.Rows.Select(r => r.Key).Should().Equal("base", "fee", "slippage", "delay");

        _out.WriteLine("SYNTHETISCH (mnq-mehrtage, rollierend, eingefrorene Gewinner) — Szenario | geschl. Trades | offene Endpos. | Gebühren | Netto inkl. offen | Diff. | ungefüllt");
        foreach (var r in s.Rows) _out.WriteLine($"{r.Label} | {r.ClosedTrades} | {r.OpenEndPositions} | {r.Fees:0.00} | {r.Value:0.00} | {r.DiffToBase:0.00} | {r.UnfilledOrdersAtEnd}");

        var b = s.Rows[0];
        b.ClosedTrades.Should().Be(v.OosTrades); b.ClosedNet.Should().Be(v.OosNet); b.Value.Should().Be(v.OosValue!.Value); b.DiffToBase.Should().Be(0);
        b.Windows.Should().Be(v.Folds.Count(f => f.SelectedParameters is not null));
        int sides = 2 * b.ClosedTrades + b.OpenEndPositions;
        b.Fees.Should().Be(0.39m * sides, "Gebühr je tatsächlich ausgeführter Seite");
        // Gebühr +0,50 und +1 Tick Slippage (MNQ: 0,25 · 2,00 = 0,50 je Seite) verändern keine Fill-Zeitpunkte → gleiche Trades,
        // Wirkung genau −0,50 je ausgeführter Seite (bei der offenen Position nur die Einstiegsseite).
        foreach (var key in new[] { "fee", "slippage" })
        {
            var r = s.Rows.Single(x => x.Key == key);
            r.ClosedTrades.Should().Be(b.ClosedTrades); r.OpenEndPositions.Should().Be(b.OpenEndPositions);
            r.DiffToBase.Should().Be(-0.50m * sides, key);
        }
        s.Rows.Single(x => x.Key == "fee").Fees.Should().Be(0.89m * sides);
        // Ein Trade mit dem verschobenen Fill: verzögert genau ein Tick später, gleiches Signal.
        var ex = s.FillExample!;
        ex.DelayedEntryTick.Should().Be(ex.BaseEntryTick + 1);
        ex.DelayedExitTick.Should().Be(ex.BaseExitTick + 1);
        _out.WriteLine($"Beispiel {ex.Window} {ex.Side}: Signal {ex.EntrySignalUtc} | Basis Tick {ex.BaseEntryTick} {ex.BaseEntryUtc} @ {ex.BaseEntryPrice} → " +
                       $"verzögert Tick {ex.DelayedEntryTick} {ex.DelayedEntryUtc} @ {ex.DelayedEntryPrice} | Exit {ex.BaseExitPrice} → {ex.DelayedExitPrice} | Netto {ex.BaseNet} → {ex.DelayedNet}");
        s.Notes.Should().Contain(n => n.Contains("ANNAHMEN") && n.Contains("keine gemessene Latenz"));
        rep.Report.Controls!.Notes.Should().NotContain(n => n.Contains("Slippage/Latenz im Tick-Pfad NICHT angebunden"));
    }
}
