using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Application.MarketData;
using TradingBot.Application.Strategies.OrderFlow;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Config;
using TradingBot.Infrastructure.MarketData.Import;
using TradingBot.Research;
using TradingBot.Research.Orderflow;

namespace TradingBot.DevDashboard.Services;

/// <summary>Eine Datenanforderung einer Strategie und ob der gewählte Datensatz sie erfüllt.</summary>
public sealed record DataRequirementCheck(string Requirement, bool Met, string Detail);

/// <summary>Geladene Trade-Ticks (mit Herkunft) für den Tick-Orderflow-Pfad.</summary>
public sealed record LoadedTicks(ImportedMarketDataSet Set, IReadOnlyList<MarketTick> Ticks, string Source, string Path, string Sha256);

/// <summary>
/// Anbindung des Backtesting-Bereichs an den BESTEHENDEN Tick-Orderflow-Pfad (keine zweite Engine):
/// Trade-Ticks werden mit denselben Importern wie die Orderflow-Konsole gelesen und über
/// <see cref="OrderflowMultiStrategyRunner"/> (→ BacktestStrategyRunner → tick-getriebene BacktestEngine) mit den
/// gegateten Kandidaten aus <see cref="OrderflowProofCandidates"/> und denselben Konfigurationsprofilen
/// (Instrument/Gebühren/Broker/Risiko aus config/) simuliert. Delta stammt ausschließlich aus dem gemessenen
/// Aggressor je Trade — aus OHLC-Kerzen wird KEIN Delta geschätzt.
/// </summary>
public sealed class OrderflowTickBacktest
{
    public const string EngineKind = "orderflow-ticks";
    public const string TicksPerBarKey = "TicksPerBar";
    public const int DefaultTicksPerBar = 500;

    private readonly string _repoRoot;
    private static readonly JsonSerializerOptions JsonRead = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private readonly object _lock = new();
    private readonly Dictionary<string, (ImportedMarketDataSet Set, string Sha)> _cache = new(StringComparer.Ordinal);

    public OrderflowTickBacktest(string repoRoot) => _repoRoot = repoRoot;

    /// <summary>Manifest einer ATAS-Collection (MNQ New York), optional; liefert je Tag eine Tick-Datenquelle.</summary>
    public string? CollectionManifest { get; set; }

    public static bool IsTickSource(string dataSourceId) =>
        dataSourceId.StartsWith("ticks-csv:", StringComparison.Ordinal) || dataSourceId.StartsWith("atas-collection:", StringComparison.Ordinal)
        || dataSourceId.StartsWith("synthetic:", StringComparison.Ordinal);

    public static int TicksPerBarOf(IReadOnlyDictionary<string, string>? p) =>
        p is not null && p.TryGetValue(TicksPerBarKey, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n : DefaultTicksPerBar;

    public static decimal ThresholdOf(IReadOnlyDictionary<string, string>? p) =>
        p is not null && p.TryGetValue(DeltaFlowStrategy.ThresholdParameterKey, out var v)
        && decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : DeltaFlowStrategy.DefaultDeltaThreshold;

    // ---- Datenquellen -------------------------------------------------------------------------------

    public IReadOnlyList<DataSourceDef> GetDataSources()
    {
        var list = new List<DataSourceDef>
        {
            new(SyntheticTickFixtures.OneDayId, "ticks-aggressor", "SYNTHETISCH (keine Marktdaten): MNQ-Ticks, 1 Handelstag", true,
                "Im Code erzeugte Testsequenz (6 Bars à 500 Ticks). Für Walk-forward zu kurz — dient der Prüfung der Ablehnung."),
            new(SyntheticTickFixtures.MultiDayId, "ticks-aggressor", "SYNTHETISCH (keine Marktdaten): MNQ-Ticks, 11 Handelstage (Walk-forward-Abnahme)", true,
                "Im Code erzeugte Testsequenz 01.–16.06.2026 (10.06. fehlt absichtlich, 08.06. ohne Einstiegssignal). Kein Marktbezug.")
        };
        var dir = System.IO.Path.Combine(_repoRoot, "samples", "ticks");
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*.csv").OrderBy(x => x))
                list.Add(new DataSourceDef("ticks-csv:" + System.IO.Path.GetFileName(f), "ticks-aggressor",
                    "Trade-Ticks (ATAS-Format): " + System.IO.Path.GetFileName(f), true,
                    "Trade-Ticks mit Aggressor je Trade (config/import-profiles/atas-tick.example.json)."));
        if (CollectionManifest is { } m && File.Exists(m))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(m));
                var ny = TradingBot.Quant.Report.TradeDateCalendar.NewYork;
                foreach (var job in doc.RootElement.GetProperty("jobs").EnumerateObject())
                {
                    if (job.Value.GetProperty("status").GetString() != "committed") continue;
                    var start = DateTimeOffset.Parse(job.Value.GetProperty("window").GetProperty("start").GetString()!, CultureInfo.InvariantCulture);
                    var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, ny).DateTime);
                    list.Add(new DataSourceDef("atas-collection:" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "ticks-aggressor",
                        $"ATAS-Collection MNQ New York {day:yyyy-MM-dd} (Trade-Ticks)", true,
                        "Echte Aufnahme (manifest-geprüfter Import). Trade-Ticks mit Aggressor; keine Quotes/kein Orderbuch."));
                }
            }
            catch (Exception) { /* Manifest unlesbar → keine Collection-Quellen anbieten */ }
        }
        return list.GroupBy(d => d.Id).Select(g => g.First()).ToList();
    }

    // ---- Laden ---------------------------------------------------------------------------------------

    public LoadedTicks Load(BacktestRunRequest req)
    {
        DateTimeOffset? from = Parse(req.FromUtc), to = Parse(req.ToUtc);
        ImportedMarketDataSet set; string source, path, sha;
        if (req.DataSourceId.StartsWith("ticks-csv:", StringComparison.Ordinal))
        {
            path = req.Path ?? System.IO.Path.Combine(_repoRoot, "samples", "ticks", req.DataSourceId["ticks-csv:".Length..]);
            if (!File.Exists(path)) throw new FileNotFoundException("Tick-Datei nicht gefunden: " + path);
            (set, sha) = Cached(path + "|" + new FileInfo(path).Length + "|" + File.GetLastWriteTimeUtc(path).Ticks, () =>
            {
                var profile = JsonSerializer.Deserialize<CsvImportProfile>(
                    File.ReadAllText(System.IO.Path.Combine(_repoRoot, "config", "import-profiles", "atas-tick.example.json")), JsonRead)
                    ?? throw new InvalidOperationException("Importprofil atas-tick.example.json ungültig.");
                return (new AtasTickCsvImporter(profile).ImportFile(path), Sha256File(path));
            });
            source = "Trade-Ticks " + System.IO.Path.GetFileName(path);
        }
        else if (req.DataSourceId.StartsWith("synthetic:", StringComparison.Ordinal))
        {
            var csv = SyntheticTickFixtures.CsvFor(req.DataSourceId) ?? throw new InvalidOperationException($"Unbekannte synthetische Quelle '{req.DataSourceId}'.");
            (set, sha) = Cached(req.DataSourceId, () =>
            {
                var profile = JsonSerializer.Deserialize<CsvImportProfile>(
                    File.ReadAllText(System.IO.Path.Combine(_repoRoot, "config", "import-profiles", "atas-tick.example.json")), JsonRead)
                    ?? throw new InvalidOperationException("Importprofil atas-tick.example.json ungültig.");
                var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(csv))).ToLowerInvariant();
                return (new AtasTickCsvImporter(profile).Import(new StringReader(csv)), hash);
            });
            path = "(im Code erzeugt) " + req.DataSourceId;
            source = req.DataSourceId == SyntheticTickFixtures.OneDayId ? "SYNTHETISCHE MNQ-Ticks (1 Tag)" : "SYNTHETISCHE MNQ-Ticks (11 Handelstage)";
        }
        else if (req.DataSourceId.StartsWith("atas-collection:", StringComparison.Ordinal))
        {
            var manifest = CollectionManifest ?? throw new InvalidOperationException("Keine ATAS-Collection konfiguriert.");
            var day = DateOnly.ParseExact(req.DataSourceId["atas-collection:".Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            (set, sha) = Cached(manifest + "|" + day, () =>
            {
                var res = new AtasCollectionImporter().ImportDay(manifest, day);
                return (res.Data, res.CsvSha256);
            });
            path = manifest;
            source = $"ATAS-Collection MNQ New York {day:yyyy-MM-dd}";
        }
        else throw new InvalidOperationException($"'{req.DataSourceId}' ist keine Tick-Datenquelle.");

        var ticks = set.Ticks.Where(t => (from is null || t.Timestamp >= from) && (to is null || t.Timestamp < to)).ToList();
        return new LoadedTicks(set, ticks, source, path, sha);
    }

    private (ImportedMarketDataSet, string) Cached(string key, Func<(ImportedMarketDataSet, string)> load)
    {
        lock (_lock) if (_cache.TryGetValue(key, out var s)) return s;
        var loaded = load();
        lock (_lock) { _cache[key] = loaded; if (_cache.Count > 4) _cache.Remove(_cache.Keys.First()); }
        return loaded;
    }

    /// <summary>Volle N-Tick-Orderflow-Bars als Kerzen (gleiche Bar-Bildung wie der Orderflow-Lauf; Teilbar ausgeschlossen).</summary>
    public static (List<Candle> Candles, List<OrderFlowBar> Bars) Bars(IReadOnlyList<MarketTick> ticks, int ticksPerBar, string symbol)
    {
        int full = ticks.Count / ticksPerBar;
        var bars = full == 0 ? new List<OrderFlowBar>() : OrderFlowBarAggregator.AggregateByTicks(ticks, ticksPerBar).Take(full).ToList();
        var candles = bars.Select(b => new Candle
        {
            Symbol = symbol, OpenTime = b.OpenTime, CloseTime = b.CloseTime, Open = b.Open, High = b.High, Low = b.Low, Close = b.Close, Volume = b.TotalVolume
        }).ToList();
        return (candles, bars);
    }

    // ---- Handelstage ---------------------------------------------------------------------------------

    /// <summary>Zusammenhängende Tick-Bereiche je CME-Handelstag (Ticks sind zeitlich geordnet).</summary>
    public static List<(string Key, int Start, int End)> SessionRanges(IReadOnlyList<MarketTick> ticks)
    {
        var list = new List<(string, int, int)>();
        int start = 0; string? cur = null;
        for (int i = 0; i < ticks.Count; i++)
        {
            var k = TradingBot.Quant.Report.TradeDateCalendar.Key(ticks[i].Timestamp);
            if (k != cur) { if (cur is not null) list.Add((cur, start, i)); cur = k; start = i; }
        }
        if (cur is not null) list.Add((cur, start, ticks.Count));
        return list;
    }

    /// <summary>Holdout nach ganzen Handelstagen der Ticks: die letzten floor(n·fraction) Tage (identisch zum Walk-forward-Planer).</summary>
    public static int HoldoutSessions(int sessions, double fraction) => fraction is > 0 and < 1 ? (int)Math.Floor(sessions * fraction) : 0;

    /// <summary>Werktage (Mo–Fr) zwischen erstem und letztem Handelstag, für die KEINE Ticks vorliegen (Feiertag oder Datenlücke).</summary>
    public static IReadOnlyList<string> MissingWeekdays(IReadOnlyList<string> sessionKeys)
    {
        if (sessionKeys.Count < 2) return Array.Empty<string>();
        var have = sessionKeys.ToHashSet(StringComparer.Ordinal);
        var res = new List<string>();
        var d = DateOnly.Parse(sessionKeys[0], CultureInfo.InvariantCulture);
        var end = DateOnly.Parse(sessionKeys[^1], CultureInfo.InvariantCulture);
        for (; d <= end; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var k = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!have.Contains(k)) res.Add(k);
        }
        return res;
    }

    // ---- Datenanforderungen --------------------------------------------------------------------------

    public static IReadOnlyList<string> DeltaFlowRequirements(int ticksPerBar) => new[]
    {
        "Trade-Ticks mit gemessenem Aggressor (Käufer/Verkäufer) je Trade — daraus das Delta je Bar",
        $"Mindestens 2 volle {ticksPerBar}-Tick-Bars (Signal auf Bar i, Ausführung frühestens am ersten Tick von Bar i+1)",
        "Instrument- und Gebührenprofil für das Symbol in config/"
    };

    public static IReadOnlyList<DataRequirementCheck> Check(LoadedTicks? t, string dataSourceId, int ticksPerBar, string? ohlcSourceLabel)
    {
        if (t is null)
            return new[]
            {
                new DataRequirementCheck("Trade-Ticks mit Aggressor", false,
                    $"Gewählter Datensatz ({ohlcSourceLabel ?? dataSourceId}) liefert nur OHLC-Kerzen. Aus Kerzen wird KEIN Delta geschätzt — einen Tick-Datensatz wählen.")
            };
        bool classified = t.Set.SourceType == MarketDataSourceType.AggressorTick && t.Set.Capabilities.SupportsDeltaCvd;
        int full = t.Ticks.Count / ticksPerBar;
        return new[]
        {
            new DataRequirementCheck("Trade-Ticks mit Aggressor", classified,
                classified ? $"{t.Ticks.Count} Ticks, alle mit Aggressor klassifiziert ({t.Set.Quality.RowsAccepted}/{t.Set.Quality.RowsRead} Zeilen übernommen)."
                           : "Nicht alle Ticks haben einen Aggressor — Delta wäre nicht gemessen. Lauf nicht möglich."),
            new DataRequirementCheck($"Mindestens 2 volle {ticksPerBar}-Tick-Bars", full >= 2,
                $"{full} volle Bar(s) aus {t.Ticks.Count} Ticks ({t.Ticks.Count % ticksPerBar} Rest-Ticks ohne eigene Bar).")
        };
    }

    // ---- Lauf über den bestehenden Orderflow-Pfad ---------------------------------------------------

    /// <summary>Baut die Eingabe exakt wie die Orderflow-Konsole: gegateter DeltaFlow-Kandidat, Profile aus config/.</summary>
    public OrderflowRunRequest BuildRequest(BacktestRunRequest run, IReadOnlyList<MarketTick> ticks, InstrumentProfile instrument, FeeProfile fee,
        string dataSource, string sha, IReadOnlyDictionary<string, string>? paramOverride = null, string strategy = "deltaflow")
    {
        var p = paramOverride ?? run.Params;
        decimal threshold = ThresholdOf(p);
        var all = OrderflowProofCandidates.BuildGated(instrument.Symbol, threshold);
        var cand = strategy switch
        {
            "momentum" => all.Single(c => c.Name == "Momentum(Price)"),
            _ => all.Single(c => c.Name == "DeltaFlow(OF)")
        };
        var broker = LoadConfig<BrokerProfile>(@"brokers\amp-rithmic.example.json");
        var risk = LoadConfig<RiskConfig>(@"risk\research-sim.example.json");
        if (run.FeePerSideOverride is decimal fo)
            fee = fee with { CommissionPerSide = fo, ExchangeFeePerSide = 0m, ClearingFeePerSide = 0m, RoutingFeePerSide = 0m, NfaFeePerSide = 0m, OtherFeePerSide = 0m };
        if (!run.ApplyFees)
            fee = fee with { CommissionPerSide = 0m, ExchangeFeePerSide = 0m, ClearingFeePerSide = 0m, RoutingFeePerSide = 0m, NfaFeePerSide = 0m, OtherFeePerSide = 0m };
        return new OrderflowRunRequest
        {
            Ticks = ticks, Symbol = instrument.Symbol, Instrument = instrument, Fee = fee, Broker = broker, Risk = risk,
            Account = new TradingAccount
            {
                AccountId = "research-sim", BrokerName = broker.BrokerName, AccountType = broker.AccountType,
                Currency = instrument.Currency, StartingBalance = run.InitialBalance
            },
            TicksPerBar = TicksPerBarOf(p), SlippageTicksOverride = run.SlippageTicks, Candidates = new[] { cand },
            DataSource = dataSource, DataSha256 = sha, CapabilitiesSufficient = true, DeltaThreshold = threshold, MaxTapeEntries = 1
        };
    }

    public static Task<OrderflowRunResult> RunAsync(OrderflowRunRequest request, CancellationToken ct = default)
        => new OrderflowMultiStrategyRunner().RunAsync(request, ct);

    private T LoadConfig<T>(string rel)
    {
        var path = System.IO.Path.Combine(_repoRoot, "config", rel);
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonRead) ?? throw new InvalidOperationException("Config ungültig: " + path);
    }

    private static DateTimeOffset? Parse(string? s) =>
        !string.IsNullOrWhiteSpace(s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var v) ? v.ToUniversalTime() : null;

    private static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
}
