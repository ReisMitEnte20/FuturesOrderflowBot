using TradingBot.Backtesting.Ohlc;
using TradingBot.Quant.Series;

namespace TradingBot.Quant.Metrics;

/// <summary>
/// Kennzahlen zur Handelsaktivität: Marktpräsenz (Exposure), Umschlag (Turnover) und
/// Gewinnkonzentration. Diese Größen erklären, WIE ein Ergebnis zustande kam, und decken auf,
/// wenn es an wenigen Trades hängt.
/// </summary>
public static class TradeActivityMetricsCalculator
{
    public static IReadOnlyList<QuantMetric> Compute(
        IReadOnlyList<OhlcBacktestTrade> trades,
        QuantEquityCurve curve,
        QuantContractSpec spec)
    {
        ArgumentNullException.ThrowIfNull(trades);
        ArgumentNullException.ThrowIfNull(curve);
        ArgumentNullException.ThrowIfNull(spec);

        var metrics = new List<QuantMetric>();
        int n = curve.Points.Count;
        string inputs = $"{trades.Count} Trades, {n} Perioden ({PerformanceMetricsCalculator.FrequencyLabel(curve.Frequency)}).";

        // --- Exposure ---
        if (n == 0)
            metrics.Add(QuantMetric.Unavailable("exposure", "Marktpräsenz", "fraction",
                "Anteil der Perioden mit offener Position", inputs, "Keine Perioden vorhanden."));
        else
        {
            int open = curve.Points.Count(p => p.OpenQuantity != 0);
            metrics.Add(new QuantMetric
            {
                Key = "exposure", Label = "Marktpräsenz", Unit = "fraction", Value = (double)open / n,
                Method = "Perioden mit offener Position / alle Perioden (Stand jeweils zum Periodenende)",
                Inputs = inputs, SampleSize = n,
                Limitation = "Auf Periodenenden gemessen; Positionen, die innerhalb einer Periode geöffnet und geschlossen werden, zählen nicht mit."
            });
        }

        // --- Turnover ---
        double years = curve.Start is { } s && curve.End is { } e ? (e - s).TotalDays / 365.25 : 0;
        double meanEquity = n == 0 ? 0 : curve.Points.Average(p => (double)p.TotalEquity);
        double notional = 0;
        foreach (var t in trades)
            notional += ((double)t.EntryPrice + (double)t.ExitPrice) * (double)spec.PointValue * t.Quantity;

        if (trades.Count == 0)
            metrics.Add(QuantMetric.Unavailable("turnover_annual", "Umschlag p. a.", "ratio",
                "gehandeltes Nominal / mittleres Kapital / Jahre", inputs, "Keine Trades."));
        else if (meanEquity <= 0 || years <= 0)
            metrics.Add(QuantMetric.Unavailable("turnover_annual", "Umschlag p. a.", "ratio",
                "gehandeltes Nominal / mittleres Kapital / Jahre", inputs,
                meanEquity <= 0 ? "Mittleres Kapital ≤ 0." : "Überspannte Zeit = 0."));
        else
            metrics.Add(new QuantMetric
            {
                Key = "turnover_annual", Label = "Umschlag p. a.", Unit = "ratio",
                Value = notional / meanEquity / years,
                Method = "Σ((Entry+Exit) × PointValue × Kontrakte) / mittleres Gesamtkapital / Jahre",
                Inputs = inputs + $" Nominal {notional:0.##}, mittleres Kapital {meanEquity:0.##}, {years:0.###} Jahre.",
                SampleSize = trades.Count,
                Limitation = "Futures werden auf Margin gehandelt: das Nominal übersteigt das eingesetzte Kapital deutlich, " +
                             "der Umschlag ist daher nicht mit dem einer voll finanzierten Aktienstrategie vergleichbar."
            });

        // --- Gewinnkonzentration ---
        var wins = trades.Where(t => t.NetPnL > 0).Select(t => (double)t.NetPnL).OrderByDescending(v => v).ToList();
        double totalWin = wins.Sum();
        if (wins.Count == 0 || totalWin <= 0)
        {
            metrics.Add(QuantMetric.Unavailable("gain_concentration_top5", "Gewinnanteil der 5 besten Trades", "fraction",
                "Summe der 5 größten Gewinne / Summe aller Gewinne", inputs, "Keine Gewinntrades vorhanden.", trades.Count));
            metrics.Add(QuantMetric.Unavailable("gain_herfindahl", "Gewinnkonzentration (Herfindahl)", "ratio",
                "Σ (Gewinnanteil_i)² über alle Gewinntrades", inputs, "Keine Gewinntrades vorhanden.", trades.Count));
        }
        else
        {
            metrics.Add(new QuantMetric
            {
                Key = "gain_concentration_top5", Label = "Gewinnanteil der 5 besten Trades", Unit = "fraction",
                Value = wins.Take(5).Sum() / totalWin,
                Method = "Summe der 5 größten Gewinne / Summe aller Gewinne",
                Inputs = inputs + $" {wins.Count} Gewinntrades.", SampleSize = wins.Count,
                Limitation = "Werte nahe 1 bedeuten: das Ergebnis hängt an sehr wenigen Trades und ist entsprechend zufallsanfällig."
            });
            double hhi = wins.Sum(w => (w / totalWin) * (w / totalWin));
            metrics.Add(new QuantMetric
            {
                Key = "gain_herfindahl", Label = "Gewinnkonzentration (Herfindahl)", Unit = "ratio", Value = hhi,
                Method = "Σ (Gewinn_i / Σ Gewinne)²; 1/k bei k gleich großen Gewinnen, 1 bei einem einzigen",
                Inputs = inputs, SampleSize = wins.Count,
                Limitation = "Beschreibt nur die Verteilung der Gewinne, nicht deren Ursache."
            });
        }

        metrics.Add(new QuantMetric
        {
            Key = "trade_count", Label = "Anzahl Trades", Unit = "count", Value = trades.Count,
            Method = "abgeschlossene Round-Turn-Trades", Inputs = inputs, SampleSize = trades.Count,
            Limitation = trades.Count < 30 ? "Unter 30 Trades sind trade-basierte Aussagen statistisch schwach." : null
        });

        return metrics;
    }
}
