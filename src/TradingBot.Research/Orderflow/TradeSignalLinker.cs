using TradingBot.Backtesting;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Research.Orderflow;

/// <summary>Belegter Nachvollzug EINES Trades: Signal-Bar/Signal-Tick, Fill-Ticks, Marktpreis der Fill-Ticks.
/// Jedes Feld ist entweder durch Engine-Daten bestaetigt oder null (dann steht der Grund in <see cref="Status"/>).</summary>
public sealed record TradeLink
{
    public WsSignal? EntrySignal { get; init; }
    public WsSignal? ExitSignal { get; init; }
    public int? EntryTickIndex { get; init; }
    public int? ExitTickIndex { get; init; }
    public decimal? EntryTickPrice { get; init; }
    public decimal? ExitTickPrice { get; init; }
    public required string Status { get; init; }
}

/// <summary>
/// Ordnet die mitgeschnittenen Signale den abgeschlossenen Engine-Trades zu — ausschliesslich mit
/// pruefbaren Gleichheiten, nichts wird geschaetzt:
///  1. Signal-Bar = die volle Tick-Bar mit identischer Open-/Close-Zeit und identischen OHLCV-Werten
///     (Zeiger laeuft nur vorwaerts, Reihenfolge der Quelle). Signal-Tick = letzter Tick dieser Bar.
///  2. Eroeffnende Signale (Position davor flach) und schliessende Signale (Position davor offen) werden
///     in Reihenfolge den Trades zugeordnet.
///  3. Bestaetigung je Trade: Richtung passt, der Fill liegt auf dem UNMITTELBAR folgenden Tick
///     (gleiche Engine-Zeit) und Fillpreis = Tickpreis ± konfigurierte Slippage (adverse Richtung).
/// Schlaegt eine Bestaetigung fehl, bleiben die Felder null und der Grund steht im Status.
/// </summary>
public static class TradeSignalLinker
{
    public const string Linked = "verknuepft";

    public static IReadOnlyList<TradeLink> Link(
        IReadOnlyList<MarketTick> ticks, IReadOnlyList<OrderFlowBar> fullBars, int ticksPerBar,
        IReadOnlyList<RecordedSignal> signals, IReadOnlyList<BacktestTrade> trades,
        decimal tickSize, decimal slippageTicks)
    {
        TradeLink None(string why) => new() { Status = why };
        if (trades.Count == 0) return Array.Empty<TradeLink>();
        if (signals.Count == 0) return trades.Select(_ => None("Signale dieses Laufs nicht aufgezeichnet")).ToList();
        if (signals.Any(s => !s.PositionKnown))
            return trades.Select(_ => None("Positionszustand beim Signal unbekannt - keine Zuordnung")).ToList();

        // 1. Signal -> volle Bar (vorwaerts, exakte Gleichheit).
        var barOf = new int[signals.Count];
        int j = 0;
        for (int s = 0; s < signals.Count; s++)
        {
            var b = signals[s].Bar;
            while (j < fullBars.Count && !SameBar(fullBars[j], b)) j++;
            if (j >= fullBars.Count)
                return trades.Select(_ => None("Signal-Bar nicht in den vollen Bars gefunden - keine Zuordnung")).ToList();
            barOf[s] = j;
            int sigTick = (j + 1) * ticksPerBar - 1;
            if (sigTick >= ticks.Count || ticks[sigTick].Timestamp != b.CloseTime || ticks[sigTick].Price != b.Close)
                return trades.Select(_ => None("Signal-Tick passt nicht zur Bar - keine Zuordnung")).ToList();
            j++; // jede Bar liefert hoechstens ein Signal
        }

        // 2. Eroeffnende / schliessende Signale. Ein Signal auf dem allerletzten Tick hat keinen Folge-Tick und kann
        //    nie gefuellt werden (bleibt pending) -> gehoert zu keinem Trade.
        var opens = new List<int>();
        var closes = new List<int>();
        for (int s = 0; s < signals.Count; s++)
        {
            if ((barOf[s] + 1) * ticksPerBar >= ticks.Count) continue;
            (signals[s].PositionBefore == PositionSide.Flat ? opens : closes).Add(s);
        }
        if (closes.Count != trades.Count || (opens.Count != trades.Count && opens.Count != trades.Count + 1))
            return trades.Select(_ => None(
                $"Signalanzahl passt nicht eindeutig zu den Trades ({opens.Count} eroeffnend / {closes.Count} schliessend / {trades.Count} Trades) - keine Zuordnung")).ToList();

        decimal slip = slippageTicks * tickSize;
        var result = new List<TradeLink>(trades.Count);
        for (int k = 0; k < trades.Count; k++)
        {
            var tr = trades[k];
            int so = opens[k], sc = closes[k];
            var o = signals[so];
            var c = signals[sc];
            bool longTrade = tr.Side == PositionSide.Long;
            if (sc <= so) { result.Add(None("Schliessendes Signal liegt nicht nach dem eroeffnenden")); continue; }
            if (o.Signal.Direction != (longTrade ? SignalDirection.Long : SignalDirection.Short))
            { result.Add(None("Richtung des eroeffnenden Signals passt nicht zum Trade")); continue; }
            if (c.PositionBefore != tr.Side || c.Signal.Direction == (longTrade ? SignalDirection.Long : SignalDirection.Short))
            { result.Add(None("Schliessendes Signal passt nicht zur offenen Position")); continue; }

            int oTick = (barOf[so] + 1) * ticksPerBar - 1, cTick = (barOf[sc] + 1) * ticksPerBar - 1;
            int ef = oTick + 1, xf = cTick + 1;
            if (ef >= ticks.Count || xf >= ticks.Count) { result.Add(None("Kein Folge-Tick fuer den Fill")); continue; }
            // Entry: Long = Kauf (Tick + Slippage), Short = Verkauf (Tick - Slippage); Exit umgekehrt.
            decimal expEntry = longTrade ? ticks[ef].Price + slip : ticks[ef].Price - slip;
            decimal expExit = longTrade ? ticks[xf].Price - slip : ticks[xf].Price + slip;
            if (ticks[ef].Timestamp != tr.EntryTime || tr.EntryPrice != expEntry)
            { result.Add(None("Entry-Fill liegt nicht auf dem Folge-Tick des Signals (Zeit/Preis)")); continue; }
            if (ticks[xf].Timestamp != tr.ExitTime || tr.ExitPrice != expExit)
            { result.Add(None("Exit-Fill liegt nicht auf dem Folge-Tick des Signals (Zeit/Preis)")); continue; }

            result.Add(new TradeLink
            {
                EntrySignal = ToWs(o, barOf[so], oTick),
                ExitSignal = ToWs(c, barOf[sc], cTick),
                EntryTickIndex = ef,
                ExitTickIndex = xf,
                EntryTickPrice = ticks[ef].Price,
                ExitTickPrice = ticks[xf].Price,
                Status = Linked
            });
        }
        return result;
    }

    private static bool SameBar(OrderFlowBar a, OrderFlowBar b) =>
        a.OpenTime == b.OpenTime && a.CloseTime == b.CloseTime && a.Open == b.Open && a.High == b.High
        && a.Low == b.Low && a.Close == b.Close && a.TotalVolume == b.TotalVolume
        && a.BidVolume == b.BidVolume && a.AskVolume == b.AskVolume;

    private static WsSignal ToWs(RecordedSignal r, int barIndex, int tickIndex) => new()
    {
        BarIndex = barIndex,
        TickIndex = tickIndex,
        TimeUtc = Iso(r.Bar.CloseTime),
        Direction = r.Signal.Direction.ToString(),
        ReferencePrice = r.Signal.ReferencePrice,
        Reason = r.Signal.Reason ?? string.Empty,
        TriggeredConditions = r.Signal.TriggeredConditions ?? Array.Empty<string>(),
        PositionBefore = r.PositionBefore.ToString()
    };

    /// <summary>Exakte Engine-Zeit als ISO-UTC mit 7 Nachkommastellen (100 ns) — verlustfrei, anders als Millisekunden.</summary>
    public static string Iso(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
