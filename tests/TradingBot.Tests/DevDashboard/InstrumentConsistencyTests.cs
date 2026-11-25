using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using TradingBot.DevDashboard.Services;
using TradingBot.DevDashboard.Services.Quant;
using TradingBot.DevDashboard.Services.Quant.Report;
using TradingBot.Domain.Models;
using TradingBot.Research.Orderflow;
using Xunit;
using Xunit.Abstractions;

namespace TradingBot.Tests.DevDashboard;

/// <summary>
/// Instrument-Widerspruch Formular ↔ Datensatz (z. B. MES gewählt, Datensatz enthält MNQ-Ticks). Nur SYNTHETISCHE Daten.
/// MNQ: Tick 0,25 · Tickwert 0,50 · Punktwert 2,00 · Gebühr 0,39/Seite. MES: Tick 0,25 · Tickwert 1,25 · Punktwert 5,00 —
/// gleiche Tickgröße, daher beweist nur ein Geldbetrag (Punktwert) das richtige Profil.
/// </summary>
[Collection("ResearchSerial")]
public class InstrumentConsistencyTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<string> _paths = new();
    public InstrumentConsistencyTests(ITestOutputHelper output) => _out = output;
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

    private (BacktestingService svc, ResearchOrchestrator orch, QuantApiService quant) New()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "of-instr-tests-" + Guid.NewGuid().ToString("N"));
        _paths.Add(tempRoot);
        var quant = new QuantApiService(new BacktestApiService(RepoRoot()), tempRoot);
        var orch = new ResearchOrchestrator(quant, new QuantJobManager(), new ResearchRunStore(quant.RegistryDir));
        return (new BacktestingService(quant, orch), orch, quant);
    }

    private static BacktestingStartRequest Req(string symbol, string source, string? path = null, string threshold = "30", string tpb = "500") => new()
    {
        Strategy = "deltaflow", Params = new() { ["DeltaThreshold"] = threshold, ["TicksPerBar"] = tpb },
        Symbol = symbol, DataSourceId = source, Path = path, Quantity = 1, InitialBalance = 10_000m, ApplyFees = true,
        HoldoutShare = 0, TrainSessions = 4, TestSessions = 2, Seed = 7, SimulationIterations = 200, MinTrades = 1, MinTrainTrades = 1
    };

    /// <summary>
    /// Handgerechnete Ein-Tages-Fixture (61 MNQ-Ticks, Preis Tick i = 20 000 + 0,25·i, 10-Tick-Bars, Schwelle 5):
    /// Long 20002,75 → 20007,25 und Short 20009,75 → 20012,75 (Fill = Folge-Tick ± 1 Tick Slippage).
    /// </summary>
    private string HandCsv(string symbol = "MNQ")
    {
        var sb = new StringBuilder("Time,Instrument,Price,Volume,AggressorSide\n");
        var t0 = new DateTimeOffset(2026, 6, 1, 13, 30, 0, TimeSpan.Zero);
        int[] buys = { 10, 10, 5, 0, 5, 10 };
        for (int i = 0; i <= 60; i++)
        {
            int bar = Math.Min(i / 10, 5), k = i % 10;
            bool buy = i == 60 || buys[bar] == 10 || (buys[bar] == 5 && k % 2 == 0);
            sb.Append(t0.AddMilliseconds(100 * i).ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("Z,").Append(symbol).Append(',')
              .Append((20000m + 0.25m * i).ToString("F2", CultureInfo.InvariantCulture)).Append(",1,").Append(buy ? "Buy" : "Sell").Append('\n');
        }
        var path = Path.Combine(Path.GetTempPath(), "mnq-instr-synthetic-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, sb.ToString());
        _paths.Add(path);
        return path;
    }

    private static async Task<ResearchRunRecord> WaitAsync(ResearchOrchestrator orch, string runId)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMinutes(3))
        {
            var r = (await orch.GetAsync(runId)).Run!;
            if (r.Status is ResearchStepStatus.Completed or ResearchStepStatus.Failed or ResearchStepStatus.Cancelled) return r;
            await Task.Delay(50);
        }
        throw new TimeoutException();
    }

    [Fact]
    public async Task Before_the_fix_MES_on_MNQ_ticks_was_an_empty_run_the_engine_filtered_every_tick_by_symbol()
    {
        // Bisheriger Engine-Pfad unverändert: MES-Profil + MNQ-Ticks → die tick-getriebene Engine abonniert "MES" und sieht 0 Ticks.
        var api = new BacktestApiService(RepoRoot());
        var run = new BacktestRunRequest { DataSourceId = SyntheticTickFixtures.MultiDayId, Symbol = "MES", Strategy = "deltaflow", Params = new() { ["DeltaThreshold"] = "30" } };
        var lt = api.Ticks.Load(run);
        var mes = Cfg<InstrumentProfile>(@"instruments\mes.example.json");
        var mesFee = Cfg<FeeProfile>(@"fees\amp-rithmic-mes.example.json");
        var res = await OrderflowTickBacktest.RunAsync(api.Ticks.BuildRequest(run, lt.Ticks, mes, mesFee, lt.Source, lt.Sha256));
        var rr = res.RunResults.Single();
        _out.WriteLine($"MES auf {lt.Ticks.Count} MNQ-Ticks: Signale {rr.Diagnostics!.SignalsGenerated}, Trades {rr.Trades.Count}, Punktwert im Profil {mes.PointValue}");
        rr.Diagnostics!.SignalsGenerated.Should().Be(0);
        rr.Trades.Should().BeEmpty();
        // Jetzt: genau dieser Request wird vor jedem Engine-Lauf gesperrt.
        var act = () => api.EnsureInstrumentMatches(run);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Instrument-Widerspruch*MNQ*MES*");
    }

    [Fact]
    public async Task Preview_and_start_refuse_MES_with_a_known_MNQ_source_before_any_engine_run()
    {
        var (svc, orch, quant) = New();
        var p = await svc.PreviewAsync(Req("MES", SyntheticTickFixtures.MultiDayId));
        p.CanStart.Should().BeFalse();
        p.Requirements[0].Requirement.Should().Be("Instrument passt zum Datensatz");
        p.Requirements[0].Met.Should().BeFalse();
        p.Requirements[0].Detail.Should().Contain("MNQ").And.Contain("MES");
        p.DataInstrument!.Symbol.Should().Be("MNQ");

        long runsBefore = quant.Backtest.EngineRuns;
        var start = await svc.StartAsync(Req("MES", SyntheticTickFixtures.MultiDayId));
        _out.WriteLine("Start MES + synthetische MNQ-Quelle: " + start.Error);
        start.Ok.Should().BeFalse();
        start.Error.Should().Contain("Instrument").And.Contain("MNQ").And.Contain("MES");
        start.Run.Should().BeNull();
        (await orch.ListAsync(null)).Should().BeEmpty("kein Auftrag wird angelegt");
        quant.Backtest.EngineRuns.Should().Be(runsBefore);

        // Auch eine Tick-CSV (Instrument aus den Ticks selbst) und die Sierra-Quelle (Kontrakt MESM26) — ohne Datei-Zugriff auf Sierra.
        (await svc.StartAsync(Req("MES", "ticks-csv:hand", HandCsv()))).Error.Should().Contain("Datensatz enthält MNQ");
        var sierra = () => quant.Backtest.LoadContextAsync(new BacktestRunRequest { DataSourceId = "sierra-mesm26", Symbol = "MNQ", Strategy = "movingaverage" });
        await sierra.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Datensatz enthält MES*angefragt ist MNQ*");
        // Chart-Backtest (eigener Ladeweg) ebenso gesperrt.
        var chart = await quant.Backtest.RunAsync(new BacktestRunRequest { DataSourceId = "sierra-mesm26", Symbol = "MNQ", Strategy = "movingaverage" });
        chart.Ok.Should().BeFalse();
        chart.Error.Should().Contain("Instrument-Widerspruch");
        quant.Backtest.EngineRuns.Should().Be(runsBefore);
    }

    [Fact]
    public async Task Orchestrator_bypass_fails_without_running_and_the_run_list_flags_the_contradiction_without_rewriting()
    {
        var (svc, orch, _) = New();
        var good = await svc.BuildStartRequestAsync(Req("MNQ", SyntheticTickFixtures.MultiDayId));
        // Formular umgangen: dieselbe eingefrorene Konfiguration, nur das Instrument ausgetauscht.
        var bad = good with { Run = good.Run with { Symbol = "MES" }, Campaign = good.Campaign with { Id = good.Campaign.Id + "-mes" } };
        var res = await orch.StartAsync(bad);
        var rec = await WaitAsync(orch, res.Run!.RunId);
        rec.Status.Should().Be(ResearchStepStatus.Failed);
        rec.StatusReason.Should().Contain("Instrument-Widerspruch");
        rec.Report?.Result.Should().BeNull("es wurde nichts gerechnet");
        var row = (await svc.ListAsync()).Single(x => x.RunId == rec.RunId);
        row.Symbol.Should().Be("MES", "gespeicherte Konfiguration bleibt, wie sie war");
        row.InstrumentNote.Should().Contain("Datensatz enthält MNQ");
    }

    [Fact]
    public async Task Valid_MNQ_hand_example_through_the_one_click_report_shows_MNQ_point_value_in_money()
    {
        var (svc, orch, _) = New();
        var csv = HandCsv();
        var req = Req("MNQ", "ticks-csv:hand", csv, threshold: "5", tpb: "10");
        var p = await svc.PreviewAsync(req);
        p.CanStart.Should().BeTrue(string.Join(" | ", p.Requirements.Select(r => r.Requirement + ": " + r.Detail)));
        p.Costs!.PointValue.Should().Be(2m); p.Costs.TickValue.Should().Be(0.50m); p.Costs.FeePerSide.Should().Be(0.39m);

        var start = await svc.StartAsync(req);
        start.Ok.Should().BeTrue(start.Error);
        var rec = await WaitAsync(orch, start.Run!.RunId);
        rec.Status.Should().Be(ResearchStepStatus.Completed, rec.StatusReason);
        var prov = rec.Report!.Provenance;
        prov.Symbol.Should().Be("MNQ");
        prov.CostModel!.PointValue.Should().Be(2m);
        prov.InstrumentCheck.Should().Contain("Instrument MNQ = Datensatz");

        var t = rec.Report.Trades!.Trades;
        _out.WriteLine("Hand (MNQ, 1 Kontrakt) | Engine-Bericht");
        _out.WriteLine("Long 20002,75→20007,25 = 4,50 Pkt × 2,00 = 9,00 brutto − 0,78 = 8,22 netto | " + $"{t[0].Side} {t[0].EntryPrice}→{t[0].ExitPrice} brutto {t[0].Gross} Gebühren {t[0].Fees} netto {t[0].Net}");
        _out.WriteLine("Short 20009,75→20012,75 = −3,00 Pkt × 2,00 = −6,00 brutto − 0,78 = −6,78 netto | " + $"{t[1].Side} {t[1].EntryPrice}→{t[1].ExitPrice} brutto {t[1].Gross} Gebühren {t[1].Fees} netto {t[1].Net}");
        _out.WriteLine("Mit MES-Punktwert 5,00 wären es 22,50 bzw. −15,00 brutto gewesen.");
        t.Should().HaveCount(2);
        (t[0].Side, t[0].EntryPrice, t[0].ExitPrice, t[0].Gross, t[0].Fees, t[0].Net).Should().Be(("Long", 20002.75m, 20007.25m, 9.00m, 0.78m, 8.22m));
        (t[1].Side, t[1].EntryPrice, t[1].ExitPrice, t[1].Gross, t[1].Fees, t[1].Net).Should().Be(("Short", 20009.75m, 20012.75m, -6.00m, 0.78m, -6.78m));

        var row = (await svc.ListAsync()).Single(x => x.RunId == rec.RunId);
        row.Symbol.Should().Be("MNQ"); row.InstrumentNote.Should().BeNull();
    }
}
