using TradingBot.Domain.Models;

namespace TradingBot.Quant.DataQuality;

public enum QuantIssueSeverity { Info = 0, Warning = 1, Error = 2 }

/// <summary>Ein Befund der Datenqualitätsprüfung. Beschreibt nur Beobachtetes — nichts wird ergänzt.</summary>
public sealed record QuantDataIssue
{
    public required QuantIssueSeverity Severity { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public int Count { get; init; }
    /// <summary>Bis zu drei Beispiel-Zeitpunkte, damit der Befund im Chart auffindbar ist.</summary>
    public IReadOnlyList<DateTimeOffset> Examples { get; init; } = Array.Empty<DateTimeOffset>();
}

/// <summary>Bericht der Datenqualitätsprüfung einer OHLC-Reihe.</summary>
public sealed record QuantDataQualityReport
{
    public required string Symbol { get; init; }
    public int TimeframeMinutes { get; init; }
    /// <summary>Zeitzone der Zeitstempel. Die Pipeline arbeitet durchgängig in UTC.</summary>
    public string Timezone { get; init; } = "UTC";

    public int BarCount { get; init; }
    public DateTimeOffset? First { get; init; }
    public DateTimeOffset? Last { get; init; }
    public double SpanDays { get; init; }

    /// <summary>Beobachtete Kalendertage mit mindestens einer Kerze (keine erfundenen Tage).</summary>
    public int ObservedDays { get; init; }
    /// <summary>Median der Bars pro beobachtetem Tag — Hinweis auf Sessionlänge/Abdeckung.</summary>
    public double MedianBarsPerDay { get; init; }

    public bool LeadingPartialExcluded { get; init; }
    public bool TrailingPartialExcluded { get; init; }

    public IReadOnlyList<QuantDataIssue> Issues { get; init; } = Array.Empty<QuantDataIssue>();

    public bool HasErrors => Issues.Any(i => i.Severity == QuantIssueSeverity.Error);
    public bool HasWarnings => Issues.Any(i => i.Severity == QuantIssueSeverity.Warning);
}

/// <summary>
/// Prüft eine OHLC-Reihe auf Lücken, Dubletten, Integritätsverstöße, Sessionstruktur und mögliche
/// Kontraktwechsel (Rolls).
///
/// Wichtig: Die Prüfung MELDET nur. Sie füllt keine Lücken, interpoliert nicht und erzeugt keine
/// Ersatzkerzen. Ein Roll-Verdacht ist ausdrücklich ein Verdacht aus der Preisstruktur, keine
/// bestätigte Kontraktinformation — die läge nur mit echten Kontraktmetadaten vor.
/// </summary>
public static class QuantDataQualityChecker
{
    /// <summary>Preissprung über Nacht, ab dem ein Roll-/Kontraktwechsel-Verdacht gemeldet wird (Vielfaches der Median-Bar-Range).</summary>
    public const double RollSuspicionRangeMultiple = 25.0;

    public static QuantDataQualityReport Check(
        IReadOnlyList<Candle> candles,
        string symbol,
        int timeframeMinutes,
        bool leadingPartialExcluded = false,
        bool trailingPartialExcluded = false)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var issues = new List<QuantDataIssue>();

        if (candles.Count == 0)
        {
            issues.Add(new QuantDataIssue
            {
                Severity = QuantIssueSeverity.Error, Code = "NO_DATA",
                Message = "Keine Kerzen vorhanden — keine Auswertung möglich."
            });
            return new QuantDataQualityReport
            {
                Symbol = symbol, TimeframeMinutes = timeframeMinutes, BarCount = 0, Issues = issues,
                LeadingPartialExcluded = leadingPartialExcluded, TrailingPartialExcluded = trailingPartialExcluded
            };
        }

        var expected = TimeSpan.FromMinutes(timeframeMinutes <= 0 ? 1 : timeframeMinutes);

        var dupes = new List<DateTimeOffset>();
        var unordered = new List<DateTimeOffset>();
        var integrity = new List<DateTimeOffset>();
        var zeroVolume = new List<DateTimeOffset>();
        var intradayGaps = new List<DateTimeOffset>();
        var weekendGaps = new List<DateTimeOffset>();
        var rollSuspects = new List<DateTimeOffset>();

        var ranges = new List<decimal>(candles.Count);
        foreach (var c in candles)
        {
            ranges.Add(c.High - c.Low);
            if (c.High < c.Open || c.High < c.Close || c.Low > c.Open || c.Low > c.Close || c.High < c.Low)
                integrity.Add(c.OpenTime);
            if (c.Volume <= 0) zeroVolume.Add(c.OpenTime);
        }
        decimal medianRange = Median(ranges);

        var dayCounts = new Dictionary<DateOnly, int>();
        foreach (var c in candles)
        {
            var d = DateOnly.FromDateTime(c.OpenTime.UtcDateTime);
            dayCounts[d] = dayCounts.TryGetValue(d, out var n) ? n + 1 : 1;
        }

        for (int i = 1; i < candles.Count; i++)
        {
            var prev = candles[i - 1];
            var cur = candles[i];

            if (cur.OpenTime == prev.OpenTime) { dupes.Add(cur.OpenTime); continue; }
            if (cur.OpenTime < prev.OpenTime) { unordered.Add(cur.OpenTime); continue; }

            var gap = cur.OpenTime - prev.OpenTime;
            if (gap > expected)
            {
                // Wochenend-/Feiertagslücke von einer Lücke innerhalb eines Handelstages unterscheiden.
                bool spansWeekend = SpansWeekend(prev.OpenTime, cur.OpenTime);
                if (spansWeekend) weekendGaps.Add(prev.CloseTime);
                else intradayGaps.Add(prev.CloseTime);

                // Roll-Verdacht: großer Preissprung über eine Lücke hinweg.
                if (medianRange > 0)
                {
                    double jump = (double)Math.Abs(cur.Open - prev.Close);
                    if (jump > RollSuspicionRangeMultiple * (double)medianRange) rollSuspects.Add(cur.OpenTime);
                }
            }
        }

        Add(issues, QuantIssueSeverity.Error, "DUPLICATE_TIMESTAMP",
            "Doppelte Bar-Zeitstempel gefunden.", dupes);
        Add(issues, QuantIssueSeverity.Error, "UNORDERED_TIMESTAMP",
            "Bars sind nicht chronologisch sortiert.", unordered);
        Add(issues, QuantIssueSeverity.Error, "OHLC_INTEGRITY",
            "High/Low umschließen Open/Close nicht.", integrity);
        Add(issues, QuantIssueSeverity.Warning, "INTRADAY_GAP",
            $"Lücken innerhalb eines Handelstages (> {expected.TotalMinutes:0.##} Min). Lücken werden NICHT gefüllt.", intradayGaps);
        Add(issues, QuantIssueSeverity.Info, "SESSION_GAP",
            "Lücken über Wochenenden/Handelspausen — erwartet, keine fehlenden Daten.", weekendGaps);
        Add(issues, QuantIssueSeverity.Warning, "ZERO_VOLUME_BAR",
            "Kerzen ohne Volumen (mögliche Aggregationsartefakte).", zeroVolume);
        Add(issues, QuantIssueSeverity.Warning, "ROLL_SUSPECTED",
            $"Preissprung über eine Lücke > {RollSuspicionRangeMultiple:0}× Median-Bar-Range — möglicher Kontraktwechsel (Roll). " +
            "VERDACHT aus der Preisstruktur; ohne echte Kontraktmetadaten nicht bestätigt.", rollSuspects);

        if (leadingPartialExcluded)
            issues.Add(new QuantDataIssue
            {
                Severity = QuantIssueSeverity.Info, Code = "PARTIAL_LEADING_EXCLUDED",
                Message = "Angeschnittene erste Kerze wurde ausgeschlossen."
            });
        if (trailingPartialExcluded)
            issues.Add(new QuantDataIssue
            {
                Severity = QuantIssueSeverity.Info, Code = "PARTIAL_TRAILING_EXCLUDED",
                Message = "Angeschnittene letzte Kerze wurde ausgeschlossen."
            });

        var perDay = dayCounts.Values.Select(v => (decimal)v).ToList();
        return new QuantDataQualityReport
        {
            Symbol = symbol,
            TimeframeMinutes = timeframeMinutes,
            BarCount = candles.Count,
            First = candles[0].OpenTime,
            Last = candles[^1].CloseTime,
            SpanDays = (candles[^1].CloseTime - candles[0].OpenTime).TotalDays,
            ObservedDays = dayCounts.Count,
            MedianBarsPerDay = (double)Median(perDay),
            LeadingPartialExcluded = leadingPartialExcluded,
            TrailingPartialExcluded = trailingPartialExcluded,
            Issues = issues
        };
    }

    private static void Add(List<QuantDataIssue> issues, QuantIssueSeverity sev, string code, string msg,
        List<DateTimeOffset> hits)
    {
        if (hits.Count == 0) return;
        issues.Add(new QuantDataIssue
        {
            Severity = sev, Code = code, Message = msg, Count = hits.Count,
            Examples = hits.Take(3).ToList()
        });
    }

    /// <summary>Liegt zwischen zwei Zeitpunkten ein Samstag oder Sonntag (UTC)?</summary>
    private static bool SpansWeekend(DateTimeOffset a, DateTimeOffset b)
    {
        for (var d = a.UtcDateTime.Date; d <= b.UtcDateTime.Date; d = d.AddDays(1))
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return true;
        return false;
    }

    private static decimal Median(List<decimal> values)
    {
        if (values.Count == 0) return 0m;
        var s = values.OrderBy(v => v).ToList();
        int m = s.Count / 2;
        return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2m;
    }
}
