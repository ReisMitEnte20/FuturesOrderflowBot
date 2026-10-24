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

string repoRoot = args.Length > 1 ? args[1] : FindRepoRoot();
string recording = args.Length > 0 ? args[0]
    : @"C:\Users\Anton Minh Duy Tran\ATAS_OF_Recordings\MNQ_20261023_084204Z\ticks.csv";

Console.WriteLine($"Repo:      {repoRoot}");
Console.WriteLine($"Aufnahme:  {recording}");
if (!File.Exists(recording)) { Console.Error.WriteLine($"FEHLER: Aufnahme nicht gefunden: {recording}"); return 2; }

var jsonRead = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter() }
};

// ---- Konfiguration ausschliesslich aus config/ (keine hardcodierten Fees/TickSizes) ----
string cfg(string rel) => Path.Combine(repoRoot, "config", rel);
var instrument = Load<InstrumentProfile>(cfg(@"instruments\mnq.example.json"), jsonRead);
var fee = Load<FeeProfile>(cfg(@"fees\amp-rithmic-mnq.example.json"), jsonRead);
var broker = Load<BrokerProfile>(cfg(@"brokers\amp-rithmic.example.json"), jsonRead);
var risk = Load<RiskConfig>(cfg(@"risk\research-sim.example.json"), jsonRead);
var importProfile = Load<CsvImportProfile>(cfg(@"import-profiles\atas-tick.example.json"), jsonRead);

var account = new TradingAccount
{
    AccountId = "research-sim",
    BrokerName = broker.BrokerName,
    AccountType = broker.AccountType,
    Currency = instrument.Currency,
    StartingBalance = StartingBalance
};

// ---- Import + Fingerabdruck ----
string sha = Sha256File(recording);
var set = new AtasTickCsvImporter(importProfile).ImportFile(recording);
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
    DataSource = $"ATAS-Replay-Aufnahme {Path.GetFileName(Path.GetDirectoryName(recording))}",
    DataFilePath = recording,
    DataSha256 = sha,
    CapabilitiesSufficient = set.Capabilities.SupportsDeltaCvd,
    AggressorNote = "aus Aufnahme (AggressorSide je Trade)",
    RowsRead = set.Quality.RowsRead,
    RowsAccepted = set.Quality.RowsAccepted,
    DeltaThreshold = DeltaFlowStrategy.DefaultDeltaThreshold,
    SettingsUsed = new Dictionary<string, string>
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
        ["instrument.pointValue"] = instrument.PointValue.ToString(),
        ["fee.estimatedSlippageTicks"] = fee.EstimatedSlippageTicks.ToString(),
        ["startingBalance"] = StartingBalance.ToString("F2"),
        ["ticksPerBar"] = "500"
    }
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
