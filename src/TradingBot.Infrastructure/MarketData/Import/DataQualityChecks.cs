using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.MarketData.Import;

/// <summary>
/// Zusätzliche Qualitätsprüfungen NACH dem Import: Abgleich gegen ein InstrumentProfile
/// (Symbol, Tick-Ausrichtung, Session-Plausibilität) und Lücken-Erkennung.
/// Reine Prüfungen – die Daten werden nie verändert.
/// </summary>
public static class DataQualityChecks
{
    /// <summary>
    /// Prüft einen importierten Datensatz gegen das InstrumentProfile:
    /// Symbol-Match (Error), Preise auf TickSize ausgerichtet (Warning),
    /// Zeitstempel innerhalb der Handelssession (Info).
    /// </summary>
    public static IReadOnlyList<DataQualityIssue> CheckAgainstInstrument(
        ImportedMarketDataSet data, InstrumentProfile instrument)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(instrument);

        var issues = new List<DataQualityIssue>();

        if (!string.IsNullOrEmpty(data.Symbol)
            && !string.Equals(data.Symbol, instrument.Symbol, StringComparison.OrdinalIgnoreCase))
            issues.Add(new DataQualityIssue
            {
                Severity = DataQualitySeverity.Error,
                Code = "SymbolMismatch",
                Message = $"Datensatz-Symbol '{data.Symbol}' passt nicht zum InstrumentProfile '{instrument.Symbol}'."
            });

        if (instrument.TickSize > 0m)
        {
            int misaligned = CollectPrices(data).Count(p => p % instrument.TickSize != 0m);
            if (misaligned > 0)
                issues.Add(new DataQualityIssue
                {
                    Severity = DataQualitySeverity.Warning,
                    Code = "PriceNotTickAligned",
                    Message = $"{misaligned} Preis(e) sind nicht auf TickSize {instrument.TickSize} ausgerichtet."
                });
        }

        int outsideSession = CollectTimestamps(data).Count(ts => !IsWithinSession(ts, instrument));
        if (outsideSession > 0)
            issues.Add(new DataQualityIssue
            {
                Severity = DataQualitySeverity.Info,
                Code = "OutsideSession",
                Message = $"{outsideSession} Datenpunkt(e) außerhalb der Session " +
                          $"{instrument.SessionStart}-{instrument.SessionEnd} ({instrument.TradingTimezone})."
            });

        return issues;
    }

    /// <summary>
    /// Erkennt auffällige Zeitlücken: Abstand &gt; <paramref name="gapFactor"/> × Median-Abstand → Warning.
    /// </summary>
    public static IReadOnlyList<DataQualityIssue> CheckGaps(
        IReadOnlyList<DateTimeOffset> timestamps, decimal gapFactor = 10m)
    {
        ArgumentNullException.ThrowIfNull(timestamps);
        var issues = new List<DataQualityIssue>();
        if (timestamps.Count < 3) return issues;

        var gaps = new List<long>(timestamps.Count - 1);
        for (int i = 1; i < timestamps.Count; i++)
            gaps.Add((timestamps[i] - timestamps[i - 1]).Ticks);

        var sorted = gaps.OrderBy(g => g).ToList();
        long median = sorted[sorted.Count / 2];
        if (median <= 0) return issues;

        for (int i = 0; i < gaps.Count; i++)
            if (gaps[i] > (long)(median * gapFactor))
                issues.Add(new DataQualityIssue
                {
                    Severity = DataQualitySeverity.Warning,
                    Code = "DataGap",
                    Message = $"Lücke von {TimeSpan.FromTicks(gaps[i])} nach {timestamps[i]:O} " +
                              $"(> {gapFactor}× Median {TimeSpan.FromTicks(median)}).",
                    Timestamp = timestamps[i]
                });

        return issues;
    }

    private static IEnumerable<decimal> CollectPrices(ImportedMarketDataSet data)
    {
        foreach (var t in data.Ticks) yield return t.Price;
        foreach (var b in data.OrderFlowBars)
        {
            yield return b.Open; yield return b.High; yield return b.Low; yield return b.Close;
        }
        foreach (var f in data.FootprintBars)
            foreach (var l in f.Levels) yield return l.PriceLevel;
        foreach (var v in data.VolumeProfiles)
            foreach (var l in v.Levels) yield return l.PriceLevel;
    }

    private static IEnumerable<DateTimeOffset> CollectTimestamps(ImportedMarketDataSet data)
    {
        foreach (var t in data.Ticks) yield return t.Timestamp;
        foreach (var b in data.OrderFlowBars) yield return b.OpenTime;
        foreach (var f in data.FootprintBars) yield return f.OpenTime;
    }

    private static bool IsWithinSession(DateTimeOffset ts, InstrumentProfile instrument)
    {
        try
        {
            var tz = string.IsNullOrWhiteSpace(instrument.TradingTimezone)
                || instrument.TradingTimezone.Equals("UTC", StringComparison.OrdinalIgnoreCase)
                ? TimeZoneInfo.Utc
                : TimeZoneInfo.FindSystemTimeZoneById(instrument.TradingTimezone);
            var local = TimeZoneInfo.ConvertTime(ts, tz);
            var t = TimeOnly.FromTimeSpan(local.TimeOfDay);
            return t >= instrument.SessionStart && t < instrument.SessionEnd;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false; // fail-closed: unbekannte Zeitzone -> als außerhalb gewertet
        }
    }
}
