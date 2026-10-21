namespace TradingBot.Quant.Series;

/// <summary>
/// Gemeinsame Regel für die Tages-Auswertung der verketteten Out-of-Sample-Reihe: OOS-Punkte, die am SELBEN
/// UTC-Kalendertag ENDEN, werden zu EINER Tagesrendite multiplikativ verkettet ((1+r1)·(1+r2)-1), NICHT addiert.
/// Hintergrund: Die Walk-forward-Testabschnitte liefern je Fold eine Tagesreihe; beim Verketten über eine
/// Fold-Grenze fällt der Grenz-Kalendertag in ZWEI Teilintervalle (Ende des einen Folds + Anfang des nächsten).
/// Ohne Zusammenfassung zählte dieser Tag doppelt und Stichprobenzahl/Blocklänge/Bootstrap/Sharpe bezögen sich auf
/// Fold-Fragmente statt auf Kalendertage. Diese Regel wird im WalkForward-Schritt (QuantApiService) UND im
/// Mehrstrategie-Vergleich (ResearchComparisonBuilder) verwendet, damit Übersicht und Detail dieselbe Tagesdefinition
/// nutzen. Fehlende Tage/Lücken werden NICHT aufgefüllt (keine erfundenen Nullrenditen). Endkapital/Gesamtrendite
/// bleiben erhalten (reine Verkettung).
/// </summary>
public static class OosDailyMerge
{
    /// <summary>
    /// Fasst (Ende, Start, Rendite)-Punkte je UTC-Kalendertag (nach dem ENDzeitpunkt) zusammen. Der Endzeitpunkt des
    /// Tages ist der SPÄTESTE, der Startanker der FRÜHESTE; die Rendite ist das Produkt der (1+r) minus 1. Rückgabe
    /// chronologisch nach Tag geordnet.
    /// </summary>
    public static (List<long> Ends, List<long?> Starts, List<double> Returns) ByUtcDay(
        IReadOnlyList<long> endsMs, IReadOnlyList<long?>? startsMs, IReadOnlyList<double> returns)
    {
        ArgumentNullException.ThrowIfNull(endsMs);
        ArgumentNullException.ThrowIfNull(returns);

        var agg = new Dictionary<DateOnly, (long MaxEnd, long? MinStart, double Factor)>();
        int n = Math.Min(endsMs.Count, returns.Count);
        for (int i = 0; i < n; i++)
        {
            var day = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(endsMs[i]).UtcDateTime);
            long? start = startsMs is not null && i < startsMs.Count ? startsMs[i] : null;
            if (!agg.TryGetValue(day, out var cur))
                agg[day] = (endsMs[i], start, 1.0 + returns[i]);
            else
            {
                long? minStart = start is null ? cur.MinStart
                    : cur.MinStart is null ? start
                    : Math.Min(cur.MinStart.Value, start.Value);
                agg[day] = (Math.Max(cur.MaxEnd, endsMs[i]), minStart, cur.Factor * (1.0 + returns[i]));
            }
        }

        var ends = new List<long>(agg.Count);
        var starts = new List<long?>(agg.Count);
        var rets = new List<double>(agg.Count);
        foreach (var day in agg.Keys.OrderBy(d => d))
        {
            var v = agg[day];
            ends.Add(v.MaxEnd);
            starts.Add(v.MinStart);
            rets.Add(v.Factor - 1.0);
        }
        return (ends, starts, rets);
    }

    /// <summary>Maximaler Drawdown (positiver Bruchteil) aus einer Renditereihe (verkettete Equity ab 1,0).</summary>
    public static double MaxDrawdownFraction(IReadOnlyList<double> returns)
    {
        double eq = 1.0, peak = 1.0, mdd = 0.0;
        foreach (var r in returns)
        {
            eq *= 1.0 + r;
            if (eq > peak) peak = eq;
            if (peak > 0) mdd = Math.Min(mdd, eq / peak - 1.0);
        }
        return -mdd;
    }
}
