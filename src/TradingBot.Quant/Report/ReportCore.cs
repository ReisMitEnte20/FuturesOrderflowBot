using System.Globalization;

namespace TradingBot.Quant.Report;

/// <summary>
/// Ein abgeschlossener (oder am Datenende bewerteter) Trade in einer vom Backtest-Motor unabhängigen Form.
/// Alle Geldwerte in Kontowährung. <see cref="NetPnL"/> enthält Gebühren; die Slippage steckt bereits in den
/// Fill-Preisen und damit in <see cref="GrossPnL"/> — <see cref="SlippageCost"/> ist nur der informative Betrag
/// dieser bereits enthaltenen Slippage und wird NIE zusätzlich abgezogen.
/// </summary>
public sealed record ReportTrade
{
    public int Index { get; init; }
    public DateTimeOffset EntryTime { get; init; }
    public DateTimeOffset ExitTime { get; init; }
    public bool IsLong { get; init; }
    public int Quantity { get; init; } = 1;
    public decimal EntryPrice { get; init; }
    public decimal ExitPrice { get; init; }
    public int EntryBarIndex { get; init; }
    public int ExitBarIndex { get; init; }
    public decimal GrossPnL { get; init; }
    public decimal Fees { get; init; }
    public decimal NetPnL { get; init; }
    /// <summary>In den Fill-Preisen enthaltene Slippage (Geldwert, informativ; nicht zusätzlich abziehen).</summary>
    public decimal SlippageCost { get; init; }
    /// <summary>Anzahl Market-Fills dieses Trades (Entry immer Market; Exit Market außer Limit-Take-Profit).</summary>
    public int MarketSides { get; init; } = 2;
    public string ExitReason { get; init; } = "";
    public bool Ambiguous { get; init; }
    /// <summary>Position war am Datenende noch offen und wurde zum letzten Schlusskurs BEWERTET (nicht realisiert).</summary>
    public bool ClosedAtDataEnd { get; init; }
    /// <summary>Handelstag (Session-Schlüssel, z. B. CME-Trade-Date "2026-06-15").</summary>
    public string SessionKey { get; init; } = "";

    public TimeSpan Holding => ExitTime - EntryTime;
    /// <summary>Ergebnis VOR allen Kosten (Gebühren + enthaltene Slippage).</summary>
    public decimal PreCostPnL => NetPnL + Fees + SlippageCost;
}

/// <summary>
/// Eine Kennzahl, die auch "nicht definiert" sein darf. Fehlt der Nenner, gibt es keine Verluste oder ist die Historie
/// zu kurz, ist <see cref="Value"/> null und <see cref="Reason"/> erklärt warum — niemals 0 oder "unendlich".
/// </summary>
public sealed record ReportValue(double? Value, string? Reason = null)
{
    public static ReportValue Of(double v) => double.IsFinite(v) ? new(v) : new(null, "Nicht endlich berechenbar.");
    public static ReportValue Of(decimal v) => new((double)v);
    public static ReportValue Missing(string reason) => new(null, reason);
    public bool HasValue => Value.HasValue;
}

/// <summary>
/// Handelstage für US-Index-Futures (CME Globex): eine Session beginnt 17:00 Chicago-Zeit des Vortags und endet
/// 16:00 Chicago-Zeit. Der Session-Schlüssel ist das CME-Trade-Date. Die Zuordnung hängt ausschließlich vom
/// Zeitstempel ab (nicht von Trades/Ergebnissen) und berücksichtigt Sommerzeit über die Zeitzonen-Datenbank.
/// </summary>
public static class TradeDateCalendar
{
    public static readonly TimeZoneInfo Chicago = Resolve("America/Chicago", "Central Standard Time");
    public static readonly TimeZoneInfo NewYork = Resolve("America/New_York", "Eastern Standard Time");

    private static TimeZoneInfo Resolve(string iana, string windows)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById(windows); }
    }

    /// <summary>CME-Trade-Date eines Zeitpunkts (ab 17:00 CT zählt der Folgetag).</summary>
    public static DateOnly TradeDate(DateTimeOffset utc)
    {
        var ct = TimeZoneInfo.ConvertTime(utc, Chicago);
        var d = DateOnly.FromDateTime(ct.DateTime);
        return ct.Hour >= 17 ? d.AddDays(1) : d;
    }

    public static string Key(DateTimeOffset utc) => TradeDate(utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Indizes der ersten Kerze jedes Handelstags (sortierte Kerzen-Öffnungszeiten vorausgesetzt).</summary>
    public static IReadOnlyList<int> SessionStartIndices(IReadOnlyList<DateTimeOffset> openTimes)
    {
        var list = new List<int>();
        DateOnly? prev = null;
        for (int i = 0; i < openTimes.Count; i++)
        {
            var d = TradeDate(openTimes[i]);
            if (prev != d) { list.Add(i); prev = d; }
        }
        return list;
    }

    /// <summary>Uhrzeit in New York (für Stunden-/Sessionteil-Auswertungen).</summary>
    public static DateTime ToNewYork(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, NewYork).DateTime;

    /// <summary>Sessionteil nach New-York-Zeit: regulärer Kassamarkt 09:30–16:00 ET, sonst elektronischer Handel.</summary>
    public static string SessionPart(DateTimeOffset utc)
    {
        var t = ToNewYork(utc).TimeOfDay;
        return t >= new TimeSpan(9, 30, 0) && t < new TimeSpan(16, 0, 0) ? "RTH 09:30–16:00 ET" : "ETH (außerhalb 09:30–16:00 ET)";
    }

    /// <summary>Tage im Zeitraum, an denen sich der UTC-Versatz von New York ändert (Sommerzeitwechsel).</summary>
    public static IReadOnlyList<(DateOnly Date, TimeSpan Before, TimeSpan After)> DstTransitions(DateTimeOffset from, DateTimeOffset to)
    {
        var res = new List<(DateOnly, TimeSpan, TimeSpan)>();
        if (to <= from) return res;
        var day = from.UtcDateTime.Date;
        var prev = NewYork.GetUtcOffset(day);
        for (var d = day.AddDays(1); d <= to.UtcDateTime.Date.AddDays(1); d = d.AddDays(1))
        {
            var off = NewYork.GetUtcOffset(d);
            if (off != prev) res.Add((DateOnly.FromDateTime(d.AddDays(-1)), prev, off));
            prev = off;
        }
        return res;
    }

    /// <summary>ISO-Kalenderwoche eines Handelstags als Schlüssel "2026-W24".</summary>
    public static string WeekKey(DateOnly d)
    {
        var dt = d.ToDateTime(TimeOnly.MinValue);
        return $"{ISOWeek.GetYear(dt)}-W{ISOWeek.GetWeekOfYear(dt):00}";
    }

    public static string MonthKey(DateOnly d) => d.ToString("yyyy-MM", CultureInfo.InvariantCulture);
}
