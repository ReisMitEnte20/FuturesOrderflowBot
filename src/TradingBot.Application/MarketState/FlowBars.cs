using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.MarketState;

// =====================================================================================================================
// Bars aus EINER geordneten Ereignisfolge (Trades mit Aggressor). Jede Sicht (5 min, 1 min, RangeXV) wird bei JEDEM
// Ereignis fortgeschrieben – unabhängig davon, welcher Chart im Browser offen ist.
//
// Reihenfolge je Ereignis k (verbindlich, für Schnelllauf, Replay und Rewind gleich):
//   1. Barabschluss erkennen: Eine Zeitbar [s, s+p) gilt als geschlossen, sobald das ERSTE Ereignis mit Marktzeit >= s+p
//      eintrifft (ClosedAtEvent = k). Vorher ist sie offen – ihre bisherigen Werte sind sichtbar, ihre endgültigen nie.
//      Eine RangeXV-Bar schließt, wenn Ereignis k ihre Spanne überschreiten würde; k eröffnet dann die neue Bar.
//   2. Ereignis k fließt in die (neue) offene Bar und in den Sessionkontext.
//   3. Die Strategie wertet den Zustand NACH Ereignis k aus.
//   4. Eine Order daraus wird frühestens beim Ereignis k+1 gefüllt (Engine: Fills vor Marktupdate des Folgeereignisses).
// Gleiche Zeitstempel: Dateireihenfolge = Ereignisposition (aus den Quelldaten). Andere Datenströme (BBO/DOM) sind hier
// nicht beteiligt; ihre relative Reihenfolge zu Trades wäre unbekannt und wird nicht behauptet.
// =====================================================================================================================

public enum BarStatus { Offen, Geschlossen }

/// <summary>Bar einer Sicht. Werte werden nur durch Ereignisse ≤ aktuellem Ereignis gebildet.</summary>
public sealed class FlowBar
{
    public required string View { get; init; }
    public int Index { get; init; }
    public DateTimeOffset Start { get; init; }
    /// <summary>Planmäßiges Ende (Zeitbar) bzw. null (RangeXV: Ende erst beim Abschluss bekannt).</summary>
    public DateTimeOffset? PlannedEnd { get; init; }
    public long FirstEvent { get; init; }
    public long LastEvent { get; internal set; }
    public DateTimeOffset LastTime { get; internal set; }
    public decimal Open { get; init; }
    public decimal High { get; internal set; }
    public decimal Low { get; internal set; }
    public decimal Close { get; internal set; }
    public decimal Volume { get; internal set; }
    public decimal BuyVolume { get; internal set; }
    public decimal SellVolume { get; internal set; }
    public decimal Delta => BuyVolume - SellVolume;
    public int Trades { get; internal set; }
    public BarStatus Status { get; internal set; } = BarStatus.Offen;
    /// <summary>Ereignis, mit dem der Abschluss bekannt wurde (bei Zeitbars das erste Ereignis der Folgebar).</summary>
    public long? ClosedAtEvent { get; internal set; }
    public DateTimeOffset? ClosedAtTime { get; internal set; }
    /// <summary>Footprint: Preis → (Bid = Verkäufer-Aggressor, Ask = Käufer-Aggressor).</summary>
    public SortedDictionary<decimal, (decimal Bid, decimal Ask)> Footprint { get; } = new();

    internal void Apply(long ev, MarketTick t)
    {
        LastEvent = ev; LastTime = t.Timestamp;
        if (t.Price > High) High = t.Price;
        if (t.Price < Low) Low = t.Price;
        Close = t.Price;
        Volume += t.Volume;
        Trades++;
        Footprint.TryGetValue(t.Price, out var f);
        if (t.Aggressor == AggressorSide.Buy) { BuyVolume += t.Volume; f.Ask += t.Volume; }
        else if (t.Aggressor == AggressorSide.Sell) { SellVolume += t.Volume; f.Bid += t.Volume; }
        Footprint[t.Price] = f;
    }

    public BarSnapshot Snapshot() => new(View, Index, Start, PlannedEnd, FirstEvent, LastEvent, Open, High, Low, Close, Volume, BuyVolume, SellVolume, Trades,
        Status == BarStatus.Offen ? "offen" : "geschlossen", ClosedAtEvent, ClosedAtTime);
}

/// <summary>Unveränderliche Kopie einer Bar (für Entscheidungsspur und Rewind).</summary>
public sealed record BarSnapshot(string View, int Index, DateTimeOffset Start, DateTimeOffset? PlannedEnd, long FirstEvent, long LastEvent,
    decimal Open, decimal High, decimal Low, decimal Close, decimal Volume, decimal BuyVolume, decimal SellVolume, int Trades, string Status,
    long? ClosedAtEvent, DateTimeOffset? ClosedAtTime)
{
    public decimal Delta => BuyVolume - SellVolume;
}

public abstract class BarBuilder
{
    protected readonly List<FlowBar> Bars = new();
    public string View { get; }
    public FlowBar? Current => Bars.Count > 0 && Bars[^1].Status == BarStatus.Offen ? Bars[^1] : null;
    public IReadOnlyList<FlowBar> All => Bars;
    /// <summary>Hat das zuletzt verarbeitete Ereignis eine Bar geschlossen?</summary>
    public bool ClosedOnLastEvent { get; private set; }
    public FlowBar? LastClosed { get; private set; }
    public int ClosedCount { get; private set; }

    protected BarBuilder(string view) => View = view;

    /// <summary>Verarbeitet Ereignis ev; liefert die dabei geschlossene Bar (oder null).</summary>
    public FlowBar? OnEvent(long ev, MarketTick t)
    {
        ClosedOnLastEvent = false;
        FlowBar? closed = null;
        var cur = Current;
        if (cur is not null && ShouldClose(cur, t))
        {
            cur.Status = BarStatus.Geschlossen;
            cur.ClosedAtEvent = ev;
            cur.ClosedAtTime = t.Timestamp;
            closed = cur; LastClosed = cur; ClosedCount++;
            ClosedOnLastEvent = true;
            cur = null;
        }
        cur ??= StartBar(ev, t);
        cur.Apply(ev, t);
        return closed;
    }

    /// <summary>Sessionende/-wechsel: offene Bar schließen (sie spannt nie über eine Pause).</summary>
    public void CloseOpenBar(long atEvent, DateTimeOffset at)
    {
        var cur = Current;
        if (cur is null) return;
        cur.Status = BarStatus.Geschlossen; cur.ClosedAtEvent = atEvent; cur.ClosedAtTime = at; LastClosed = cur; ClosedCount++;
    }

    protected abstract bool ShouldClose(FlowBar current, MarketTick t);
    protected abstract FlowBar NewBar(int index, long ev, MarketTick t);

    private FlowBar StartBar(long ev, MarketTick t)
    {
        var b = NewBar(Bars.Count, ev, t);
        Bars.Add(b);
        return b;
    }
}

/// <summary>Zeitbars [Start, Start+Periode) an UTC-Rastergrenzen (5 min, 1 min).</summary>
public sealed class TimeBarBuilder : BarBuilder
{
    private readonly TimeSpan _period;
    public TimeBarBuilder(string view, TimeSpan period) : base(view) => _period = period;

    protected override bool ShouldClose(FlowBar c, MarketTick t) => t.Timestamp >= c.PlannedEnd!.Value;

    protected override FlowBar NewBar(int index, long ev, MarketTick t)
    {
        long ticks = t.Timestamp.UtcTicks - t.Timestamp.UtcTicks % _period.Ticks;
        var start = new DateTimeOffset(ticks, TimeSpan.Zero);
        return new FlowBar { View = View, Index = index, Start = start, PlannedEnd = start + _period, FirstEvent = ev, Open = t.Price, High = t.Price, Low = t.Price, Close = t.Price };
    }
}

/// <summary>
/// VORLÄUFIGE Nachbildung des ATAS-Charttyps „rangeXV“ (dein MNQ-Chart: Period 4). ATAS-Gleichheit ist NICHT bestätigt.
/// Annahmen (dokumentiert, austauschbar): Spanne = Period × TickSize; eine Bar schließt, sobald ein Trade außerhalb von
/// [Low, Low+Spanne] bzw. [High−Spanne, High] liegen würde; dieser auslösende Trade eröffnet die neue Bar zu SEINEM Preis
/// (keine erfundenen Zwischenbars bei Preissprüngen); zum Sessionbeginn startet eine neue Bar.
/// </summary>
public sealed class RangeXvBarBuilder : BarBuilder
{
    public const string SemanticsNote = "vorläufig – rangeXV-Semantik nicht mit ATAS bestätigt (Annahme: Spanne = Period×TickSize, auslösender Trade eröffnet neue Bar zu seinem Preis, keine Zwischenbars)";
    private readonly decimal _range;
    public int Period { get; }
    public decimal TickSize { get; }

    public RangeXvBarBuilder(string view, int period, decimal tickSize) : base(view)
    {
        if (period <= 0 || tickSize <= 0) throw new ArgumentException("Period und TickSize müssen > 0 sein.");
        Period = period; TickSize = tickSize; _range = period * tickSize;
    }

    protected override bool ShouldClose(FlowBar c, MarketTick t) =>
        Math.Max(c.High, t.Price) - Math.Min(c.Low, t.Price) > _range;

    protected override FlowBar NewBar(int index, long ev, MarketTick t) =>
        new() { View = View, Index = index, Start = t.Timestamp, PlannedEnd = null, FirstEvent = ev, Open = t.Price, High = t.Price, Low = t.Price, Close = t.Price };
}
