using TradingBot.Backtesting;

namespace TradingBot.Research.Statistics;

/// <summary>
/// Reine Equity-/Verteilungsstatistik für den Research-Layer. Deterministisch.
/// Drawdown nutzt dieselbe Definition wie der Backtest (<see cref="BacktestStatisticsCalculator.MaxDrawdown"/>).
/// </summary>
public static class EquityStatistics
{
    /// <summary>Maximaler Drawdown der realisierten Equity-Kurve (kumulativer NetPnL), ≥ 0.</summary>
    public static decimal MaxDrawdown(IReadOnlyList<decimal> netPnls)
        => BacktestStatisticsCalculator.MaxDrawdown(netPnls);

    /// <summary>Endwert der Equity-Kurve = Summe aller NetPnLs.</summary>
    public static decimal FinalEquity(IReadOnlyList<decimal> netPnls)
    {
        decimal sum = 0m;
        foreach (var p in netPnls) sum += p;
        return sum;
    }

    /// <summary>Tiefster Punkt der Equity-Kurve (für Ruin-Betrachtungen), ≤ 0 möglich.</summary>
    public static decimal MinEquity(IReadOnlyList<decimal> netPnls)
    {
        decimal running = 0m, min = 0m;
        foreach (var p in netPnls)
        {
            running += p;
            if (running < min) min = running;
        }
        return min;
    }

    /// <summary>
    /// Perzentil per linearer Interpolation zwischen den nächstliegenden Rängen (0-basiert),
    /// äquivalent zu numpy 'linear'. <paramref name="p"/> in [0,1]. Eingabe muss nicht sortiert sein.
    /// </summary>
    public static decimal Percentile(IReadOnlyList<decimal> values, decimal p)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) throw new ArgumentException("Leere Wertemenge.", nameof(values));
        if (p < 0m || p > 1m) throw new ArgumentOutOfRangeException(nameof(p), "p muss in [0,1] liegen.");
        if (values.Count == 1) return values[0];

        var sorted = values.OrderBy(v => v).ToList();
        decimal rank = p * (sorted.Count - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];
        decimal weight = rank - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * weight;
    }

    public static decimal Median(IReadOnlyList<decimal> values) => Percentile(values, 0.5m);

    public static decimal Mean(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return 0m;
        decimal sum = 0m;
        foreach (var v in values) sum += v;
        return sum / values.Count;
    }
}
