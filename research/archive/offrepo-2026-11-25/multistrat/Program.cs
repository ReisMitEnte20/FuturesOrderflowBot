using System.Globalization;
using System.Text.Json;
using TradingBot.Application.MarketData;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.MarketData.Import;

// ------------------------------------------------------------------------------------------------
//  Mehrstrategie-FUNKTIONSNACHWEIS (technisch, wenige Minuten) — KEINE Profitabilitaetsaussage.
//  Reused: AtasTickCsvImporter, OrderFlowBarAggregator, OrderFlowBar, IStrategy.OnOrderFlowBar, InstrumentProfile.
//  NICHT genutzt: natives bars.ndjson (buggy) und unvalidierter DOM.
// ------------------------------------------------------------------------------------------------

var csv = args.Length > 0 ? args[0]
    : @"%USERPROFILE%\ATAS_OF_Recordings\MNQ_20261023_084204Z\ticks.csv";
int ticksPerBar = 500;

var profile = new CsvImportProfile
{
    SourceType = MarketDataSourceType.AggressorTick,
    Delimiter = ',',
    ColumnMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    { ["timestamp"] = "Time", ["symbol"] = "Instrument", ["price"] = "Price", ["volume"] = "Volume", ["tradedirection"] = "AggressorSide" },
};

var set = new AtasTickCsvImporter(profile).ImportFile(csv);
if (!set.Capabilities.SupportsDeltaCvd) { Console.WriteLine("ABBRUCH: Ticks nicht voll klassifiziert."); return; }

var bars = OrderFlowBarAggregator.AggregateByTicks(set.Ticks, ticksPerBar);

// --- Teilbar-Behandlung (ausdruecklich) ---
int fullCount = set.Ticks.Count / ticksPerBar;          // vollstaendige 500-Tick-Bars
int remainder = set.Ticks.Count % ticksPerBar;          // Rest-Ticks der Teilbar
bool hasPartial = bars.Count > fullCount;               // Aggregator haengt Teilbar an
Console.WriteLine($"Datenbasis: {set.Ticks.Count} Ticks = {fullCount} volle {ticksPerBar}-Tick-Bars + {remainder} Rest-Ticks.");
Console.WriteLine($"  Aggregator lieferte {bars.Count} Bars.  Teilbar vorhanden: {hasPartial} " +
                  $"(Index {fullCount}, {remainder} Ticks) -> wird NICHT als regulaer geschlossene Bar signalgebend genutzt.");
Console.WriteLine($"  Signalgebend: nur Bars 0..{fullCount - 1} (volle Bars). EndOfData-Glattstellung am Schluss von Bar {fullCount - 1}.");
Console.WriteLine($"Fenster (volle Bars): {bars[0].OpenTime:O} -> {bars[fullCount - 1].CloseTime:O}");

var mnq = new InstrumentProfile
{
    Symbol = "MNQ", BrokerSymbol = "MNQ", Exchange = "CME", Currency = "USD",
    TickSize = 0.25m, TickValue = 0.50m, PointValue = 2.00m, ContractMultiplier = 2.00m, MaxContracts = 20,
};

const decimal InitialCapital = 10_000m;
const decimal FeePerSide = 0.37m;
const decimal SlippageTicks = 1m;
decimal slip = SlippageTicks * mnq.TickSize;

Console.WriteLine("\nAusfuehrungsmodell:");
Console.WriteLine("  - Signal auf GESCHLOSSENER voller Bar i; Ausfuehrung am OPEN von Bar i+1 (keine Rueckwirkung auf Signalpreis).");
Console.WriteLine($"  - Letzte volle Bar ({fullCount - 1}): Signal wird NICHT als Entry ausgefuehrt; offene Position per EndOfData zum Close glattgestellt.");
Console.WriteLine($"  - Teilbar (Index {fullCount}) komplett ausgeschlossen (kein Signal, kein Fill-Preis).");
Console.WriteLine($"  - Slippage {SlippageTicks} Tick ({slip} Pkt) adverse; Fee {FeePerSide} USD/Kontrakt/Seite; 1 Kontrakt; Kapital {InitialCapital}; PointValue {mnq.PointValue}.");

IStrategy[] MakeStrategies() => new IStrategy[]
{
    new MomentumStrategy(),
    new DeltaFlowStrategy(deltaThreshold: 30m),
    new FadeStrategy(),
};

Console.WriteLine("\n================ KOMBINIERTER LAUF (alle 3, gleiche Datenbasis) ================");
var combined = MakeStrategies().Select(s => RunOne(s, bars, fullCount, mnq, InitialCapital, FeePerSide, slip)).ToList();
PrintResults(combined);
PrintDivergence(combined, bars, fullCount);

// --- results.json fuer die lokale Ergebnisansicht ---
var resultsObj = new
{
    generatedUtc = DateTime.UtcNow,
    dataFile = csv,
    technicalTest = true,
    caveats = new[]
    {
        "Technischer Test - KEINE Profitabilitaetsaussage.",
        "Preisgranularitaet ungeklaert (MNQ real 0,25; Aufnahme 1-Punkt).",
        "Nativer Footprint (bars.ndjson) und DOM NICHT validiert und NICHT als Strategieeingabe genutzt.",
        "Keine registrierten lokalen Drops != vollstaendige Anbieter-Erfassung.",
    },
    window = new { start = bars[0].OpenTime, end = bars[fullCount - 1].CloseTime },
    config = new { ticksPerBar, initialCapital = InitialCapital, feePerSide = FeePerSide, slippageTicks = SlippageTicks, pointValue = mnq.PointValue, symbol = "MNQ" },
    data = new { ticks = set.Ticks.Count, fullBars = fullCount, remainderTicks = remainder },
    strategies = combined.Select(r => new
    {
        name = r.Name,
        usesOrderflow = r.Name.Contains("OF"),
        trades = r.Trades.Count,
        gross = r.Gross,
        fees = r.Fees,
        netPnL = r.NetPnL,
        finalEquity = r.Equity,
        equity = r.EquityCurve.Select(p => new { t = p.T.ToString("O"), e = p.E }),
        tradeList = r.Trades.Select(t => new { entry = t.EntryTime.ToString("HH:mm:ss"), entryPrice = t.EntryPrice, exit = t.ExitTime.ToString("HH:mm:ss"), exitPrice = t.ExitPrice, dir = t.Dir, net = t.NetPnL, eod = t.EndOfData }),
    }),
};
var outJson = @"%USERPROFILE%\ATAS_OF_Recorder\multistrat\results.json";
var jsonText = JsonSerializer.Serialize(resultsObj, new JsonSerializerOptions { WriteIndented = false });
File.WriteAllText(outJson, jsonText);
Console.WriteLine($"\nresults.json geschrieben: {outJson}");

var tplPath = @"%USERPROFILE%\ATAS_OF_Recorder\multistrat\template.html";
if (File.Exists(tplPath))
{
    var viewHtml = File.ReadAllText(tplPath).Replace("__DATA_JSON__", jsonText);
    var outHtml = @"%USERPROFILE%\ATAS_OF_Recorder\multistrat\view.html";
    File.WriteAllText(outHtml, viewHtml);
    Console.WriteLine($"view.html geschrieben: {outHtml}");
}

Console.WriteLine("\n================ REPRODUZIERBARKEIT (zweiter identischer Lauf) ================");
var combined2 = MakeStrategies().Select(s => RunOne(s, bars, fullCount, mnq, InitialCapital, FeePerSide, slip)).ToList();
Console.WriteLine($"  Lauf 1 == Lauf 2: {SameAll(combined, combined2)}");

Console.WriteLine("\n================ LEDGER-ISOLATION (jede Strategie ALLEIN == im kombinierten Lauf) ================");
foreach (var s in MakeStrategies())
{
    var solo = RunOne(s, bars, fullCount, mnq, InitialCapital, FeePerSide, slip);
    var inCombined = combined.First(c => c.Name == solo.Name);
    Console.WriteLine($"  {solo.Name,-18}: allein == kombiniert : {Same(solo, inCombined)}  " +
                      $"(Trades {solo.Trades.Count}, NetPnL {solo.NetPnL:F2}, Equity {solo.Equity:F2})");
}

// ================================================================================================
StratResult RunOne(IStrategy strat, IReadOnlyList<OrderFlowBar> bb, int full, InstrumentProfile instr,
                   decimal cap, decimal fee, decimal slp)
{
    int pos = 0; decimal entryFill = 0m, realized = 0m, fees = 0m; int desired = 0;
    DateTimeOffset entryTime = default;
    var trades = new List<TradeRec>();
    var decisions = new int[full];
    var equity = new List<EqPoint> { new(bb[0].OpenTime, cap) };   // Equity-Kurve (Start + nach jedem Trade-Close)

    void Transition(int newPos, decimal rawPrice, DateTimeOffset time, bool endOfData)
    {
        if (newPos == pos) return;
        if (pos != 0)
        {
            decimal exitFill = rawPrice - Math.Sign(pos) * slp;
            decimal pnl = (exitFill - entryFill) * pos * instr.PointValue;
            fees += fee; realized += pnl;
            trades.Add(new TradeRec(entryTime, entryFill, time, exitFill, pos, pnl - 2 * fee, endOfData));
            equity.Add(new EqPoint(time, cap + realized - fees));
        }
        if (newPos != 0) { entryFill = rawPrice + Math.Sign(newPos) * slp; fees += fee; entryTime = time; }
        pos = newPos;
    }

    for (int i = 0; i < full; i++)
    {
        var sig = strat.OnOrderFlowBar(bb[i]);                 // Signal nur auf voller Bar
        if (sig != null)
            desired = sig.Direction switch { SignalDirection.Long => 1, SignalDirection.Short => -1, _ => 0 };
        decisions[i] = desired;
        if (i < full - 1)
            Transition(desired, bb[i + 1].Open, bb[i + 1].OpenTime, endOfData: false);  // naechste VOLLE Bar
    }
    if (pos != 0)
        Transition(0, bb[full - 1].Close, bb[full - 1].CloseTime, endOfData: true);      // EndOfData-Glattstellung

    decimal net = realized - fees;
    return new StratResult(strat.Name, trades, realized, fees, net, cap + net, decisions, equity);
}

void PrintResults(List<StratResult> rs)
{
    foreach (var r in rs)
    {
        int eod = r.Trades.Count(t => t.EndOfData);
        Console.WriteLine($"\n  [{r.Name}]  Trades {r.Trades.Count} (davon {eod} EndOfData-Exit) | " +
                          $"Brutto {r.Gross:F2}  Gebuehren {r.Fees:F2}  NETTO {r.NetPnL:F2} USD | Equity {InitialCapital:F0}->{r.Equity:F2}");
        foreach (var t in r.Trades.Take(3))
            Console.WriteLine($"     {(t.Dir > 0 ? "LONG " : "SHORT")} {t.EntryTime:HH:mm:ss}@{t.EntryPrice} -> {t.ExitTime:HH:mm:ss}@{t.ExitPrice} net {t.NetPnL:F2}{(t.EndOfData ? " [EndOfData]" : "")}");
        if (r.Trades.Count > 0 && r.Trades[^1].EndOfData && r.Trades.Count > 3)
            Console.WriteLine($"     ... letzter: {(r.Trades[^1].Dir > 0 ? "LONG " : "SHORT")} ...@{r.Trades[^1].ExitPrice} [EndOfData]");
    }
}

void PrintDivergence(List<StratResult> rs, IReadOnlyList<OrderFlowBar> bb, int full)
{
    Console.WriteLine("\n  --- Konkreter Zeitpunkt gegensaetzlicher Entscheidung ---");
    for (int i = 0; i < full; i++)
    {
        var d = rs.Select(r => r.Decisions[i]).ToArray();
        if (d.Any(x => x > 0) && d.Any(x => x < 0))
        {
            var b = bb[i];
            Console.WriteLine($"     Bar {i} @ {b.CloseTime:HH:mm:ss}: O={b.Open} C={b.Close} Delta={b.Delta} (Bid {b.BidVolume}/Ask {b.AskVolume})");
            for (int k = 0; k < rs.Count; k++)
                Console.WriteLine($"        {rs[k].Name,-18} -> {(d[k] > 0 ? "LONG" : d[k] < 0 ? "SHORT" : "FLAT")}");
            return;
        }
    }
}

bool Same(StratResult a, StratResult b)
    => a.Trades.Count == b.Trades.Count && a.NetPnL == b.NetPnL && a.Fees == b.Fees && a.Equity == b.Equity
       && a.Trades.Zip(b.Trades).All(p => p.First == p.Second);
bool SameAll(List<StratResult> a, List<StratResult> b)
    => a.Count == b.Count && a.Zip(b).All(p => p.First.Name == p.Second.Name && Same(p.First, p.Second));

record TradeRec(DateTimeOffset EntryTime, decimal EntryPrice, DateTimeOffset ExitTime, decimal ExitPrice, int Dir, decimal NetPnL, bool EndOfData);
record EqPoint(DateTimeOffset T, decimal E);
record StratResult(string Name, List<TradeRec> Trades, decimal Gross, decimal Fees, decimal NetPnL, decimal Equity, int[] Decisions, List<EqPoint> EquityCurve);

sealed class MomentumStrategy : IStrategy
{
    public string Name => "Momentum(Price)";
    public TradeSignal? OnOrderFlowBar(OrderFlowBar b) =>
        b.Close == b.Open ? null : Helpers.Sig(Name, b, b.Close > b.Open ? SignalDirection.Long : SignalDirection.Short);
}
sealed class FadeStrategy : IStrategy
{
    public string Name => "Fade(Price)";
    public TradeSignal? OnOrderFlowBar(OrderFlowBar b) =>
        b.Close == b.Open ? null : Helpers.Sig(Name, b, b.Close > b.Open ? SignalDirection.Short : SignalDirection.Long);
}
sealed class DeltaFlowStrategy : IStrategy
{
    private readonly decimal _t;
    public DeltaFlowStrategy(decimal deltaThreshold) => _t = deltaThreshold;
    public string Name => "DeltaFlow(OF)";
    public TradeSignal? OnOrderFlowBar(OrderFlowBar b) =>
        b.Delta > _t ? Helpers.Sig(Name, b, SignalDirection.Long)
        : b.Delta < -_t ? Helpers.Sig(Name, b, SignalDirection.Short)
        : Helpers.Sig(Name, b, SignalDirection.Flat);
}
static class Helpers
{
    public static TradeSignal Sig(string name, OrderFlowBar b, SignalDirection dir) => new()
    { StrategyName = name, Symbol = b.Symbol, Direction = dir, Timestamp = b.CloseTime, ReferencePrice = b.Close, SuggestedQuantity = 1 };
}
