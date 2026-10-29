using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Application.Strategies.OrderFlow;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.MarketData.Import;
using TradingBot.Research;
using TradingBot.Research.Orderflow;

// ------------------------------------------------------------------------------------------------
//  Orderflow-Mehrstrategie-Runner (Repo-Tool).
//  Faehrt die drei vorhandenen Teststrategien (Momentum/Fade/DeltaFlow) ueber den BESTEHENDEN
//  Tick-Backtester (BacktestStrategyRunner + BacktestEngine), 500 Ticks/Bar, aus EINER ATAS-Aufnahme.
//  Schreibt EIN Lauf-Artefakt (Workspace-JSON + Manifest) + die Datei, die das Dashboard laedt.
//  Nur Simulation/Research. KEINE Orders, KEINE zweite Engine, KEINE Optimierung/Holdout.
// ------------------------------------------------------------------------------------------------

const decimal StartingBalance = 10_000m; // Research-Startkapital (keine Broker-Kontoconfig vorhanden) - in Provenienz gespeichert.

// ---- Argumente: Sierra-Fenster-Lauf ODER (Default, rueckwaertskompatibel) ATAS-Aufnahme ----
//   Sierra:  --sierra <pfad> [--symbol MES] --from <isoUtc> --to <isoUtc> [--repo <root>] [--max-tape N]
//   ATAS  :  <recording> [repoRoot]        (unveraendert)
var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var pos = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("--", StringComparison.Ordinal))
    {
        string k = args[i];
        string v = (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) ? args[++i] : "true";
        opts[k] = v;
    }
    else pos.Add(args[i]);
}
bool sierraMode = opts.ContainsKey("--sierra");

string repoRoot = opts.TryGetValue("--repo", out var repoOpt) ? repoOpt : (pos.Count > 1 ? pos[1] : FindRepoRoot());

var jsonRead = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter() }
};
// ---- Konfiguration ausschliesslich aus config/ (keine hardcodierten Fees/TickSizes) ----
string cfg(string rel) => Path.Combine(repoRoot, "config", rel);

// gemeinsame Laufvariablen (beide Modi)
InstrumentProfile instrument;
FeeProfile fee;
BrokerProfile broker;
RiskConfig risk;
ImportedMarketDataSet set;
string sha, dataFilePath, dataSource, aggressorNote, priceGranularityNote, footprintSourceNote;
int maxTape;
Dictionary<string, string> settingsUsed;

if (sierraMode)
{
    string sierraPath = opts["--sierra"];
    string symbol = opts.TryGetValue("--symbol", out var sy) ? sy : "MES";
    if (!opts.TryGetValue("--from", out var fromStr) || !opts.TryGetValue("--to", out var toStr))
    { Console.Error.WriteLine("FEHLER: --from <isoUtc> und --to <isoUtc> sind im --sierra-Modus Pflicht."); return 2; }
    var fromUtc = DateTimeOffset.Parse(fromStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    var toUtc = DateTimeOffset.Parse(toStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    maxTape = opts.TryGetValue("--max-tape", out var mt) && int.TryParse(mt, out var mtv) ? mtv : 5000;

    Console.WriteLine($"Repo:      {repoRoot}");
    Console.WriteLine($"Sierra:    {sierraPath}");
    Console.WriteLine($"Symbol:    {symbol}");
    Console.WriteLine($"Fenster:   {fromUtc:O} .. {toUtc:O} (UTC, inklusive)");
    if (!File.Exists(sierraPath)) { Console.Error.WriteLine($"FEHLER: Sierra-Datei nicht gefunden: {sierraPath}"); return 2; }

    instrument = Load<InstrumentProfile>(cfg(@"instruments\mes.example.json"), jsonRead);
    fee = Load<FeeProfile>(cfg(@"fees\amp-rithmic-mes.example.json"), jsonRead);
    broker = Load<BrokerProfile>(cfg(@"brokers\amp-rithmic.example.json"), jsonRead);
    risk = Load<RiskConfig>(cfg(@"risk\research-sim.example.json"), jsonRead);

    Console.WriteLine("SHA-256 der Quelldatei (streamend) ...");
    var swSha = System.Diagnostics.Stopwatch.StartNew();
    sha = Sha256File(sierraPath);
    swSha.Stop();
    Console.WriteLine($"SHA-256:   {sha}  ({swSha.Elapsed.TotalSeconds:F1}s)");

    Console.WriteLine("Lese NUR das UTC-Fenster (streamend, kein Materialisieren der Gesamtdatei) ...");
    var swImp = System.Diagnostics.Stopwatch.StartNew();
    long lastProg = 0;
    set = new SierraMarketDataAdapter().ImportWindowedSet(sierraPath, symbol, fromUtc, toUtc,
        onProgress: n => { if (n - lastProg >= 5_000_000) { lastProg = n; Console.WriteLine($"  ... {n:N0} Zeilen gescannt"); } });
    swImp.Stop();
    Console.WriteLine($"Fenster gelesen in {swImp.Elapsed.TotalSeconds:F1}s.");

    dataFilePath = sierraPath;
    dataSource = $"Sierra MES {fromUtc:yyyy-MM-dd} (UTC-Kalendertag {fromUtc:HH:mm}-{toUtc:HH:mm}Z, keine Boersensession)";
    aggressorNote = "aus Sierra BidVolume/AskVolume je Trade (am Ask=Kaeufer/Buy, am Bid=Verkaeufer/Sell); im Fenster voll klassifiziert";
    priceGranularityNote = "0,25 (nativ, Sierra MES-Tick); NICHT korrigiert";
    footprintSourceNote = "aus Sierra BidVolume/AskVolume je Trade, je 500-Tick-Bar (kein nativer ATAS-Footprint)";
    settingsUsed = new Dictionary<string, string>
    {
        ["mode"] = "sierra-windowed",
        ["instrumentConfig"] = "config/instruments/mes.example.json (EXAMPLE VALUES = Annahmen)",
        ["feeConfig"] = "config/fees/amp-rithmic-mes.example.json (EXAMPLE VALUES = Annahmen)",
        ["brokerConfig"] = "config/brokers/amp-rithmic.example.json",
        ["riskConfig"] = "config/risk/research-sim.example.json (PERMISSIV, nur dieser lokale Simulationslauf)",
        ["window.fromUtc"] = fromUtc.ToString("O", CultureInfo.InvariantCulture),
        ["window.toUtc"] = toUtc.ToString("O", CultureInfo.InvariantCulture),
        ["window.tzEvidence"] = "Date/Time werden als UTC interpretiert (Sierra-Intraday-Konvention bzw. Importer-Annahme, DateTimeStyles.AssumeUniversal); NICHT unabhaengig aus der Datei verifiziert.",
        ["maxTapeEntries"] = maxTape.ToString(),
        ["risk.enforceSession"] = risk.EnforceSession.ToString(),
        ["risk.maxContracts"] = risk.MaxContracts.ToString(),
        ["risk.maxOpenPositions"] = risk.MaxOpenPositions.ToString(),
        ["risk.maxDailyLoss"] = risk.MaxDailyLoss.ToString("F2"),
        ["instrument.tickSize"] = instrument.TickSize.ToString(),
        ["instrument.pointValue"] = instrument.PointValue.ToString(),
        ["fee.estimatedSlippageTicks"] = fee.EstimatedSlippageTicks.ToString(),
        ["startingBalance"] = StartingBalance.ToString("F2"),
        ["ticksPerBar"] = "500"
    };
}
else
{
    string recording = pos.Count > 0 ? pos[0]
        : @"C:\Users\Anton Minh Duy Tran\ATAS_OF_Recordings\MNQ_20261023_084204Z\ticks.csv";

    Console.WriteLine($"Repo:      {repoRoot}");
    Console.WriteLine($"Aufnahme:  {recording}");
    if (!File.Exists(recording)) { Console.Error.WriteLine($"FEHLER: Aufnahme nicht gefunden: {recording}"); return 2; }

    instrument = Load<InstrumentProfile>(cfg(@"instruments\mnq.example.json"), jsonRead);
    fee = Load<FeeProfile>(cfg(@"fees\amp-rithmic-mnq.example.json"), jsonRead);
    broker = Load<BrokerProfile>(cfg(@"brokers\amp-rithmic.example.json"), jsonRead);
    risk = Load<RiskConfig>(cfg(@"risk\research-sim.example.json"), jsonRead);
    var importProfile = Load<CsvImportProfile>(cfg(@"import-profiles\atas-tick.example.json"), jsonRead);

    sha = Sha256File(recording);
    set = new AtasTickCsvImporter(importProfile).ImportFile(recording);
    dataFilePath = recording;
    dataSource = $"ATAS-Replay-Aufnahme {Path.GetFileName(Path.GetDirectoryName(recording))}";
    aggressorNote = "aus Aufnahme (AggressorSide je Trade)";
    // Quellenbezogene Granularitaet: beobachtete Preisschrittweite (aus den tatsaechlichen Ticks) GETRENNT
    // von der Instrument-TickSize ausweisen. So bleiben alte 1-Punkt-Aufnahmen als 1,0 erkennbar und werden
    // nicht nachtraeglich als 0,25 ausgegeben; 0,25-Aufnahmen erscheinen als 0,25.
    decimal observedTick = ObservedPriceGranularity(set.Ticks);
    priceGranularityNote = observedTick <= 0m
        ? $"beobachtet: n/a (zu wenige Preise); Instrument-TickSize {instrument.TickSize}"
        : observedTick == instrument.TickSize
            ? $"{observedTick} (beobachtet = Instrument-TickSize {instrument.TickSize})"
            : observedTick > instrument.TickSize
                ? $"{observedTick} beobachtet — GROEBER als Instrument-TickSize {instrument.TickSize} (nicht korrigiert)"
                : $"{observedTick} beobachtet (feiner als Instrument-TickSize {instrument.TickSize})";
    footprintSourceNote = "aus Ticks je 500-Tick-Bar (Footprint/Delta aus AggressorSide je Trade)";
    maxTape = 5000;                         // begrenzt NUR den Dashboard-Tape-Export; Simulation + Kerzen/Footprint/Delta/CVD nutzen ALLE Ticks
    settingsUsed = new Dictionary<string, string>
    {
        ["instrumentConfig"] = "config/instruments/mnq.example.json",
        ["feeConfig"] = "config/fees/amp-rithmic-mnq.example.json",
        ["brokerConfig"] = "config/brokers/amp-rithmic.example.json",
        ["riskConfig"] = "config/risk/research-sim.example.json (PERMISSIV, nur dieser lokale Simulationslauf)",
        ["importProfile"] = "config/import-profiles/atas-tick.example.json",
        ["risk.enforceSession"] = risk.EnforceSession.ToString(),
        ["risk.maxContracts"] = risk.MaxContracts.ToString(),
        ["risk.maxOpenPositions"] = risk.MaxOpenPositions.ToString(),
        ["risk.maxDailyLoss"] = risk.MaxDailyLoss.ToString("F2"),
        ["instrument.tickSize"] = instrument.TickSize.ToString(),
        ["observedPriceGranularity"] = observedTick.ToString(),
        ["instrument.pointValue"] = instrument.PointValue.ToString(),
        ["fee.estimatedSlippageTicks"] = fee.EstimatedSlippageTicks.ToString(),
        ["maxTapeEntries"] = maxTape.ToString(),
        ["startingBalance"] = StartingBalance.ToString("F2"),
        ["ticksPerBar"] = "500"
    };
}

var account = new TradingAccount
{
    AccountId = "research-sim",
    BrokerName = broker.BrokerName,
    AccountType = broker.AccountType,
    Currency = instrument.Currency,
    StartingBalance = StartingBalance
};

Console.WriteLine($"Import:    {set.Quality.RowsAccepted}/{set.Quality.RowsRead} Ticks akzeptiert, Symbol {set.Symbol}, Caps.DeltaCvd={set.Capabilities.SupportsDeltaCvd}");
if (!set.Capabilities.SupportsDeltaCvd)
{
    Console.Error.WriteLine("ABBRUCH: Ticks nicht voll klassifiziert (keine ehrliche Delta/CVD-Basis).");
    return 3;
}

// ---- Die drei vorhandenen Teststrategien als Kandidaten (unveraenderte Regeln), je im
//      SinglePositionGate: max. 1 Kontrakt, kein Aufstocken - wie im urspruenglichen Harness. ----
var candidates = OrderflowProofCandidates.BuildGated(set.Symbol, DeltaFlowStrategy.DefaultDeltaThreshold);

var request = new OrderflowRunRequest
{
    Ticks = set.Ticks,
    Symbol = set.Symbol,
    Instrument = instrument,
    Fee = fee,
    Broker = broker,
    Risk = risk,
    Account = account,
    TicksPerBar = 500,
    SlippageTicksOverride = null, // nutzt FeeProfile.EstimatedSlippageTicks (= config)
    Candidates = candidates,
    DataSource = dataSource,
    DataFilePath = dataFilePath,
    DataSha256 = sha,
    CapabilitiesSufficient = set.Capabilities.SupportsDeltaCvd,
    AggressorNote = aggressorNote,
    RowsRead = set.Quality.RowsRead,
    RowsAccepted = set.Quality.RowsAccepted,
    DeltaThreshold = DeltaFlowStrategy.DefaultDeltaThreshold,
    MaxTapeEntries = maxTape,
    PriceGranularityNote = priceGranularityNote,
    FootprintSourceNote = footprintSourceNote,
    SettingsUsed = settingsUsed
};

var result = await new OrderflowMultiStrategyRunner().RunAsync(request);

// ---- Ausgabe/Speicherung ----
var jsonWrite = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    Converters = { new JsonStringEnumConverter() }
};

bool runOk = result.Workspace.Strategies.All(s => s.Status == "Completed");
string runStatus = runOk ? "ok" : "fehlgeschlagen";
var failedStrategies = result.Workspace.Strategies.Where(s => s.Status != "Completed").Select(s => s.Name).ToArray();

string runsRoot = Path.Combine(repoRoot, "artifacts", "orderflow", "runs");
string runDir = Path.Combine(runsRoot, result.RunId);
Directory.CreateDirectory(runDir);
string workspacePath = Path.Combine(runDir, "workspace.json");
string manifestPath = Path.Combine(runDir, "manifest.json");

File.WriteAllText(workspacePath, JsonSerializer.Serialize(result.Workspace, jsonWrite));
File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
{
    runId = result.RunId, status = runStatus, ok = runOk, failedStrategies,
    result.Workspace.Meta,
    result.Workspace.Quality,
    result.Workspace.Provenance,
    strategies = result.Workspace.Strategies.Select(s => new
    {
        s.Name, s.UsesOrderflow, trades = s.Trades.Count, s.Status,
        s.RealizedNetPnL, s.RealizedFees, s.OpenPositionEntryFees, s.TotalFeesPaid,
        s.UnrealizedGrossAtLastTick, s.LastTickPrice, s.EndEquityRealized, s.EndEquityMarkToMarket,
        s.OpenPositionSide, s.OpenPositionQty, s.OpenPositionAvgPrice,
        s.SignalsGenerated, s.OrdersSubmitted, s.OrdersRejectedByRisk, s.OrdersRejectedByBroker, s.OrdersUnfilledAtEnd
    })
}, new JsonSerializerOptions(jsonWrite) { WriteIndented = true }));

// ---- Dashboard-Spiegelung (read-only) + Index ueber ALLE vorhandenen Laeufe ----
string pubRuns = Path.Combine(repoRoot, "dashboard", "public", "orderflow-runs");
Directory.CreateDirectory(pubRuns);
File.WriteAllText(Path.Combine(pubRuns, result.RunId + ".json"), JsonSerializer.Serialize(result.Workspace, jsonWrite));
// Latest/Fallback (Zwischenstand bleibt gueltig):
File.WriteAllText(Path.Combine(repoRoot, "dashboard", "public", "orderflow-run.json"), JsonSerializer.Serialize(result.Workspace, jsonWrite));

var index = BuildIndex(runsRoot, pubRuns);
string indexPath = Path.Combine(pubRuns, "index.json");
File.WriteAllText(indexPath, JsonSerializer.Serialize(index, new JsonSerializerOptions(jsonWrite) { WriteIndented = true }));

Console.WriteLine($"\nLauf-ID:   {result.RunId}  (Status {runStatus})");
Console.WriteLine($"Datenbasis: {request.Ticks.Count} Ticks = {result.FullBars} volle 500-Tick-Bars + {result.RemainderTicks} Rest-Ticks (keine Signal-Bar).");
Console.WriteLine("\nStrategien (getrennte Ledgers, max. 1 Kontrakt):");
foreach (var s in result.Workspace.Strategies)
{
    Console.WriteLine($"  {s.Name,-18} Trades {s.Trades.Count,4} | real.Netto {s.RealizedNetPnL,9:F2} | unreal.@letzterTick {s.UnrealizedGrossAtLastTick,8:F2} | "
        + $"Fees(ges) {s.TotalFeesPaid,7:F2} | EndEquity real {s.EndEquityRealized,9:F2} / MtM {s.EndEquityMarkToMarket,9:F2}");
    Console.WriteLine($"      Status {s.Status} | Signale {s.SignalsGenerated} | submit {s.OrdersSubmitted} rej(Risk/Broker) {s.OrdersRejectedByRisk}/{s.OrdersRejectedByBroker} | Pending@Ende {s.OrdersUnfilledAtEnd} | "
        + $"offene Position@Ende {s.OpenPositionSide} {s.OpenPositionQty} @ {s.OpenPositionAvgPrice:F2} (Eintritts-Fees {s.OpenPositionEntryFees:F2}, KEINE Exit-Fee)");
}

Console.WriteLine("\nAbweichungen zum Harness (dokumentiert, nicht erzwungen identisch):");
foreach (var d in result.Workspace.Provenance.DivergencesFromHarness) Console.WriteLine($"  - {d}");

Console.WriteLine($"\nGeschrieben:\n  {workspacePath}\n  {manifestPath}\n  {Path.Combine(pubRuns, result.RunId + ".json")}\n  {indexPath}  ({index.Count} Lauf/Laeufe im Index)");
Console.WriteLine("Dashboard: Research-Tab -> Orderflow-Arbeitsflaeche -> Lauf im Dropdown auswaehlbar.");
return 0;

// ------------------------------------------------------------------------------------------------
static T Load<T>(string path, JsonSerializerOptions opts)
{
    if (!File.Exists(path)) throw new FileNotFoundException($"Config nicht gefunden: {path}");
    var obj = JsonSerializer.Deserialize<T>(File.ReadAllText(path), opts);
    return obj ?? throw new InvalidOperationException($"Config leer/ungueltig: {path}");
}

static string Sha256File(string path)
{
    using var sha = SHA256.Create();
    using var fs = File.OpenRead(path);
    return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TradingBot.sln"))) dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

// Beobachtete Preis-Schrittweite aus den tatsaechlichen Tick-Preisen: GCD der Differenzen zu einem Basispreis
// (in 1/100-Einheiten). Liefert z. B. 0,25 bei Viertel-Ticks, 1,0 bei ganzzahligen Preisen. 0 = unbestimmbar.
static decimal ObservedPriceGranularity(IReadOnlyList<MarketTick> ticks)
{
    long g = 0, baseC = long.MinValue;
    foreach (var t in ticks)
    {
        long c = (long)Math.Round(t.Price * 100m);
        if (baseC == long.MinValue) { baseC = c; continue; }
        long d = c - baseC; if (d < 0) d = -d;
        if (d == 0) continue;
        g = Gcd(g, d);
        if (g == 1) break; // feiner als 0,01 nicht moeglich
    }
    return g == 0 ? 0m : g / 100m;
}

static long Gcd(long a, long b) { while (b != 0) { var t = b; b = a % b; a = t; } return a; }

// Read-only Index ueber alle vorhandenen Laeufe (artifacts/orderflow/runs/<id>/manifest.json).
// Spiegelt jeden Workspace nach dashboard/public/orderflow-runs/<id>.json (damit aeltere Laeufe weiter ladbar
// bleiben) und kennzeichnet fehlgeschlagene/unlesbare Laeufe. Neueste zuerst.
static List<object> BuildIndex(string runsRoot, string pubRuns)
{
    var entries = new List<(string gen, object entry)>();
    if (!Directory.Exists(runsRoot)) return new List<object>();

    foreach (var dir in Directory.EnumerateDirectories(runsRoot))
    {
        string id = Path.GetFileName(dir);
        string manifest = Path.Combine(dir, "manifest.json");
        string workspace = Path.Combine(dir, "workspace.json");
        try
        {
            if (!File.Exists(manifest))
            {
                entries.Add(("", new { id, generatedUtc = "", status = "fehlgeschlagen", ok = false, loadable = false, note = "kein Manifest", strategies = Array.Empty<object>() }));
                continue;
            }
            if (File.Exists(workspace))
                File.Copy(workspace, Path.Combine(pubRuns, id + ".json"), overwrite: true);
            bool loadable = File.Exists(Path.Combine(pubRuns, id + ".json"));

            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = doc.RootElement;
            string gen = TryStr(root, "meta", "generatedUtc") ?? "";
            // Alte Manifeste ohne status-Feld: NICHT pauschal "ok", sondern als Legacy/Status unbekannt kennzeichnen.
            bool hasStatus = root.TryGetProperty("status", out var st);
            bool legacy = !hasStatus;
            string status = hasStatus ? (st.GetString() ?? "unbekannt") : "unbekannt (Legacy)";
            bool ok = !legacy && (root.TryGetProperty("ok", out var okEl) ? okEl.GetBoolean() : status == "ok");

            var strategies = new List<object>();
            if (root.TryGetProperty("strategies", out var sarr) && sarr.ValueKind == JsonValueKind.Array)
                foreach (var s in sarr.EnumerateArray())
                    strategies.Add(new
                    {
                        name = s.TryGetProperty("name", out var n) ? n.GetString() : "",
                        trades = s.TryGetProperty("trades", out var t) && t.TryGetInt32(out var tv) ? tv : 0,
                        status = s.TryGetProperty("status", out var ss) ? ss.GetString() : "",
                        realizedNetPnL = s.TryGetProperty("realizedNetPnL", out var r) && r.TryGetDecimal(out var rv) ? rv : 0m,
                        endEquityMarkToMarket = s.TryGetProperty("endEquityMarkToMarket", out var e) && e.TryGetDecimal(out var ev) ? ev : 0m,
                        openPositionSide = s.TryGetProperty("openPositionSide", out var o) ? o.GetString() : "Flat"
                    });

            entries.Add((gen, new
            {
                id,
                generatedUtc = gen,
                instrument = TryStr(root, "meta", "instrument") ?? "",
                dataSource = TryStr(root, "meta", "dataSource") ?? "",
                ticks = TryInt(root, "provenance", "ticks"),
                fullBars = TryInt(root, "provenance", "fullBars"),
                status, ok, legacy, loadable, strategies
            }));
        }
        catch (Exception ex)
        {
            entries.Add(("", new { id, generatedUtc = "", status = "fehlgeschlagen", ok = false, loadable = false, note = "Manifest unlesbar: " + ex.Message, strategies = Array.Empty<object>() }));
        }
    }
    return entries.OrderByDescending(e => e.gen, StringComparer.Ordinal).Select(e => e.entry).ToList();
}

static string? TryStr(JsonElement root, string obj, string prop)
    => root.TryGetProperty(obj, out var o) && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(prop, out var p) ? p.GetString() : null;
static int TryInt(JsonElement root, string obj, string prop)
    => root.TryGetProperty(obj, out var o) && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(prop, out var p) && p.TryGetInt32(out var v) ? v : 0;
