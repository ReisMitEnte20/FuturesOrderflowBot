namespace TradingBot.Quant.Series;

/// <summary>
/// Berechnet die realisierten Holdout-Kennzahlen so, dass das STARTKAPITAL erhalten bleibt: Die erste
/// (ggf. aggregierte) Periode wird gegen das Startkapital gemessen und der Drawdown startet am Startkapital.
/// Sonst ginge bei der Kombination aus Aggregation und fehlendem Startanker die erste Rendite verloren und
/// der Drawdown begänne erst beim ersten Holdout-Punkt.
///
/// Zwei getrennte Auswertungsgrenzen: Die renditebasierten (Prozent-)Kennzahlen stehen auf der
/// Renditereihe, die bei Kapital ≤ 0 ABBRICHT (<c>ReturnsTruncated</c>). Die ABSOLUTEN Ergebniszahlen
/// (End-Equity, Netto-PnL, absoluter Drawdown) werden dagegen IMMER aus der VOLLSTÄNDIGEN Holdout-Equity
/// bestimmt — genau wie Chart und Journal —, damit die Zusammenfassung nicht von einem verkürzten,
/// vom dargestellten Lauf abweichenden Ausschnitt stammt.
/// </summary>
public static class HoldoutMetrics
{
    /// <param name="anchoredHoldoutCurve">
    /// Die auf den Holdout-Zeitraum eingeschränkte Kapitalkurve, deren <see cref="QuantEquityCurve.StartTime"/>
    /// VOR dem ersten Holdout-Punkt liegt (Zeitpunkt, an dem das Kapital noch dem Startkapital entspricht).
    /// Nur so misst <see cref="ReturnSeriesBuilder.ToReturnSeries"/> die erste Rendite gegen das Startkapital.
    /// Die <see cref="QuantEquityCurve.Points"/> tragen den VOLLSTÄNDIGEN Lauf (auch nach Kapital ≤ 0).
    /// </param>
    /// <param name="initialCapital">Startkapital des Holdouts (Peak-Anker für den absoluten Drawdown).</param>
    /// <returns>
    /// <c>Series</c> = Renditereihe (kann verkürzt sein), <c>MaxDrawdownAbs</c>/<c>NetProfit</c>/<c>FinalEquity</c>
    /// aus dem vollständigen Lauf, <c>ReturnsTruncated</c> = die PROZENTKENNZAHLEN wurden wegen Kapital ≤ 0
    /// abgebrochen und gelten nur bis dahin.
    /// </returns>
    public static (ReturnSeries Series, double MaxDrawdownAbs, double NetProfit, double FinalEquity, bool ReturnsTruncated)
        RealizedFromHoldout(QuantEquityCurve anchoredHoldoutCurve, double initialCapital)
    {
        ArgumentNullException.ThrowIfNull(anchoredHoldoutCurve);

        // Renditebasierte (Prozent-)Kennzahlen: die Reihe kann bei Kapital ≤ 0 ABBRECHEN (ReturnsTruncated).
        var build = ReturnSeriesBuilder.ToReturnSeries(anchoredHoldoutCurve, EquityBasis.Realized);

        // Absolute Ergebniszahlen IMMER aus der VOLLSTÄNDIGEN Holdout-Equity — dieselbe Auswertungsgrenze
        // wie Chart und Journal. Nicht aus der (bei Kapital ≤ 0 verkürzten) Renditereihe ableiten.
        var fullLevels = new List<double>(anchoredHoldoutCurve.Points.Count);
        foreach (var p in anchoredHoldoutCurve.Points)
            fullLevels.Add((double)p.RealizedEquity);

        double finalEquity = fullLevels.Count > 0 ? fullLevels[^1] : initialCapital;
        double netProfit = finalEquity - initialCapital;
        double maxDd = MaxDrawdownAbs(fullLevels, startPeak: initialCapital);

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
