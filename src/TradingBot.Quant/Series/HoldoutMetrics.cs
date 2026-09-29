namespace TradingBot.Quant.Series;

/// <summary>
/// Berechnet die realisierten Holdout-Kennzahlen so, dass das STARTKAPITAL erhalten bleibt: Die erste
/// (ggf. aggregierte) Periode wird gegen das Startkapital gemessen und der Drawdown startet am Startkapital.
/// Sonst ginge bei der Kombination aus Aggregation und fehlendem Startanker die erste Rendite verloren und
/// der Drawdown begänne erst beim ersten Holdout-Punkt.
/// </summary>
public static class HoldoutMetrics
{
    /// <param name="anchoredHoldoutCurve">
    /// Die auf den Holdout-Zeitraum eingeschränkte Kapitalkurve, deren <see cref="QuantEquityCurve.StartTime"/>
    /// VOR dem ersten Holdout-Punkt liegt (Zeitpunkt, an dem das Kapital noch dem Startkapital entspricht).
    /// Nur so misst <see cref="ReturnSeriesBuilder.ToReturnSeries"/> die erste Rendite gegen das Startkapital.
    /// </param>
    /// <param name="initialCapital">Startkapital des Holdouts (Peak-Anker für den absoluten Drawdown).</param>
    public static (ReturnSeries Series, double MaxDrawdownAbs, double NetProfit, double FinalEquity, bool Truncated)
        RealizedFromHoldout(QuantEquityCurve anchoredHoldoutCurve, double initialCapital)
    {
        ArgumentNullException.ThrowIfNull(anchoredHoldoutCurve);

        var build = ReturnSeriesBuilder.ToReturnSeries(anchoredHoldoutCurve, EquityBasis.Realized);
        var levels = build.Series.EquityLevels;

        double finalEquity = levels.Count > 0 ? levels[^1] : initialCapital;
        double netProfit = finalEquity - initialCapital;
        double maxDd = MaxDrawdownAbs(levels, startPeak: initialCapital);

        return (build.Series, maxDd, netProfit, finalEquity, build.Truncated);
    }

    /// <summary>Absoluter Drawdown (Peak − Tal), wobei der laufende Höchststand beim Startkapital beginnt.</summary>
    public static double MaxDrawdownAbs(IReadOnlyList<double> equity, double startPeak)
    {
        ArgumentNullException.ThrowIfNull(equity);
        double peak = startPeak, maxDd = 0;
        foreach (var e in equity)
        {
            if (e > peak) peak = e;
            maxDd = Math.Max(maxDd, peak - e);
        }
        return maxDd;
    }
}
