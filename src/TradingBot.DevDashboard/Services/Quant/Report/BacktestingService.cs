using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingBot.Quant.Report;

namespace TradingBot.DevDashboard.Services.Quant.Report;

/// <summary>Eingabe des Ein-Knopf-Ablaufs „Strategie testen“. Alles außer Strategie/Instrument/Daten/Zeitraum ist optional
/// (Bereich „Erweitert“) und hat geprüfte Standardwerte.</summary>
public sealed record BacktestingStartRequest
{
    public string Strategy { get; init; } = "movingaverage";
    public Dictionary<string, string> Params { get; init; } = new();
    public string Symbol { get; init; } = "MES";
    public string DataSourceId { get; init; } = "";
    public string? Path { get; init; }
    public int TimeframeMinutes { get; init; } = 5;
    public string? FromUtc { get; init; }
    public string? ToUtc { get; init; }
    public long? MaxRows { get; init; }

    public int Quantity { get; init; } = 1;
    public decimal InitialBalance { get; init; } = 10_000m;
    public int? StopLossTicks { get; init; }
    public int? TakeProfitTicks { get; init; }
    public decimal? SlippageTicks { get; init; }
    public decimal? FeePerSideOverride { get; init; }
    public bool ApplyFees { get; init; } = true;

    // --- Erweitert ---
    public double HoldoutShare { get; init; } = 0.2;
    public int TrainSessions { get; init; } = 20;
    public int TestSessions { get; init; } = 5;
    public int Seed { get; init; } = 20261119;
    public int SimulationIterations { get; init; } = TradeResampling.DefaultIterations;
    public int MinTrades { get; init; } = 30;
    public int MinTrainTrades { get; init; } = 5;
    public string SelectionMetric { get; init; } = "netprofit";
    public string? BenchmarkId { get; init; }
    public bool IsDemo { get; init; }
}

public sealed record PlannedCheck(string Key, string Label, string Status, string? Reason);

public sealed record BacktestingPreview
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string Source { get; init; } = "";
    public string DataKind { get; init; } = "";
    public string Symbol { get; init; } = "";
    public int TimeframeMinutes { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public int Bars { get; init; }
    public int Sessions { get; init; }
    public int DevelopmentSessions { get; init; }
    public int HoldoutSessions { get; init; }
    public DateTimeOffset? HoldoutFrom { get; init; }
    public double HoldoutFraction { get; init; }
    public CostProfileDto? Costs { get; init; }
    public bool FeeIsExample { get; init; }
    public StrategyDef? Strategy { get; init; }
    public int Candidates { get; init; }
    public IReadOnlyList<string> SensitivityParameters { get; init; } = Array.Empty<string>();
    public int EstimatedEngineRuns { get; init; }
    public IReadOnlyList<PlannedCheck> Checks { get; init; } = Array.Empty<PlannedCheck>();
    /// <summary>Datenanforderungen der Strategie und ob der gewählte Datensatz sie erfüllt (vor dem Start sichtbar).</summary>
    public IReadOnlyList<DataRequirementCheck> Requirements { get; init; } = Array.Empty<DataRequirementCheck>();
    /// <summary>False, wenn eine Anforderung fehlt — dann startet der Auftrag nicht.</summary>
    public bool CanStart { get; init; } = true;
    public string Engine { get; init; } = "ohlc";
    public string SignalData { get; init; } = "";
    public string ExecutionData { get; init; } = "";
    public long ElapsedMs { get; init; }
}

public sealed record ReportSummary
{
    public required string RunId { get; init; }
    public string Profile { get; init; } = "";
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public string Status { get; init; } = "";
    public string? StatusReason { get; init; }
    public string Strategy { get; init; } = "";
    public string StrategyName { get; init; } = "";
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
    public string Symbol { get; init; } = "";
    public string? FromUtc { get; init; }
    public string? ToUtc { get; init; }
    /// <summary>Tatsächlich geladener Datenbereich des Laufs (erste/letzte Kerze bzw. Tick; nur lesend aus dem gespeicherten Lauf).</summary>
    public DateTimeOffset? DataFrom { get; init; }
    public DateTimeOffset? DataTo { get; init; }
    public string? DataSha256 { get; init; }
    public int StepsDone { get; init; }
    public int StepsTotal { get; init; }
    public string? JobId { get; init; }
    public IReadOnlyList<OverviewFigure> Figures { get; init; } = Array.Empty<OverviewFigure>();
}

public sealed record CompareRow
{
    public required string RunId { get; init; }
    public string Label { get; init; } = "";
    public string Strategy { get; init; } = "";
    // Rendite (gemeinsame Basis)
    public decimal Net { get; init; }
    public int Trades { get; init; }
    public double? NetPerSession { get; init; }
    public double? ExpectancyPerTrade { get; init; }
    // Risiko (gemeinsame Basis)
    public double MaxDrawdown { get; init; }
    public decimal WorstSession { get; init; }
    public double? SessionStdDev { get; init; }
    public double? NetToDrawdown { get; init; }
    // Stabilität (gemeinsame Basis)
    public double? PositiveSessionShare { get; init; }
    public double? BestSessionShare { get; init; }
    public int Sessions { get; init; }
    public IReadOnlyList<double> Equity { get; init; } = Array.Empty<double>();
    // Kontext aus dem eigenen Bericht (NICHT gemeinsame Basis)
    public decimal? OwnBaseNet { get; init; }
    public double? OwnBaseDrawdown { get; init; }
    public double? OwnSimP95Drawdown { get; init; }
}

public sealed record CompareResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public bool Fair { get; init; }
    public string Basis { get; init; } = "";
    public IReadOnlyList<string> Conditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CommonSessions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<CompareRow> Rows { get; init; } = Array.Empty<CompareRow>();
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
    public long EngineRunsBefore { get; init; }
    public long EngineRunsAfter { get; init; }
}

/// <summary>
/// Backend des Bereichs „Backtesting“: Vorschau (Datenumfang, Kosten, mögliche Prüfungen), Start des Ein-Knopf-Auftrags
/// (baut das vollständige Analyseprofil vorab und friert es ein), Berichtsliste und Vergleich gespeicherter Berichte —
/// der Vergleich liest ausschließlich gespeicherte Ergebnisse und startet KEINE Simulation.
/// </summary>
public sealed class BacktestingService
{
    private readonly QuantApiService _quant;
    private readonly ResearchOrchestrator _orch;

    public BacktestingService(QuantApiService quant, ResearchOrchestrator orch)
    {
        _quant = quant ?? throw new ArgumentNullException(nameof(quant));
        _orch = orch ?? throw new ArgumentNullException(nameof(orch));
    }

    private BacktestRunRequest RunOf(BacktestingStartRequest r) => new()
    {
        DataSourceId = r.DataSourceId, Path = r.Path, Symbol = r.Symbol, TimeframeMinutes = r.TimeframeMinutes,
        FromUtc = r.FromUtc, ToUtc = r.ToUtc, MaxRows = r.MaxRows ?? 100_000_000,
        Strategy = r.Strategy, Params = new Dictionary<string, string>(r.Params ?? new()),
        Quantity = r.Quantity, InitialBalance = r.InitialBalance, StopLossTicks = r.StopLossTicks, TakeProfitTicks = r.TakeProfitTicks,
        SlippageTicks = r.SlippageTicks, FeePerSideOverride = r.FeePerSideOverride, ApplyFees = r.ApplyFees, ExcludePartialEdges = true
    };

    private StrategyDef? Def(string id) => _quant.Backtest.GetStrategies().FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Numerische Strategieparameter (aus der Definition) + Stop/Ziel, wenn die Strategie feste Tick-Stops nutzt.</summary>
    internal static List<string> EffectiveNumericParams(StrategyDef? def, IReadOnlyDictionary<string, string> p)
    {
        var list = new List<string>();
        if (def is null) return list;
        foreach (var d in def.Params)
        {
            string v = p.TryGetValue(d.Key, out var x) ? x : d.Default;
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) list.Add(d.Key);
        }
        if (def.UsesFixedStops) { list.Add("StopLossTicks"); list.Add("TakeProfitTicks"); }
        return list;
    }

    /// <summary>Vorab festgelegtes Walk-forward-Raster: je Strategieparameter Basis ±20 % (3 Werte), kartesisch, max. 27.</summary>
    internal static List<Dictionary<string, string>> Grid(StrategyDef? def, Dictionary<string, string> p)
    {
        var axes = new List<(string Key, List<string> Values)>();
        // Tick-Orderflow (DeltaFlow): vorab festgelegtes Raster NUR über die Delta-Schwelle (Basis ±20 %); die Bar-Größe
        // bleibt fest, weil sie die Bar-Bildung selbst verändert.
        if (def?.Engine == OrderflowTickBacktest.EngineKind)
        {
            int tpb = OrderflowTickBacktest.TicksPerBarOf(p);
            decimal th = OrderflowTickBacktest.ThresholdOf(p);
            return new[] { th * 0.8m, th, th * 1.2m }.Select(x => Math.Round(x, MidpointRounding.AwayFromZero)).Where(x => x > 0).Distinct()
                .Select(x => new Dictionary<string, string>
                {
                    [TradingBot.Application.Strategies.OrderFlow.DeltaFlowStrategy.ThresholdParameterKey] = x.ToString("0.######", CultureInfo.InvariantCulture),
                    [OrderflowTickBacktest.TicksPerBarKey] = tpb.ToString(CultureInfo.InvariantCulture)
                }).ToList();
        }
        if (def is not null)
            foreach (var d in def.Params)
            {
                string v = p.TryGetValue(d.Key, out var x) ? x : d.Default;
                if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv))
                    axes.Add((d.Key, new[] { iv * 0.8, iv, iv * 1.2 }.Select(z => (int)Math.Round(z, MidpointRounding.AwayFromZero))
                        .Where(z => z > 0).Distinct().Select(z => z.ToString(CultureInfo.InvariantCulture)).ToList()));
                else if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv))
                    axes.Add((d.Key, new[] { dv * 0.8, dv, dv * 1.2 }.Select(z => z.ToString("0.######", CultureInfo.InvariantCulture)).Distinct().ToList()));
                else axes.Add((d.Key, new List<string> { v }));
            }
        var grid = new List<Dictionary<string, string>> { new() };
        foreach (var (key, values) in axes)
            grid = grid.SelectMany(g => values.Select(v => new Dictionary<string, string>(g) { [key] = v })).ToList();
        // Fachliche Gültigkeit (z. B. SMA: schnell < langsam) — ungültige Kombinationen gar nicht erst ins Raster.
        grid = grid.Where(g => !(g.TryGetValue("FastPeriod", out var f) && g.TryGetValue("SlowPeriod", out var s)
                                 && int.TryParse(f, out var fi) && int.TryParse(s, out var si) && fi >= si)).Take(27).ToList();
        return grid.Count == 0 ? new List<Dictionary<string, string>> { new(p) } : grid;
    }

    private static int WarmupFor(List<Dictionary<string, string>> grid)
    {
        int max = 0;
        foreach (var g in grid)
            foreach (var kv in g)
                if (int.TryParse(kv.Value, out var v) && kv.Key is not ("StopLossTicks" or "TakeProfitTicks")) max = Math.Max(max, v);
        return max == 0 ? 0 : max + 1;
    }

    /// <summary>Holdout auf Handelstag-Grenze: die letzten round(Anteil·Sessions) Sessions. Rückgabe = SESSION-Fraktion
    /// (h + 0,5)/Sessions, sodass floor(Sessions·Fraktion) = h — genau so zählen Walk-forward-Planer und Holdout-Split im
    /// session-ausgerichteten Modus. (Eine Kerzen-Fraktion würde je nach Sessionlänge eine Session mehr/weniger ergeben.)</summary>
    private static (double Fraction, int HoldoutSessions, DateTimeOffset? From) HoldoutFor(IReadOnlyList<TradingBot.Domain.Models.Candle> candles, double share)
    {
        if (share <= 0 || candles.Count < 2) return (0, 0, null);
        var starts = TradeDateCalendar.SessionStartIndices(candles.Select(c => c.OpenTime).ToList());
        return HoldoutForSessions(starts.Select(i => candles[i].OpenTime).ToList(), share);
    }

    /// <summary>Wie oben, auf Basis der ersten Zeitpunkte je Handelstag (Tick-Pfad: Handelstage der Ticks selbst).</summary>
    private static (double Fraction, int HoldoutSessions, DateTimeOffset? From) HoldoutForSessions(IReadOnlyList<DateTimeOffset> sessionFirstTimes, double share)
    {
        int s = sessionFirstTimes.Count;
        if (share <= 0 || s < 2) return (0, 0, null);
        int h = Math.Clamp((int)Math.Round(s * share, MidpointRounding.AwayFromZero), 1, s - 1);
        return ((h + 0.5) / s, h, sessionFirstTimes[s - h]);
    }

    /// <summary>Handelstage + Holdout: im Tick-Pfad aus den Ticks (Tag-genau), sonst aus den Kerzen.</summary>
    private (int Sessions, double Fraction, int HoldoutSessions, DateTimeOffset? From) SessionPlan(BacktestRunRequest run, bool tick,
        IReadOnlyList<TradingBot.Domain.Models.Candle> candles, double share)
    {
        if (tick)
        {
            var lt = _quant.Backtest.Ticks.Load(run);
            var ranges = OrderflowTickBacktest.SessionRanges(lt.Ticks);
            var (f, h, from) = HoldoutForSessions(ranges.Select(r => lt.Ticks[r.Start].Timestamp).ToList(), share);
            return (ranges.Count, f, h, from);
        }
        var (f2, h2, from2) = HoldoutFor(candles, share);
        return (candles.Select(c => TradeDateCalendar.Key(c.OpenTime)).Distinct().Count(), f2, h2, from2);
    }

    private string StrategyVersion(BacktestRunRequest run, BacktestApiService.RunContext ctx)
    {
        string code = _quant.CodeVersion();
        try
        {
            var type = _quant.Backtest.CreateStrategy(run, ctx.Instrument).GetType();
            var root = RepoLocator.FindRoot(AppContext.BaseDirectory);
            var file = root is null ? null : Directory.EnumerateFiles(System.IO.Path.Combine(root, "src"), type.Name + ".cs", SearchOption.AllDirectories).FirstOrDefault();
            if (file is not null)
            {
                var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))[..12].ToLowerInvariant();
                return $"{type.Name} src:{sha} @ {code}";
            }
            return $"{type.Name} @ {code}";
        }
        catch { return $"{run.Strategy} @ {code}"; }
    }

    /// <summary>Prüft die Datenanforderungen der Strategie gegen den gewählten Datensatz — fail-closed, nichts wird geschätzt.</summary>
    private (IReadOnlyList<DataRequirementCheck> Checks, bool CanStart) Requirements(BacktestRunRequest run, StrategyDef? def)
    {
        if (def is null) return (new[] { new DataRequirementCheck("Bekannte Strategie", false, $"Strategie '{run.Strategy}' ist nicht registriert.") }, false);
        bool tickSource = OrderflowTickBacktest.IsTickSource(run.DataSourceId);
        if (def.Engine == OrderflowTickBacktest.EngineKind)
        {
            int tpb = OrderflowTickBacktest.TicksPerBarOf(run.Params);
            if (!tickSource)
            {
                string label = _quant.Backtest.GetDataSources().FirstOrDefault(d => d.Id == run.DataSourceId)?.Label ?? run.DataSourceId;
                return (OrderflowTickBacktest.Check(null, run.DataSourceId, tpb, label), false);
            }
            var checks = OrderflowTickBacktest.Check(_quant.Backtest.Ticks.Load(run), run.DataSourceId, tpb, null);
            return (checks, checks.All(c => c.Met));
        }
        if (tickSource)
            return (new[] { new DataRequirementCheck("OHLC-Zeitkerzen", false,
                $"{def.Name} rechnet auf Zeitkerzen; der gewählte Tick-Datensatz liefert N-Tick-Bars ohne feste Dauer — für diese Strategie nicht vorgesehen.") }, false);
        return (new[] { new DataRequirementCheck("OHLC-Zeitkerzen", true, "Datensatz liefert Zeitkerzen.") }, true);
    }

    public async Task<BacktestingPreview> PreviewAsync(BacktestingStartRequest r, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var run = RunOf(r);
            var def = Def(r.Strategy);
            var (reqs, canStart) = Requirements(run, def);
            bool tick = def?.Engine == OrderflowTickBacktest.EngineKind;
            if (!canStart)
                return new BacktestingPreview
                {
                    Ok = true, CanStart = false, Requirements = reqs, Strategy = def, Engine = def?.Engine ?? "?",
                    Symbol = r.Symbol, ElapsedMs = sw.ElapsedMilliseconds
                };
            var ctx = await _quant.Backtest.LoadContextAsync(run, ct);
            if (ctx.Candles.Count == 0) return new BacktestingPreview { Ok = false, Error = "Keine gültigen Kerzen im gewählten Zeitraum.", Requirements = reqs, ElapsedMs = sw.ElapsedMilliseconds };
            var (sessions, frac, hs, hFrom) = SessionPlan(run, tick, ctx.Candles, r.HoldoutShare);
            int dev = sessions - hs;
            var sens = EffectiveNumericParams(def, run.Params);
            var grid = Grid(def, run.Params);
            var fee = ctx.Fee;
            decimal feeSide = fee.CommissionPerSide + fee.ExchangeFeePerSide + fee.ClearingFeePerSide + fee.RoutingFeePerSide + fee.NfaFeePerSide + fee.OtherFeePerSide;
            decimal slip = run.SlippageTicks ?? fee.EstimatedSlippageTicks;
            var costs = new CostProfileDto(ctx.Instrument.TickSize, ctx.Instrument.TickValue, ctx.Instrument.PointValue, ctx.Instrument.Currency,
                feeSide, feeSide * 2, slip, slip * ctx.Instrument.TickValue, run.ApplyFees, ctx.InstrumentIsExample, ctx.FeeIsExample);

            int splitNeed = 8, wfNeed = r.TrainSessions + r.TestSessions;
            string Need(int need) => dev < need ? "insufficient" : "possible";
            string? NeedWhy(int need, string? ok) => dev < need ? $"{need} Sessions benötigt, {dev} vorhanden." : ok;
            var checks = new List<PlannedCheck>
            {
                new("data", "01 Datenqualität & zeitliche Korrektheit", "possible", null),
                new("result", "02 Basis-Backtest & Ergebnisrechnung", "possible", null),
                new("equity", "03 Equity, Drawdown & Verlustphasen", "possible", null),
                new("risk", "04 Sharpe/Sortino/Calmar", dev >= 20 ? "possible" : "insufficient",
                    dev >= 20 ? "Tagesrenditen, annualisiert mit 252" : $"Für belastbare Kennzahlen 20 Handelstage benötigt, {dev} vorhanden — werden als „nicht definiert“ ausgewiesen."),
                new("sensitivity", "05 Parametersensitivität", sens.Count > 0 ? "possible" : "notApplicable",
                    sens.Count > 0 ? $"{sens.Count} Parameter: {string.Join(", ", sens)}" : "Keine einstellbaren numerischen Parameter."),
                new("oos-split", "06 Walk-forward-Testdaten: fester Train/Test-Split", Need(splitNeed), NeedWhy(splitNeed, null)),
                new("oos-rolling", "06 Walk-forward-Testdaten: rollierend", Need(wfNeed), NeedWhy(wfNeed, $"{r.TrainSessions} Training / {r.TestSessions} Test")),
                new("oos-anchored", "06 Walk-forward-Testdaten: anwachsend", Need(wfNeed), NeedWhy(wfNeed, null)),
                new("simulation", "07 Monte Carlo & Bootstrap", "possible", $"{r.SimulationIterations} Wiederholungen je Verfahren, Seed {r.Seed}"),
                new("costs", "08 Kosten- & Ausführungsstress", "possible", null),
                new("stability", "09 Stabilität", "possible", null),
                new("concentration", "10 Konzentration & Ausreißer", dev >= 5 ? "possible" : "insufficient", dev >= 5 ? null : $"Weglassen je Handelstag: 5 Sessions benötigt, {dev} vorhanden."),
                new("controls", "11 Benchmarks, Kontrollläufe & Overfitting", "possible", null),
                new("trades", "12 Einzeltrade-Prüfung", "possible", tick ? "MAE/MFE gemessen aus den Trade-Ticks" : "MAE/MFE als Schätzung aus Kerzen (nicht tickgenau)"),
                new("holdout", "Finaler Holdout – unbenutzt", hs > 0 ? "reserved" : "notApplicable", hs > 0 ? $"{hs} Session(s) ab {hFrom:yyyy-MM-dd} reserviert — im normalen Lauf nicht ausgewertet." : r.HoldoutShare > 0 ? $"Kein Holdout reserviert: {sessions} Session(s) vorhanden, mindestens 2 nötig." : "Holdout-Anteil 0.")
            };
            int pairRuns = (def?.Params.Count >= 2 ? 25 : 0) + (def?.UsesFixedStops == true ? 25 : 0);
            return new BacktestingPreview
            {
                Ok = true, Source = ctx.Source,
                DataKind = tick ? "Trade-Ticks mit gemessenem Aggressor, keine Quotes" : ctx.Source.StartsWith("Sierra", StringComparison.OrdinalIgnoreCase) ? "OHLC aus Trade-Ticks (Last), keine Quotes" : "OHLC-CSV",
                Requirements = reqs, CanStart = true, Engine = def?.Engine ?? "ohlc",
                SignalData = tick ? $"{OrderflowTickBacktest.TicksPerBarOf(run.Params)}-Tick-Orderflow-Bars, Delta gemessen aus Aggressor je Trade" : $"OHLC-Zeitkerzen ({ctx.TimeframeMinutes} Min)",
                ExecutionData = tick ? "Bestehende tick-getriebene BacktestEngine: Fill am nächsten Trade-Tick ± Slippage" : "Bar-basierte OHLC-Engine: Fill am Open der Folgekerze ± Slippage",
                Symbol = ctx.Instrument.Symbol, TimeframeMinutes = ctx.TimeframeMinutes,
                From = ctx.Candles[0].OpenTime, To = ctx.Candles[^1].CloseTime, Bars = ctx.Candles.Count,
                Sessions = sessions, DevelopmentSessions = dev, HoldoutSessions = hs, HoldoutFrom = hFrom, HoldoutFraction = frac,
                Costs = costs, FeeIsExample = ctx.FeeIsExample, Strategy = def, Candidates = grid.Count, SensitivityParameters = sens,
                EstimatedEngineRuns = 2 + sens.Count * 5 + pairRuns + grid.Count * 2 * 3 * Math.Max(1, dev / Math.Max(1, r.TestSessions)) + 8,
                Checks = checks, ElapsedMs = sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new BacktestingPreview { Ok = false, Error = ex.Message, ElapsedMs = sw.ElapsedMilliseconds };
        }
    }

    /// <summary>Baut die eingefrorene Konfiguration des Ein-Knopf-Auftrags.</summary>
    public async Task<ResearchStartRequest> BuildStartRequestAsync(BacktestingStartRequest r, CancellationToken ct = default)
    {
        var run = RunOf(r);
        var def = Def(r.Strategy);
        var (reqs, canStart) = Requirements(run, def);
        if (!canStart)
            throw new InvalidOperationException("Datenanforderungen nicht erfüllt: " +
                string.Join(" · ", reqs.Where(x => !x.Met).Select(x => $"{x.Requirement}: {x.Detail}")));
        var ctx = await _quant.Backtest.LoadContextAsync(run, ct);
        bool tickRun = def?.Engine == OrderflowTickBacktest.EngineKind;
        var (_, frac, _, _) = SessionPlan(run, tickRun, ctx.Candles, r.HoldoutShare);
        var grid = Grid(def, run.Params);
        var sens = EffectiveNumericParams(def, run.Params);
        var pairs = new List<string[]>();
        var stratParams = sens.Where(s => s is not ("StopLossTicks" or "TakeProfitTicks")).ToList();
        if (stratParams.Count >= 2) pairs.Add(new[] { stratParams[0], stratParams[1] });
        if (def?.UsesFixedStops == true) pairs.Add(new[] { "StopLossTicks", "TakeProfitTicks" });
        string metric = string.IsNullOrWhiteSpace(r.SelectionMetric) ? "netprofit" : r.SelectionMetric.Trim().ToLowerInvariant();

        // Deterministische Kampagnen-Id aus der vollständigen Konfiguration: ein Doppelklick/Reload mit derselben
        // Konfiguration findet den laufenden Auftrag statt einen zweiten zu starten.
        string cfgHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(r))))[..12].ToLowerInvariant();
        var opts = new FullReportOptions
        {
            MinTrades = r.MinTrades, MinTrainTrades = r.MinTrainTrades, SimulationIterations = Math.Clamp(r.SimulationIterations, 100, 50_000),
            StrategyVersion = StrategyVersion(run, ctx), SensitivityParameters = sens, ParameterPairs = pairs
        };
        return new ResearchStartRequest
        {
            Run = run,
            Options = new QuantEvaluationOptions { Frequency = "Daily", AnnualizationBasis = "Fixed", FixedPeriodsPerYear = 252, MinimumPeriods = 20 },
            Campaign = new CampaignInput
            {
                Id = "bt-" + r.Strategy + "-" + cfgHash, Name = $"Backtest {def?.Name ?? r.Strategy}",
                Hypothesis = "Ein-Knopf-Bericht: vollständige Prüfung der Strategie mit vorab festgelegtem Profil.",
                SearchSpace = string.Join(" | ", grid.Select(g => string.Join(",", g.Select(kv => kv.Key + "=" + kv.Value)))),
                SelectionMetric = metric, TrialBudget = grid.Count, HoldoutFraction = frac
            },
            Candidates = grid,
            SelectionMetric = metric,
            Mode = "Rolling",
            SessionAligned = true, SessionCalendar = "tradedate", TrainSessions = r.TrainSessions, TestSessions = r.TestSessions,
            TrainBars = 1, TestBars = 1, WarmupBars = tickRun ? 0 : WarmupFor(grid), LabelSpanBars = 0, EmbargoBars = 0,
            HoldoutFraction = frac,
            MonteCarloIterations = opts.SimulationIterations, Seed = r.Seed,
            RobustnessMetric = "netprofit", OverfittingBlocks = 16, EstimateEffectiveTrials = true,
            BenchmarkId = r.BenchmarkId, IsDemo = r.IsDemo,
            Profile = "full", Report = opts
        };
    }

    public async Task<ResearchRunResponse> StartAsync(BacktestingStartRequest r, CancellationToken ct = default)
    {
        try { return await _orch.StartAsync(await BuildStartRequestAsync(r, ct), ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new ResearchRunResponse { Ok = false, Error = ex.Message }; }
    }

    public async Task<IReadOnlyList<ReportSummary>> ListAsync(CancellationToken ct = default)
    {
        var runs = await _orch.ListAsync(null, ct);
        var defs = _quant.Backtest.GetStrategies();
        return runs.OrderByDescending(x => x.CreatedUtc).Select(x => new ReportSummary
        {
            RunId = x.RunId, Profile = x.Config.Profile, CreatedUtc = x.CreatedUtc, CompletedUtc = x.CompletedUtc,
            Status = x.Status.ToString(), StatusReason = x.StatusReason, Strategy = x.Config.Run.Strategy,
            StrategyName = defs.FirstOrDefault(d => d.Id == x.Config.Run.Strategy)?.Name ?? x.Config.Run.Strategy,
            Parameters = x.Config.Run.Params, Symbol = x.Config.Run.Symbol, FromUtc = x.Config.Run.FromUtc, ToUtc = x.Config.Run.ToUtc,
            DataFrom = x.DataFrom, DataTo = x.DataTo,
            DataSha256 = x.Report?.Provenance.DataSha256, JobId = x.JobId,
            StepsDone = x.Steps.Count(s => s.Status is not (ResearchStepStatus.Pending or ResearchStepStatus.Running)),
            StepsTotal = x.Steps.Count, Figures = x.Report?.Overview.Figures ?? Array.Empty<OverviewFigure>()
        }).ToList();
    }

    // =============================================================================================
    // Vergleich gespeicherter Berichte — liest NUR gespeicherte Ergebnisse, keine Engine-Läufe.
    // =============================================================================================

    public async Task<CompareResult> CompareAsync(IReadOnlyList<string> runIds, CancellationToken ct = default)
    {
        long before = _quant.Backtest.EngineRuns;
        if (runIds.Count < 2) return new CompareResult { Ok = false, Error = "Mindestens zwei Berichte auswählen." };
        var records = new List<ResearchRunRecord>();
        var all = await _orch.ListAsync(null, ct);
        foreach (var id in runIds.Distinct())
        {
            var rec = all.FirstOrDefault(x => x.RunId == id);
            if (rec is null) return new CompareResult { Ok = false, Error = $"Bericht '{id}' nicht gefunden." };
            if (rec.Report is null) return new CompareResult { Ok = false, Error = $"Lauf '{id}' ist kein vollständiger Backtest-Bericht." };
            records.Add(rec);
        }
        var result = BuildComparison(records);
        return result with { EngineRunsBefore = before, EngineRunsAfter = _quant.Backtest.EngineRuns };
    }

    public static CompareResult BuildComparison(IReadOnlyList<ResearchRunRecord> records)
    {
        var problems = new List<string>();
        var conditions = new List<string>();
        var reps = records.Select(r => (Rec: r, Rep: r.Report!)).ToList();

        void Same<T>(string what, Func<(ResearchRunRecord Rec, BacktestReport Rep), T> f)
        {
            var vals = reps.Select(f).Select(v => v?.ToString() ?? "–").Distinct().ToList();
            if (vals.Count == 1) conditions.Add($"{what}: {vals[0]} (gleich)");
            else problems.Add($"{what} unterscheidet sich: {string.Join(" vs. ", reps.Select(x => ShortId(x.Rec.RunId) + "=" + (f(x)?.ToString() ?? "–")))}.");
        }
        Same("Instrument", x => x.Rep.Provenance.Symbol);
        Same("Kontraktzahl", x => x.Rep.Provenance.Quantity);
        Same("Gebühr je Seite", x => x.Rep.Provenance.CostModel?.FeePerSide.ToString("0.00", CultureInfo.InvariantCulture));
        Same("Slippage (Ticks)", x => x.Rep.Provenance.CostModel?.SlippageTicks.ToString("0.##", CultureInfo.InvariantCulture));
        Same("Gebühren aktiv", x => x.Rep.Provenance.CostModel?.ApplyFees);
        Same("Zeitrahmen (Min.)", x => x.Rep.Provenance.TimeframeMinutes);
        // Ausführungsmodell/Signaldaten ausdrücklich prüfen: ein Tick-Orderflow-Lauf ist NICHT gleichwertig mit einem Kerzenlauf.
        Same("Ausführungsmodell", x => string.IsNullOrEmpty(x.Rep.Provenance.EngineKind) || x.Rep.Provenance.EngineKind == "ohlc-bars"
            ? "OHLC-Bar-Engine (Fill am Open der Folgekerze)" : "Tick-Engine (Fill am nächsten Trade-Tick)");
        Same("Signaldaten", x => x.Rep.Provenance.EngineKind == OrderflowTickBacktest.EngineKind
            ? "N-Tick-Orderflow-Bars mit gemessenem Delta" : $"OHLC-Zeitkerzen ({x.Rep.Provenance.TimeframeMinutes} Min)");
        Same("Datenart", x => x.Rep.Provenance.DataKind);

        // Gemeinsame OOS-Sessions der Primärvariante.
        List<SessionResult> OosOf(BacktestReport rep) =>
            rep.Oos?.Variants.FirstOrDefault(v => v.Key == rep.Oos.PrimaryVariant)?.OosSessionResults.ToList() ?? new List<SessionResult>();
        var oosSets = reps.Select(x => OosOf(x.Rep)).ToList();
        string basis;
        List<string> common;
        Func<int, List<SessionResult>> perRun;
        if (oosSets.All(s => s.Count > 0))
        {
            common = oosSets.Select(s => s.Select(r => r.Key).ToHashSet()).Aggregate((a, b) => { a.IntersectWith(b); return a; })
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            basis = "Gemeinsame Walk-forward-Testsessions (OOS)";
            perRun = i => oosSets[i];
            if (common.Count == 0)
                problems.Add("Keine gemeinsamen Walk-forward-Testsessions: " + string.Join("; ", reps.Select((x, i) =>
                    $"{ShortId(x.Rec.RunId)} OOS {oosSets[i].Min(s => s.Key)} – {oosSets[i].Max(s => s.Key)}")) + ".");
        }
        else
        {
            common = new List<string>();
            basis = "Gemeinsame Walk-forward-Testsessions (OOS)";
            perRun = i => oosSets[i];
            problems.Add("Mindestens ein Bericht hat kein OOS-Ergebnis (" + string.Join(", ", reps.Where((x, i) => oosSets[i].Count == 0).Select(x => ShortId(x.Rec.RunId))) +
                         ") — ein fairer Vergleich auf ungesehenen Daten ist nicht möglich.");
        }

        var rows = new List<CompareRow>();
        for (int i = 0; i < reps.Count; i++)
        {
            var rep = reps[i].Rep;
            var bySession = perRun(i).GroupBy(s => s.Key).ToDictionary(g => g.Key, g => (Net: g.Sum(s => s.Net), Trades: g.Sum(s => s.Trades)));
            var nets = common.Select(k => bySession.TryGetValue(k, out var v) ? v.Net : 0m).ToList();
            int trades = common.Sum(k => bySession.TryGetValue(k, out var v) ? v.Trades : 0);
            double eq = 0, peak = 0, dd = 0;
            var curve = new List<double>();
            foreach (var n in nets) { eq += (double)n; peak = Math.Max(peak, eq); dd = Math.Max(dd, peak - eq); curve.Add(eq); }
            decimal total = nets.Sum();
            double? std = nets.Count >= 2 ? TradingBot.Quant.Statistics.Stats.StdDevSample(nets.Select(n => (double)n).ToList()) : null;
            var simDd = rep.Simulation?.Methods.FirstOrDefault(m => m.Key == "bootstrap")?.MaxDrawdown?.P95;
            rows.Add(new CompareRow
            {
                RunId = reps[i].Rec.RunId,
                Label = $"{rep.Provenance.StrategyName} ({string.Join(", ", rep.Provenance.Parameters.Select(kv => kv.Key + "=" + kv.Value))})",
                Strategy = rep.Provenance.StrategyId,
                Net = total, Trades = trades,
                NetPerSession = nets.Count > 0 ? (double)total / nets.Count : null,
                ExpectancyPerTrade = trades > 0 ? (double)total / trades : null,
                MaxDrawdown = dd,
                WorstSession = nets.Count > 0 ? nets.Min() : 0,
                SessionStdDev = std,
                NetToDrawdown = dd > 0 ? (double)total / dd : null,
                PositiveSessionShare = nets.Count > 0 ? nets.Count(n => n > 0) / (double)nets.Count : null,
                BestSessionShare = total > 0 && nets.Count > 0 ? (double)(nets.Max() / total) : null,
                Sessions = nets.Count,
                Equity = curve,
                OwnBaseNet = rep.Result?.Stats.NetPnL,
                OwnBaseDrawdown = rep.Equity?.ClosedTrades.MaxDrawdown,
                OwnSimP95Drawdown = simDd
            });
        }

        // Klare Gegenüberstellung: mehr Ertrag vs. mehr Risiko.
        var findings = new List<string>();
        if (common.Count > 0 && rows.Count >= 2)
        {
            var best = rows.OrderByDescending(r => r.Net).First();
            foreach (var other in rows.Where(r => r.RunId != best.RunId))
            {
                string ddRatio = other.MaxDrawdown > 0 ? $"{best.MaxDrawdown / other.MaxDrawdown:0.0}×" : best.MaxDrawdown > 0 ? "mehr als" : "gleich";
                string verb = best.Net > 0 ? "verdient" : "verliert";
                string diff = best.Net > 0 ? $"{best.Net - other.Net:0.00} mehr" : $"{best.Net - other.Net:0.00} weniger";
                findings.Add($"{best.Label} {verb} auf den {common.Count} gemeinsamen Sessions {diff} als {other.Label} " +
                             $"(Netto {best.Net:0.00} vs. {other.Net:0.00}), bei {ddRatio} Drawdown ({best.MaxDrawdown:0.00} vs. {other.MaxDrawdown:0.00}).");
            }
            if (common.Count < 20) findings.Add($"Nur {common.Count} gemeinsame Sessions — Unterschiede sind statistisch wenig belastbar.");
        }
        bool fair = problems.Count == 0 && common.Count > 0;
        return new CompareResult
        {
            Ok = true, Fair = fair, Basis = basis, Conditions = conditions, Problems = problems, CommonSessions = common,
            Rows = rows, Findings = findings
        };
    }

    private static string ShortId(string id) => id.Length > 14 ? id[^8..] : id;
}
