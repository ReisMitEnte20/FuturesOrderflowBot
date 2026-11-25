using System.Diagnostics;
using System.Globalization;
using System.Text;
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
/// Offene Positionen an Fenstergrenzen (Tick-Walk-forward, DeltaFlow, MNQ). Kleine, im Code erzeugte, SYNTHETISCHE Ticks; alle
/// Erwartungswerte von Hand gerechnet (MNQ: Tick 0,25 · Punktwert 2,00 · Gebühr 0,39 je Seite · Slippage 1 Tick).
/// Regel: Fensterwert = Netto geschlossener Trades + (unrealisiert brutto zum letzten Trade-Tick des Fensters − bereits gezahlte
/// Einstiegsgebühr). Bewertung, kein Exit: kein Fill, kein Journal-Eintrag, keine Exit-Gebühr.
/// </summary>
[Collection("ResearchSerial")]
public class WindowEndValuationTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<string> _paths = new();
    public WindowEndValuationTests(ITestOutputHelper output) => _out = output;
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

    // ---- Tick-Generator: Bars à 10 Ticks, Volumen 1, 100 ms Abstand ----------------------------------------------------------

    /// <summary>Eine Bar: <paramref name="buys"/> Käufer-Ticks von 10, Preise je Tick (Länge 10).</summary>
    private sealed record Bar(int Buys, decimal[] Prices);
    private static Bar Flat(int buys, decimal price) => new(buys, Enumerable.Repeat(price, 10).ToArray());

    private static IEnumerable<(DateTimeOffset T, decimal P, bool Buy)> Ticks(DateOnly day, IEnumerable<Bar> bars)
    {
        var t0 = new DateTimeOffset(day.ToDateTime(new TimeOnly(13, 30)), TimeSpan.Zero);
        int idx = 0;
        foreach (var b in bars)
        {
            // Käufer zuerst auf Position 0, Verkäufer ab Position 1 (das Delta der Bar ist exakt Käufer − Verkäufer).
            int sells = 10 - b.Buys;
            for (int i = 0; i < 10; i++)
            {
                bool buy = !(i >= 1 && i <= sells);
                if (b.Buys == 0) buy = false;
                yield return (t0.AddMilliseconds(100 * idx++), b.Prices[i], buy);
            }
        }
    }

    private static List<MarketTick> ToTicks(IEnumerable<(DateTimeOffset T, decimal P, bool Buy)> rows) =>
        rows.Select(r => new MarketTick { Symbol = "MNQ", Timestamp = r.T, Price = r.P, Volume = 1, Aggressor = r.Buy ? AggressorSide.Buy : AggressorSide.Sell }).ToList();

    private string Csv(IEnumerable<(DateTimeOffset T, decimal P, bool Buy)> rows)
    {
        var sb = new StringBuilder("Time,Instrument,Price,Volume,AggressorSide\n");
        foreach (var r in rows)
            sb.Append(r.T.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("Z,MNQ,")
              .Append(r.P.ToString("F2", CultureInfo.InvariantCulture)).Append(",1,").Append(r.Buy ? "Buy" : "Sell").Append('\n');
        var path = Path.Combine(Path.GetTempPath(), "mnq-wev-synthetic-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, sb.ToString());
        _paths.Add(path);
        return path;
    }

    private static async Task<OrderflowRunResult> DirectAsync(IReadOnlyList<MarketTick> ticks, decimal threshold, int tpb = 10)
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
            TicksPerBar = tpb, Candidates = new[] { cand }, DataSource = "direkt", DataSha256 = "x", CapabilitiesSufficient = true, DeltaThreshold = threshold
        });
    }

    // ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Hand_example_open_loss_and_mirrored_open_gain_are_valued_at_the_last_tick_minus_the_paid_entry_fee_without_a_fill()
    {
        var day = new DateOnly(2026, 6, 1);
        var down = Enumerable.Range(0, 10).Select(i => 20000m - 0.25m * i).ToArray();   // 20000,00 … 19997,75

        // Long: Bar 0 Delta +10 → Long-Signal; Fill am 1. Tick von Bar 1: 20000,00 + 0,25 = 20000,25. Bar 1 Delta +10 → halten.
        // Letzter Tick 19997,75 → (19997,75 − 20000,25) · 2 = −5,00; Einstiegsgebühr 0,39 → Bewertung −5,39.
        var longLoss = await DirectAsync(ToTicks(Ticks(day, new[] { Flat(10, 20000m), new Bar(10, down) })), 5m);
        // Spiegelfall Short: Bar 0 Delta −10 → Short; Fill 20000,00 − 0,25 = 19999,75; letzter Tick 19997,75
        // → (19999,75 − 19997,75) · 2 = +4,00; − 0,39 → Bewertung +3,61.
        var shortGain = await DirectAsync(ToTicks(Ticks(day, new[] { Flat(0, 20000m), new Bar(0, down) })), 5m);

        foreach (var (name, run, side, avg, unreal, value) in new[]
        {
            ("Long, offener Verlust", longLoss, "Long", 20000.25m, -5.00m, -5.39m),
            ("Short, offener Gewinn", shortGain, "Short", 19999.75m, 4.00m, 3.61m)
        })
        {
            var ws = run.Workspace.Strategies.Single();
            var rr = run.RunResults.Single();
            _out.WriteLine($"{name}: Hand Einstieg {avg} / unrealisiert {unreal} / Gebühr 0,39 / Bewertung {value} | Engine Einstieg {ws.OpenPositionAvgPrice} / " +
                           $"letzter Tick {ws.LastTickPrice} / unrealisiert {ws.UnrealizedGrossAtLastTick} / Einstiegsgebühr {ws.OpenPositionEntryFees} / " +
                           $"Bewertung {ws.EndEquityMarkToMarket - 10_000m} / geschlossene Trades {rr.Trades.Count}");
            ws.OpenPositionSide.Should().Be(side);
            ws.OpenPositionQty.Should().Be(1);
            ws.OpenPositionAvgPrice.Should().Be(avg);
            ws.LastTickPrice.Should().Be(19997.75m, "Bewertung am letzten Trade-Tick INNERHALB des Fensters");
            ws.UnrealizedGrossAtLastTick.Should().Be(unreal);
            ws.OpenPositionEntryFees.Should().Be(0.39m, "genau eine bereits gezahlte Seite, keine hypothetische Exit-Gebühr");
            (ws.EndEquityMarkToMarket - 10_000m).Should().Be(value);
            rr.Trades.Should().BeEmpty("die Bewertung ist kein Exit — kein fiktiver Fill im Journal");
            rr.Diagnostics!.OrdersUnfilledAtEnd.Should().Be(0);
        }
    }

    // ---- Auswahl-Fixture -------------------------------------------------------------------------------------------------------
    // Tag „S“ (7 Bars à 10 Ticks, Basis-Schwelle 5 → Raster 4/5/6; 4 und 5 handeln ab Delta +6, 6 erst ab Delta +8):
    //  b0 Δ+10 @20000 → alle Long        b1 Δ+10 @20000 → Fill 20000,25, halten     b2 Δ0 @20010 → alle Flat
    //  b3 Δ+6  @20010 → Exit 20009,75 (alle: +19,00 brutto, 18,22 netto); nur 4/5: Long
    //  b4 Δ0   @20010 → 4/5 Fill 20010,25, dann Flat                                    b5 Δ+6 @20015 → 4/5 Exit 20014,75 (+9,00 / 8,22); 4/5 Long
    //  b6 Δ+6, 1. Tick 20015, danach 20000 → 4/5 Fill 20015,25, offen; letzter Tick 20000,00 → −30,50 brutto − 0,39 = −30,89
    // Nur geschlossene Trades: 4/5 = 26,44 > 6 = 18,22 (wäre die Wahl). Inkl. offener Bewertung: 4/5 = −4,45 < 6 = 18,22 → Wahl 6.
    private static Bar[] DayS => new[]
    {
        Flat(10, 20000m), Flat(10, 20000m), Flat(5, 20010m), Flat(8, 20010m), Flat(5, 20010m), Flat(8, 20015m),
        new Bar(8, new[] { 20015m }.Concat(Enumerable.Repeat(20000m, 9)).ToArray())
    };
    private static readonly DateOnly[] Days = { new(2026, 6, 1), new(2026, 6, 2), new(2026, 6, 3) };

    private (BacktestingService svc, ResearchOrchestrator orch) New()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "of-wev-tests-" + Guid.NewGuid().ToString("N"));
        _paths.Add(tempRoot);
        var quant = new QuantApiService(new BacktestApiService(RepoRoot()), tempRoot);
        var orch = new ResearchOrchestrator(quant, new QuantJobManager(), new ResearchRunStore(quant.RegistryDir));
        return (new BacktestingService(quant, orch), orch);
    }

    private async Task<ResearchRunRecord> ReportAsync(string csv)
    {
        var (svc, orch) = New();
        var start = await svc.StartAsync(new BacktestingStartRequest
        {
            Strategy = "deltaflow", Params = new() { ["DeltaThreshold"] = "5", ["TicksPerBar"] = "10" },
            Symbol = "MNQ", DataSourceId = "ticks-csv:wev", Path = csv, Quantity = 1, InitialBalance = 10_000m, ApplyFees = true,
            HoldoutShare = 0, TrainSessions = 1, TestSessions = 1, Seed = 7, SimulationIterations = 200, MinTrades = 1, MinTrainTrades = 1
        });
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

    [Fact]
    public async Task Selection_follows_net_including_open_valuation_and_a_window_without_open_position_keeps_its_closed_net()
    {
        // Direkter Engine-Nachweis der Handrechnung auf einem Tag „S“.
        var dayTicks = ToTicks(Ticks(Days[0], DayS));
        foreach (var (th, closed, open) in new[] { (4m, 26.44m, -30.89m), (5m, 26.44m, -30.89m), (6m, 18.22m, 0m) })
        {
            var ws = (await DirectAsync(dayTicks, th)).Workspace.Strategies.Single();
            _out.WriteLine($"Schwelle {th}: geschlossen {ws.RealizedNetPnL} ({ws.Trades.Count} Trades) | offen {ws.OpenPositionSide} @ {ws.OpenPositionAvgPrice}, " +
                           $"unrealisiert {ws.UnrealizedGrossAtLastTick} − Gebühr {ws.OpenPositionEntryFees} | Fensterwert {ws.EndEquityMarkToMarket - 10_000m}");
            ws.RealizedNetPnL.Should().Be(closed);
            (ws.EndEquityMarkToMarket - 10_000m - ws.RealizedNetPnL).Should().Be(open);
        }

        var rows = Days.SelectMany(d => Ticks(d, DayS));
        var rep = await ReportAsync(Csv(rows));
        var oos = rep.Report!.Oos!;
        var v = oos.Variants.Single(x => x.Key == "rolling");
        v.Status.Should().Be("Completed", v.Reason);
        v.Folds.Should().HaveCount(2);
        oos.SelectionFigure.Should().Contain("inkl. offener Bewertung");
        oos.WindowEndRule.Should().Contain("LETZTEN Trade-Tick").And.Contain("Einstiegsgebühr");

        foreach (var f in v.Folds)
        {
            _out.WriteLine($"Fold {f.Index}: Training {string.Join(",", f.TrainSessionIds)} → Test {string.Join(",", f.TestSessionIds)} | Trainingswerte " +
                           string.Join(", ", f.CandidateTrainValues.Select(kv => $"{kv.Key}={kv.Value}")) + $" | gewählt {f.SelectedParameters!["DeltaThreshold"]} | " +
                           $"Test geschlossen {f.TestNet} + offen {f.TestOpenValuation} = {f.TestValue} | Standard (5) geschlossen {f.BaseNet} + offen {f.BaseOpenValuation} = {f.BaseValue}");
            // Trainingsscore = Netto inkl. offener Bewertung: 4/5 → −4,45, 6 → 18,22 → Wahl 6 (nach Realisiert-Regel wäre es 4 gewesen).
            f.CandidateTrainValues.Values.Should().BeEquivalentTo(new double?[] { -4.45, -4.45, 18.22 });
            f.SelectedParameters!["DeltaThreshold"].Should().Be("6");
            f.TrainValue.Should().Be(18.22);
            // Fenster ohne offene Position: Wert = bisheriges (geschlossenes) Netto, unverändert.
            f.TestNet.Should().Be(18.22m); f.TestOpenValuation.Should().Be(0m); f.TestValue.Should().Be(18.22m); f.OpenPositionAtTestEnd.Should().BeNull();
            f.TestFees.Should().Be(0.78m); f.TestTrades.Should().Be(1);
            // Standardparameter: geschlossen 26,44, offener Verlust −30,89 (Einstiegsgebühr 0,39 enthalten) → −4,45.
            f.BaseNet.Should().Be(26.44m); f.BaseOpenValuation.Should().Be(-30.89m); f.BaseValue.Should().Be(-4.45m); f.BaseTrades.Should().Be(2);
            f.BaseOpenPositionAtTestEnd.Should().Contain("Long 1 @ 20015.25").And.Contain("20000");
            f.BaseTestSessionResults.Single().Should().Be(new SessionResult(f.TestSessionIds.Single(), -4.45m, 2, -30.89m));
        }
        // Folgefenster startet flach: der Standardlauf am 03.06. ist identisch zum 02.06. (keine übertragene Position, kein Exit-Fill im 2. Fenster).
        v.Folds[1].BaseNet.Should().Be(v.Folds[0].BaseNet);
        v.Folds[1].BaseValue.Should().Be(v.Folds[0].BaseValue);

        // Mehrfenster-Zusammenfassung und Übersicht auf derselben Basis.
        v.OosNet.Should().Be(36.44m); v.OosOpenValuation.Should().Be(0m); v.OosValue.Should().Be(36.44m); v.OosOpenWindowEnds.Should().Be(0);
        v.BaseNetSameSessions.Should().Be(52.88m); v.BaseOpenValuationSameSessions.Should().Be(-61.78m); v.BaseValueSameSessions.Should().Be(-8.90m);
        v.OosSessionResults.Sum(s => s.Net).Should().Be(v.OosValue!.Value);
        v.BaseSessionResults.Sum(s => s.Net).Should().Be(v.BaseValueSameSessions!.Value);
        // Drawdown der Standardparameter enthält die offenen Verluste: Kurve 10 000 → +18,22 → +8,22 (Hoch 10 026,44) → −30,89 → +18,22 → +8,22
        // → −30,89 = 9 991,10 → max. Drawdown 35,34. Nur geschlossene Trades wären 0 gewesen.
        v.BaseMaxDrawdownSameSessions.Should().BeApproximately(35.34, 1e-6);
        v.OosMaxDrawdown.Should().Be(0);
        rep.Report.Overview.Figures.Single(x => x.Key == "oos").Value.Should().Be(36.44);
        // Kein fiktiver Journal-Fill: OOS-Trades = nur die geschlossenen Trades des Gewinners.
        v.OosTrades.Should().Be(2);
        v.Folds.SelectMany(f => f.TestTradeRows).Should().HaveCount(2);
    }
}
