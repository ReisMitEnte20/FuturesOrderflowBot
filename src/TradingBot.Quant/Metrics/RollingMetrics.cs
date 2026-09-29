using TradingBot.Quant.Series;
using TradingBot.Quant.Statistics;

namespace TradingBot.Quant.Metrics;

/// <summary>Ein Punkt der rollierenden Kennzahlen. Null = im Fenster nicht berechenbar.</summary>
public sealed record RollingPoint(DateTimeOffset Time, double? Sharpe, double? Volatility, double? Return);

/// <summary>Rendite einer Kalenderperiode (Monat/Jahr) aus zusammengesetzten Periodenrenditen.</summary>
public sealed record PeriodReturn(string Period, DateTimeOffset Start, double Return, int Observations);

/// <summary>
/// Rollierende Kennzahlen und Kalenderaggregate. Beides dient der Frage, ob ein Ergebnis über die
/// Zeit trägt oder von einzelnen Abschnitten getragen wird.
///
/// Es wird ausschließlich über tatsächlich beobachtete Perioden aggregiert; Lücken im Kalender
/// werden nicht gefüllt.
/// </summary>
public static class RollingMetrics
{
    /// <summary>
    /// Rollierendes Fenster fester Länge (in Perioden). Der erste Punkt liegt am Ende des ersten
    /// vollständigen Fensters — davor gibt es keine Kennzahl, und es wird keine geschätzt.
    /// </summary>
    public static IReadOnlyList<RollingPoint> Compute(ReturnSeries series, int window, double? periodsPerYear)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (window < 2) throw new ArgumentOutOfRangeException(nameof(window), "Fenster muss mindestens 2 Perioden umfassen.");

        var points = new List<RollingPoint>();
        if (series.Count < window) return points;

        double ann = periodsPerYear is > 0 ? Math.Sqrt(periodsPerYear.Value) : 1.0;
        bool annualize = periodsPerYear is > 0;

        for (int end = window; end <= series.Count; end++)
        {
            var slice = new double[window];
            for (int i = 0; i < window; i++) slice[i] = series.Returns[end - window + i];

            var sd = Stats.StdDevSample(slice);
            double mean = Stats.Mean(slice);
            bool degenerate = sd is null || Stats.IsNegligibleDeviation(sd.Value, Stats.MaxAbs(slice));

            double compounded = 1.0;
            foreach (var r in slice) compounded *= 1.0 + r;

            points.Add(new RollingPoint(
                series.Timestamps[end - 1],
                degenerate || !annualize ? null : mean / sd!.Value * ann,
                sd is null ? null : annualize ? sd.Value * ann : sd.Value,
                compounded - 1.0));
        }
        return points;
    }

    /// <summary>Monatsrenditen: geometrische Verkettung der Periodenrenditen je beobachtetem Kalendermonat (UTC).</summary>
    public static IReadOnlyList<PeriodReturn> Monthly(ReturnSeries series) => Aggregate(series, ReturnFrequency.Monthly);

    /// <summary>Jahresrenditen je beobachtetem Kalenderjahr (UTC).</summary>
    public static IReadOnlyList<PeriodReturn> Yearly(ReturnSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);
        return Group(series, t => t.ToUniversalTime().Year.ToString("D4"));
    }

    private static IReadOnlyList<PeriodReturn> Aggregate(ReturnSeries series, ReturnFrequency frequency)
    {
        ArgumentNullException.ThrowIfNull(series);
        return Group(series, t => ReturnSeriesBuilder.PeriodKey(t, frequency));
    }

    private static IReadOnlyList<PeriodReturn> Group(ReturnSeries series, Func<DateTimeOffset, string> key)
    {
        var result = new List<PeriodReturn>();
        string? current = null;
        double compounded = 1.0;
        int count = 0;
        DateTimeOffset start = default;

        for (int i = 0; i < series.Count; i++)
        {
            string k = key(series.Timestamps[i]);
            if (current is null) { current = k; start = series.Timestamps[i]; }
            else if (k != current)
            {
                result.Add(new PeriodReturn(current, start, compounded - 1.0, count));
                current = k; start = series.Timestamps[i]; compounded = 1.0; count = 0;
            }
            compounded *= 1.0 + series.Returns[i];
            count++;
        }
        if (current is not null) result.Add(new PeriodReturn(current, start, compounded - 1.0, count));
        return result;
    }

    /// <summary>
    /// Unterwasserkurve: relativer Abstand zum bisherigen Höchststand je Periode (≤ 0).
    /// Grundlage der Drawdown-Darstellung im Dashboard.
    /// </summary>
    public static IReadOnlyList<double> Underwater(IReadOnlyList<double> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        var outp = new List<double>(levels.Count);
        double peak = levels.Count > 0 ? levels[0] : 0;
        foreach (var l in levels)
        {
            if (l > peak) peak = l;
            outp.Add(peak > 0 ? l / peak - 1.0 : 0.0);
        }
        return outp;
    }
}
