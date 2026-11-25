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
using TradingBot.Research.Orderflow;
using Xunit;
using Xunit.Abstractions;

namespace TradingBot.Tests.DevDashboard;

/// <summary>
/// Technische Abnahme „MNQ-Orderflow über einen Knopf“: DIESELBE kleine synthetische MNQ-Tick-Sequenz läuft einmal direkt
/// durch den bestehenden Orderflow-Pfad (wie die Orderflow-Konsole: AtasTickCsvImporter + config/-Profile +
/// gegateter DeltaFlow-Kandidat + OrderflowMultiStrategyRunner) und einmal durch den vollständigen Ein-Knopf-Bericht.
/// Signale, Fills, Gebühren und Netto müssen übereinstimmen; die erste Abweichung wird konkret benannt.
/// </summary>
[Collection("ResearchSerial")]
public class OrderflowReportTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<string> _dirs = new();
    public OrderflowReportTests(ITestOutputHelper output) => _out = output;
    public void Dispose()
    {
        foreach (var d in _dirs) try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradingBot.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo-Wurzel nicht gefunden.");
    }
    /// <summary>Synthetische MNQ-Ein-Tages-Sequenz (KEINE Marktdaten), im Test erzeugt: 6 Bars à 500 Ticks im Muster
    /// buy/sell/buy/neutral/sell/buy, Preisschritt 0,25, Aggressor je Trade — ATAS-Tickformat.</summary>
    private static readonly Lazy<string> FixturePath = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "mnq-deltaflow-synthetic-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, SyntheticTickFixtures.OneDayCsv());
        return path;
    });
    private static string Fixture => FixturePath.Value;

    private (BacktestingService svc, ResearchOrchestrator orch, QuantApiService quant) New()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "of-report-tests-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(tempRoot);
        var quant = new QuantApiService(new BacktestApiService(RepoRoot()), tempRoot);
        var orch = new ResearchOrchestrator(quant, new QuantJobManager(), new ResearchRunStore(quant.RegistryDir));
        return (new BacktestingService(quant, orch), orch, quant);
    }

    private static BacktestingStartRequest Req(string dataSourceId = "ticks-csv:synthetic", string strategy = "deltaflow") => new()
    {
        Path = dataSourceId == "ticks-csv:synthetic" ? Fixture : null,
        Strategy = strategy,
        Params = strategy == "deltaflow" ? new() { ["DeltaThreshold"] = "30", ["TicksPerBar"] = "500" } : new() { ["FastPeriod"] = "9", ["SlowPeriod"] = "21" },
        Symbol = "MNQ", DataSourceId = dataSourceId, TimeframeMinutes = 5, Quantity = 1, InitialBalance = 10_000m, ApplyFees = true,
        HoldoutShare = 0.2, TrainSessions = 20, TestSessions = 5, Seed = 7, SimulationIterations = 300, MinTrades = 30, MinTrainTrades = 3
    };

    private static async Task<ResearchRunRecord> Wait(ResearchOrchestrator orch, string runId)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMinutes(2))
        {
            var r = (await orch.GetAsync(runId)).Run!;
            if (r.Status is ResearchStepStatus.Completed or ResearchStepStatus.Failed or ResearchStepStatus.Cancelled) return r;
            await Task.Delay(50);
        }
        throw new TimeoutException();
    }

    /// <summary>Der bestehende Orderflow-Pfad, aufgebaut wie TradingBot.OrderflowRun (Konsole), nur DeltaFlow.</summary>
    private static async Task<OrderflowRunResult> DirectAsync()
    {
        var root = RepoRoot();
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
        T Load<T>(string rel) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(root, "config", rel)), json)!;
        var instrument = Load<InstrumentProfile>(@"instruments\mnq.example.json");
        var fee = Load<FeeProfile>(@"fees\amp-rithmic-mnq.example.json");
        var broker = Load<BrokerProfile>(@"brokers\amp-rithmic.example.json");
        var risk = Load<RiskConfig>(@"risk\research-sim.example.json");
        var set = new AtasTickCsvImporter(Load<CsvImportProfile>(@"import-profiles\atas-tick.example.json")).ImportFile(Fixture);
        set.Capabilities.SupportsDeltaCvd.Should().BeTrue();
        var deltaFlow = OrderflowProofCandidates.BuildGated(set.Symbol, DeltaFlowStrategy.DefaultDeltaThreshold).Single(c => c.Name == "DeltaFlow(OF)");
        return await new OrderflowMultiStrategyRunner().RunAsync(new OrderflowRunRequest
        {
            Ticks = set.Ticks, Symbol = set.Symbol, Instrument = instrument, Fee = fee, Broker = broker, Risk = risk,
            Account = new TradingAccount { AccountId = "research-sim", BrokerName = broker.BrokerName, AccountType = broker.AccountType, Currency = instrument.Currency, StartingBalance = 10_000m },
            TicksPerBar = 500, SlippageTicksOverride = null, Candidates = new[] { deltaFlow }, DataSource = "direkt", DataSha256 = "x",
            CapabilitiesSufficient = true, DeltaThreshold = DeltaFlowStrategy.DefaultDeltaThreshold
        });
    }

    [Fact]
    public async Task Same_MNQ_tick_sequence_direct_orderflow_path_and_full_report_agree_on_signals_fills_fees_and_net()
    {
        var direct = await DirectAsync();
        var dTrades = direct.Workspace.Strategies.Single().Trades;
        var dDiag = direct.RunResults.Single().Diagnostics!;
        dTrades.Should().NotBeEmpty("die Testsequenz muss DeltaFlow-Trades erzeugen");

        var (svc, orch, _) = New();
        var preview = await svc.PreviewAsync(Req());
        preview.Ok.Should().BeTrue(preview.Error);
        preview.CanStart.Should().BeTrue(string.Join(" | ", preview.Requirements.Select(r => r.Requirement + ": " + r.Detail)));
        preview.Engine.Should().Be(OrderflowTickBacktest.EngineKind);

        var start = await svc.StartAsync(Req());
        start.Ok.Should().BeTrue(start.Error);
        var run = await Wait(orch, start.Run!.RunId);
        run.Status.Should().Be(ResearchStepStatus.Completed, run.StatusReason + " | " + string.Join(" | ", run.Steps.Select(s => $"{s.Key}:{s.Status}:{s.Reason}")));
        var rep = run.Report!;
        var rTrades = rep.Trades!.Trades;

        // Vergleich Trade für Trade — erste Abweichung konkret benennen.
        string? firstDiff = null;
        int n = Math.Max(dTrades.Count, rTrades.Count);
        for (int i = 0; i < n && firstDiff is null; i++)
        {
            if (i >= dTrades.Count || i >= rTrades.Count) { firstDiff = $"Trade #{i + 1}: nur in {(i >= dTrades.Count ? "Bericht" : "direktem Pfad")} vorhanden"; break; }
            var d = dTrades[i]; var r = rTrades[i];
            (string Feld, object? Direkt, object? Bericht)[] fields =
            {
                ("Einstiegssignal (UTC)", d.EntrySignal?.TimeUtc, r.EntrySignalUtc), ("Einstiegssignal Richtung", d.EntrySignal?.Direction, r.EntrySignalDirection),
                ("Ausstiegssignal (UTC)", d.ExitSignal?.TimeUtc, r.ExitSignalUtc), ("Ausstiegssignal Richtung", d.ExitSignal?.Direction, r.ExitSignalDirection),
                ("Entry-Fill (UTC)", d.EntryTimeUtc, r.EntryTimeUtc), ("Exit-Fill (UTC)", d.ExitTimeUtc, r.ExitTimeUtc),
                ("Seite", d.Side, r.Side), ("Entry-Fillpreis", d.EntryPrice, r.EntryPrice), ("Exit-Fillpreis", d.ExitPrice, r.ExitPrice),
                ("Gebühren", d.Fees, r.Fees), ("Brutto", d.GrossPnL, r.Gross), ("Netto", d.NetPnL, r.Net)
            };
            foreach (var f in fields)
                if (!Equals(f.Direkt, f.Bericht)) { firstDiff = $"Trade #{i + 1}, {f.Feld}: direkt={f.Direkt} vs. Bericht={f.Bericht}"; break; }
        }
        _out.WriteLine($"Direkt: {dTrades.Count} Trades, Signale {dDiag.SignalsGenerated}, Netto {dTrades.Sum(t => t.NetPnL)}, Gebühren {dTrades.Sum(t => t.Fees ?? 0)}");
        _out.WriteLine($"Bericht: {rTrades.Count} Trades, Signale {rep.Result!.SignalsGenerated}, Netto {rep.Result.Stats.NetPnL}, Gebühren {rep.Result.Stats.Fees}");
        _out.WriteLine("Erste Abweichung: " + (firstDiff ?? "keine"));
        firstDiff.Should().BeNull();
        rep.Result!.SignalsGenerated.Should().Be(dDiag.SignalsGenerated);
        rep.Result.OrdersSubmitted.Should().Be(dDiag.OrdersSubmitted);
        rep.Result.Stats.NetPnL.Should().Be(dTrades.Sum(t => t.NetPnL));
        rep.Result.Stats.Fees.Should().Be(dTrades.Sum(t => t.Fees ?? 0m));

        // Herkunft: Signal- und Ausführungsdaten benannt; MAE/MFE gemessen.
        rep.Provenance.EngineKind.Should().Be(OrderflowTickBacktest.EngineKind);
        rep.Provenance.SignalData.Should().Contain("gemessen");
        rep.Provenance.ExecutionData.Should().Contain("nächsten Trade-Tick");
        rTrades.Should().OnlyContain(t => t.ExcursionMeasured && t.Mae != null);

        // Zu wenig Historie: nachvollziehbar „nicht auswertbar“ statt Zahlen.
        var oos = run.Steps.Single(s => s.Key == "oos");
        oos.Status.Should().Be(ResearchStepStatus.InsufficientData);
        rep.Oos!.Variants.Single(v => v.Key == "rolling").Reason.Should().Be("25 Sessions benötigt, 1 vorhanden.");
        foreach (var m in rep.Risk!.Metrics) _out.WriteLine($"Risk {m.Key}: {m.Value} n={m.SampleSize} reason={m.UnavailableReason} limitation={m.Limitation}");
        _out.WriteLine("Risk freq " + rep.Risk.Frequency + " ppy " + rep.Risk.PeriodsPerYear + " notes " + string.Join(" | ", rep.Risk.Notes));
        rep.Risk!.Metrics.Where(m => m.Key is "sharpe" or "sortino" or "calmar").Should().OnlyContain(m => m.Value == null && m.UnavailableReason != null);
        rep.Concentration!.Result.LeaveOneOutReason.Should().Be("5 Handelstage benötigt, 1 vorhanden.");
        rep.Data!.TemporalChecks.Where(c => c.Passed != null).Should().OnlyContain(c => c.Passed == true,
            string.Join(" | ", rep.Data.TemporalChecks.Select(c => c.Name + ": " + c.Detail)));
    }

    [Fact]
    public async Task DeltaFlow_on_an_ohlc_dataset_is_refused_before_start_and_never_runs_as_SMA()
    {
        var (svc, _, quant) = New();
        var csv = Path.Combine(Path.GetTempPath(), "ohlc-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(csv, "timestamp,open,high,low,close,volume\n2026-06-23T13:30:00Z,1,2,0.5,1.5,10\n2026-06-23T13:35:00Z,1.5,2,1,1.2,10\n");
        try
        {
            var r = Req("csv", "deltaflow") with { Path = csv };
            var p = await svc.PreviewAsync(r);
            p.CanStart.Should().BeFalse();
            p.Requirements.Should().Contain(x => !x.Met && x.Detail.Contains("KEIN Delta"));
            var s = await svc.StartAsync(r);
            s.Ok.Should().BeFalse();
            s.Error.Should().Contain("Datenanforderungen nicht erfüllt");
            // Früher lief eine unbekannte/Orderflow-Strategie in der OHLC-Engine stillschweigend als SMA.
            var single = await quant.Backtest.RunAsync(new BacktestRunRequest { DataSourceId = "csv", Path = csv, Symbol = "MNQ", Strategy = "deltaflow" });
            single.Ok.Should().BeFalse();
            single.Error.Should().Contain("Tick-Orderflow-Pfad");
        }
        finally { File.Delete(csv); }
    }

    [Fact]
    public async Task Comparing_a_tick_orderflow_report_with_a_candle_report_is_not_shown_as_fair()
    {
        var (svc, orch, _) = New();
        var tick = await Wait(orch, (await svc.StartAsync(Req())).Run!.RunId);
        var ohlcCsv = Path.Combine(Path.GetTempPath(), "ohlc-" + Guid.NewGuid().ToString("N") + ".csv");
        var sb = new System.Text.StringBuilder("timestamp,open,high,low,close,volume\n");
        for (int i = 0; i < 300; i++)
            sb.Append($"{new DateTime(2026, 6, 23, 13, 30, 0, DateTimeKind.Utc).AddMinutes(5 * i):yyyy-MM-ddTHH:mm:ss}Z,{20000 + i % 7},{20003 + i % 7},{19998 + i % 7},{20001 + i % 5},10\n");
        File.WriteAllText(ohlcCsv, sb.ToString());
        try
        {
            var ohlc = await Wait(orch, (await svc.StartAsync(Req("csv", "movingaverage") with { Path = ohlcCsv })).Run!.RunId);
            var cmp = BacktestingService.BuildComparison(new[] { tick, ohlc });
            cmp.Fair.Should().BeFalse();
            cmp.Problems.Should().Contain(p => p.StartsWith("Ausführungsmodell unterscheidet sich"));
            cmp.Problems.Should().Contain(p => p.StartsWith("Signaldaten unterscheidet sich"));
        }
        finally { File.Delete(ohlcCsv); }
    }
}
