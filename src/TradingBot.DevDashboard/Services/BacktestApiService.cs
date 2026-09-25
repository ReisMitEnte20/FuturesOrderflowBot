using TradingBot.Application.Strategies;
using TradingBot.Backtesting;
using TradingBot.Backtesting.Ohlc;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Config;
using TradingBot.Infrastructure.MarketData.Import;

namespace TradingBot.DevDashboard.Services;

// ---- DTOs (JSON für das React-Dashboard) ----
public sealed record StrategyParamDef(string Key, string Label, string Type, string Default);
public sealed record StrategyDef(string Id, string Name, string Description, bool IsReference, IReadOnlyList<StrategyParamDef> Params);
public sealed record InstrumentDef(string Symbol, decimal TickSize, decimal TickValue, decimal PointValue, int MaxContracts, int DefaultStopLossTicks, int DefaultTakeProfitTicks);
public sealed record DataSourceDef(string Id, string Kind, string Label, bool Available, string? Note);
public sealed record CandleDto(long T, decimal O, decimal H, decimal L, decimal C, decimal V);

/// <summary>
/// Tatsächlich verwendete Instrument- und Kostenwerte eines Laufs (Anzeige im Dashboard, mit Einheiten).
/// <see cref="SlippagePerSideDollars"/> ist der Geldwert der in den Fill-Preisen enthaltenen Slippage je
/// Market-Seite (informativ, KEIN zusätzlicher Abzug). Die Example-Flags kennzeichnen Beispielprofile
/// (nur *.example.json vorhanden) – keine echten Broker-Kosten.
/// </summary>
public sealed record CostProfileDto(
    decimal TickSize, decimal TickValue, decimal PointValue, string Currency,
    decimal FeePerSide, decimal FeeRoundTrip, decimal SlippageTicks, decimal SlippagePerSideDollars,
    bool ApplyFees, bool InstrumentIsExample, bool FeeIsExample);

public sealed record BacktestRunRequest
{
    public string DataSourceId { get; init; } = "";
    public string? Path { get; init; }
    public string Symbol { get; init; } = "MES";
    public int TimeframeMinutes { get; init; } = 5;
    public string? FromUtc { get; init; }
    public long MaxRows { get; init; } = 100_000;

    public string Strategy { get; init; } = "movingaverage";
    public Dictionary<string, string> Params { get; init; } = new();

    public int Quantity { get; init; } = 1;
    public decimal InitialBalance { get; init; } = 10_000m;
    public int? StopLossTicks { get; init; }
    public int? TakeProfitTicks { get; init; }
    public decimal? SlippageTicks { get; init; }
    public decimal? FeePerSideOverride { get; init; }
    public bool ApplyFees { get; init; } = true;
    public bool ExcludePartialEdges { get; init; } = true;
}

public sealed record BacktestRunResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public OhlcBacktestResult? Result { get; init; }
    public CostProfileDto? CostProfile { get; init; }
    public IReadOnlyList<CandleDto> Candles { get; init; } = Array.Empty<CandleDto>();
    public IReadOnlyList<OhlcImportIssue> DataIssues { get; init; } = Array.Empty<OhlcImportIssue>();
}

/// <summary>
/// Backend-Brücke für den OHLC-Backtest im React-Dashboard (Simulation-only, read-only Daten).
/// Lädt OHLC-Bars entweder aus einer OHLC-CSV oder — wenn nur die lokale Sierra-TICK-Datei vorliegt —
/// durch EINMALIGE Aggregation echter Zeitkerzen aus Last-Handelspreisen (High/Low = gehandelte
/// Extrema der Last-Preise, NICHT Ask/Bid). Der Backtest rechnet ausschließlich auf diesen Bars.
/// Keine Broker-/Execution-Anbindung, keine externen Calls, keine Orders.
/// </summary>
public sealed class BacktestApiService
{
    private readonly string _repoRoot;
    private readonly string _instrumentsDir;
    private readonly string _feesDir;
    private readonly InstrumentProfileProvider _instruments;
    private readonly FeeProfileProvider _fees;
    private readonly OhlcBacktestEngine _engine = new();

    /// <summary>Standardpfad der großen lokalen Sierra-Tickdatei (außerhalb des Repos).</summary>
    public const string SierraLocalPath = @"A:\Projects\MARKET DATA\MESM26-CME.txt";

    public BacktestApiService(string repoRoot)
    {
        _repoRoot = repoRoot;
        var cfg = new JsonConfigService();
        _instrumentsDir = Path.Combine(repoRoot, "config", "instruments");
        _feesDir = Path.Combine(repoRoot, "config", "fees");
        _instruments = new InstrumentProfileProvider(cfg, _instrumentsDir);
        _fees = new FeeProfileProvider(cfg, _feesDir);
    }

    /// <summary>Nur Beispielprofile im Verzeichnis (ausschließlich *.example.json, keine echten Broker-Werte)?</summary>
    private static bool OnlyExampleProfiles(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        var files = Directory.EnumerateFiles(dir, "*.json").ToList();
        return files.Count > 0 && files.All(f => f.EndsWith(".example.json", StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<StrategyDef> GetStrategies() => new[]
    {
        new StrategyDef("movingaverage", "SMA-Crossover (Referenz/Test)",
            "Einfacher SMA-Crossover — ausdrücklich als TESTSTRATEGIE, keine Edge-Behauptung.", true, new[]
            {
                new StrategyParamDef("FastPeriod", "Schneller SMA (Bars)", "int", "9"),
                new StrategyParamDef("SlowPeriod", "Langsamer SMA (Bars)", "int", "21"),
            })
    };

    public async Task<IReadOnlyList<InstrumentDef>> GetInstrumentsAsync(CancellationToken ct = default)
    {
        var all = await _instruments.GetAllAsync(ct);
        return all.Select(i => new InstrumentDef(i.Symbol, i.TickSize, i.TickValue, i.PointValue,
                i.MaxContracts, i.DefaultStopLossTicks, i.DefaultTakeProfitTicks))
            .OrderBy(i => i.Symbol).ToList();
    }

    public IReadOnlyList<DataSourceDef> GetDataSources()
    {
        var list = new List<DataSourceDef>();
        bool sierra = File.Exists(SierraLocalPath);
        list.Add(new DataSourceDef("sierra-mesm26", "sierra-aggregated",
            "Sierra lokal (MESM26) → Zeitkerzen aus Last-Preisen", sierra,
            sierra ? "Aggregiert echte Handelskerzen (Last). Randkerzen werden ausgeschlossen."
                   : "Lokale Sierra-Datei nicht gefunden."));

        var ohlcDir = Path.Combine(_repoRoot, "samples", "ohlc");
        if (Directory.Exists(ohlcDir))
            foreach (var f in Directory.EnumerateFiles(ohlcDir, "*.csv").OrderBy(x => x))
                list.Add(new DataSourceDef("csv:" + Path.GetFileName(f), "ohlc-csv",
                    "OHLC-CSV: " + Path.GetFileName(f), true, null));
        return list;
    }

    public async Task<BacktestRunResponse> RunAsync(BacktestRunRequest req, CancellationToken ct = default)
    {
        try
        {
            var instrument = (await _instruments.GetAllAsync(ct)).FirstOrDefault(i =>
                string.Equals(i.Symbol, req.Symbol, StringComparison.OrdinalIgnoreCase));
            if (instrument is null)
                return new BacktestRunResponse { Ok = false, Error = $"Kein InstrumentProfile für '{req.Symbol}' in config/instruments." };

            var fee = await ResolveFeeAsync(req, ct);

            var (candles, leadPartial, trailPartial, issues, source, tf) = LoadCandles(req, instrument.Symbol);
            if (candles.Count == 0)
                return new BacktestRunResponse { Ok = false, Error = "Keine gültigen OHLC-Bars geladen.", DataIssues = issues };

            var strategy = BuildStrategy(req, instrument);

            var config = new OhlcBacktestConfig
            {
                Quantity = req.Quantity,
                InitialBalance = req.InitialBalance,
                StopLossTicks = req.StopLossTicks,
                TakeProfitTicks = req.TakeProfitTicks,
                SlippageTicksOverride = req.SlippageTicks,
                ApplyFees = req.ApplyFees,
                ExcludePartialEdges = req.ExcludePartialEdges
            };

            var result = _engine.Run(candles, strategy, instrument, fee, config, source, tf, leadPartial, trailPartial);

            var dto = candles.Select(c => new CandleDto(
                c.OpenTime.ToUnixTimeMilliseconds(), c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();

            // Tatsächlich verwendete Instrument-/Kostenwerte (mit Einheiten im Dashboard sichtbar).
            var costProfile = new CostProfileDto(
                TickSize: instrument.TickSize,
                TickValue: instrument.TickValue,
                PointValue: instrument.PointValue,
                Currency: instrument.Currency,
                FeePerSide: result.FeePerSide,
                FeeRoundTrip: result.FeePerSide * 2m,
                SlippageTicks: result.EffectiveSlippageTicks,
                SlippagePerSideDollars: result.EffectiveSlippageTicks * instrument.TickValue,
                ApplyFees: config.ApplyFees,
                InstrumentIsExample: OnlyExampleProfiles(_instrumentsDir),
                FeeIsExample: req.FeePerSideOverride is null && OnlyExampleProfiles(_feesDir));

            return new BacktestRunResponse { Ok = true, Result = result, CostProfile = costProfile, Candles = dto, DataIssues = issues };
        }
        catch (Exception ex)
        {
            return new BacktestRunResponse { Ok = false, Error = ex.Message };
        }
    }

    private async Task<FeeProfile> ResolveFeeAsync(BacktestRunRequest req, CancellationToken ct)
    {
        var all = await _fees.GetAllAsync(ct);
        var baseFee = all.FirstOrDefault(f => string.Equals(f.Instrument, req.Symbol, StringComparison.OrdinalIgnoreCase))
                      ?? all.FirstOrDefault()
                      ?? new FeeProfile { BrokerName = "n/a", ExecutionProvider = "n/a", Instrument = req.Symbol };
        if (req.FeePerSideOverride is decimal ov)
            baseFee = baseFee with
            {
                CommissionPerSide = ov, ExchangeFeePerSide = 0m, ClearingFeePerSide = 0m,
                RoutingFeePerSide = 0m, NfaFeePerSide = 0m, OtherFeePerSide = 0m
            };
        if (req.SlippageTicks is decimal sl) baseFee = baseFee with { EstimatedSlippageTicks = sl };
        return baseFee;
    }

    private IStrategy BuildStrategy(BacktestRunRequest req, InstrumentProfile instrument)
    {
        var strategy = req.Strategy.ToLowerInvariant() switch
        {
            "movingaverage" or "ma" or "sma" => (IStrategy)new MovingAverageDummyStrategy(),
            _ => new MovingAverageDummyStrategy()
        };
        strategy.Initialize(new StrategyExecutionContext
        {
            Symbol = instrument.Symbol,
            Instrument = instrument,
            Config = new StrategyConfig
            {
                Name = strategy.Name,
                Symbol = instrument.Symbol,
                Parameters = req.Params ?? new Dictionary<string, string>()
            }
        });
        return strategy;
    }

    private (List<Candle> candles, bool lead, bool trail, IReadOnlyList<OhlcImportIssue> issues, string source, int tf)
        LoadCandles(BacktestRunRequest req, string symbol)
    {
        int tf = req.TimeframeMinutes <= 0 ? 5 : req.TimeframeMinutes;

        if (req.DataSourceId.StartsWith("csv:", StringComparison.Ordinal) ||
            (req.DataSourceId == "csv" && !string.IsNullOrWhiteSpace(req.Path)))
        {
            string path = req.Path ?? Path.Combine(_repoRoot, "samples", "ohlc", req.DataSourceId.Substring(4));
            var imp = new OhlcCsvImporter().ImportFile(path, symbol, tf);
            return (imp.Candles.ToList(), imp.LeadingPartial, imp.TrailingPartial, imp.Issues,
                "OHLC-CSV: " + Path.GetFileName(path), imp.TimeframeMinutes);
        }

        // Sierra-Aggregation: echte Zeitkerzen aus Last-Preisen (kein Ask/Bid als Docht).
        if (!File.Exists(SierraLocalPath))
            throw new FileNotFoundException($"Lokale Sierra-Datei nicht gefunden: {SierraLocalPath}");

        var builder = new SierraOrderFlowBarBuilder();
        SierraAggregationResult agg;
        if (!string.IsNullOrWhiteSpace(req.FromUtc) &&
            DateTimeOffset.TryParse(req.FromUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                out var from))
        {
            agg = builder.BuildFileFrom(SierraLocalPath, symbol, TimeSpan.FromMinutes(tf), from.ToUniversalTime(),
                toUtc: null, maxRows: req.MaxRows);
        }
        else
        {
            agg = builder.BuildFile(SierraLocalPath, symbol, TimeSpan.FromMinutes(tf), maxRows: req.MaxRows);
        }

        var candles = agg.Bars.Select(b => new Candle
        {
            Symbol = symbol,
            OpenTime = b.Bar.OpenTime,
            CloseTime = b.Bar.CloseTime,
            Open = b.Bar.Open, High = b.Bar.High, Low = b.Bar.Low, Close = b.Bar.Close,
            Volume = b.Bar.TotalVolume
        }).ToList();

        bool lead = agg.FirstTickTime is DateTimeOffset ft && agg.FirstBarTime is DateTimeOffset fb && ft > fb;
        bool trail = agg.Truncated;
        string source = $"Sierra lokal (Last-Aggregation, {tf}-Min)";
        return (candles, lead, trail, Array.Empty<OhlcImportIssue>(), source, tf);
    }
}
