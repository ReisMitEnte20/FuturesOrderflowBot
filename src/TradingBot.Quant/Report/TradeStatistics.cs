namespace TradingBot.Quant.Report;

/// <summary>Ergebnisrechnung eines Laufs (Abschnitt 02). Alle Werte aus den tatsächlichen Trades.</summary>
public sealed record TradeStatisticsResult
{
    public int Trades { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public int Breakeven { get; init; }
    public int Longs { get; init; }
    public int Shorts { get; init; }

    /// <summary>Brutto = Kursergebnis aus den Fill-Preisen (Slippage ist darin bereits enthalten), vor Gebühren.</summary>
    public decimal GrossPnL { get; init; }
    public decimal Fees { get; init; }
    /// <summary>In den Fill-Preisen enthaltene Slippage (informativ — bereits in Brutto/Netto enthalten).</summary>
    public decimal ModeledSlippage { get; init; }
    public decimal NetPnL { get; init; }
    /// <summary>Ergebnis vor allen Kosten (Netto + Gebühren + enthaltene Slippage).</summary>
    public decimal PreCostPnL { get; init; }

    public ReportValue WinRate { get; init; } = ReportValue.Missing("Keine Trades.");
    public ReportValue AverageWin { get; init; } = ReportValue.Missing("Keine Gewinntrades.");
    public ReportValue AverageLoss { get; init; } = ReportValue.Missing("Keine Verlusttrades.");
    public ReportValue PayoffRatio { get; init; } = ReportValue.Missing("Nicht definiert.");
    public ReportValue Expectancy { get; init; } = ReportValue.Missing("Keine Trades.");
    public ReportValue ProfitFactor { get; init; } = ReportValue.Missing("Nicht definiert.");
    public ReportValue Exposure { get; init; } = ReportValue.Missing("Keine auswertbaren Kerzen.");
    public ReportValue AverageHoldingMinutes { get; init; } = ReportValue.Missing("Keine Trades.");
    public ReportValue MedianHoldingMinutes { get; init; } = ReportValue.Missing("Keine Trades.");
    public ReportValue LargestWin { get; init; } = ReportValue.Missing("Keine Trades.");
    public ReportValue LargestLoss { get; init; } = ReportValue.Missing("Keine Trades.");

    /// <summary>Realisiertes Netto (ohne den am Datenende nur bewerteten Trade).</summary>
    public decimal RealizedNetPnL { get; init; }
    /// <summary>Am Datenende noch offene Position: zum letzten Schlusskurs BEWERTET (unrealisiert). 0, wenn keine.</summary>
    public decimal UnrealizedAtEnd { get; init; }
    public int OpenAtEnd { get; init; }
    public int AmbiguousTrades { get; init; }
}

public static class TradeStatistics
{
    /// <summary>Berechnet die Ergebnisrechnung. <paramref name="evaluatedBars"/> = Zahl der ausgewerteten Kerzen
    /// (Exposure = Kerzen mit offener Position / ausgewertete Kerzen).</summary>
    public static TradeStatisticsResult Compute(IReadOnlyList<ReportTrade> trades, int evaluatedBars)
    {
        ArgumentNullException.ThrowIfNull(trades);
        int n = trades.Count;
        var wins = trades.Where(t => t.NetPnL > 0).ToList();
        var losses = trades.Where(t => t.NetPnL < 0).ToList();
        decimal gross = trades.Sum(t => t.GrossPnL), fees = trades.Sum(t => t.Fees), net = trades.Sum(t => t.NetPnL);
        decimal slip = trades.Sum(t => t.SlippageCost);
        var openAtEnd = trades.Where(t => t.ClosedAtDataEnd).ToList();

        ReportValue winRate = n == 0 ? ReportValue.Missing("Keine Trades.") : ReportValue.Of(wins.Count / (double)n);
        ReportValue avgWin = wins.Count == 0 ? ReportValue.Missing("Keine Gewinntrades.") : ReportValue.Of((double)wins.Average(t => t.NetPnL));
        ReportValue avgLoss = losses.Count == 0 ? ReportValue.Missing("Keine Verlusttrades.") : ReportValue.Of((double)losses.Average(t => t.NetPnL));
        ReportValue payoff = wins.Count == 0 || losses.Count == 0
            ? ReportValue.Missing(losses.Count == 0
                ? "Keine Verlusttrades — Payoff-Ratio nicht definiert (nicht „unendlich gut“)."
                : "Keine Gewinntrades — Payoff-Ratio nicht definiert.")
            : ReportValue.Of(avgWin.Value!.Value / Math.Abs(avgLoss.Value!.Value));
        decimal grossWin = wins.Sum(t => t.NetPnL), grossLoss = -losses.Sum(t => t.NetPnL);
        ReportValue pf = n == 0 ? ReportValue.Missing("Keine Trades.")
            : grossLoss == 0 ? ReportValue.Missing("Keine Verlusttrades — Profit Factor nicht definiert (kein Nenner).")
            : ReportValue.Of((double)(grossWin / grossLoss));

        int barsInMarket = trades.Sum(t => Math.Max(1, t.ExitBarIndex - t.EntryBarIndex + 1));
        ReportValue exposure = evaluatedBars <= 0 ? ReportValue.Missing("Keine auswertbaren Kerzen.")
            : ReportValue.Of(Math.Min(1.0, barsInMarket / (double)evaluatedBars));

        var holds = trades.Select(t => t.Holding.TotalMinutes).OrderBy(x => x).ToList();
        return new TradeStatisticsResult
        {
            Trades = n,
            Wins = wins.Count,
            Losses = losses.Count,
            Breakeven = n - wins.Count - losses.Count,
            Longs = trades.Count(t => t.IsLong),
            Shorts = trades.Count(t => !t.IsLong),
            GrossPnL = gross,
            Fees = fees,
            ModeledSlippage = slip,
            NetPnL = net,
            PreCostPnL = net + fees + slip,
            WinRate = winRate,
            AverageWin = avgWin,
            AverageLoss = avgLoss,
            PayoffRatio = payoff,
            Expectancy = n == 0 ? ReportValue.Missing("Keine Trades.") : ReportValue.Of((double)(net / n)),
            ProfitFactor = pf,
            Exposure = exposure,
            AverageHoldingMinutes = n == 0 ? ReportValue.Missing("Keine Trades.") : ReportValue.Of(holds.Average()),
            MedianHoldingMinutes = n == 0 ? ReportValue.Missing("Keine Trades.") : ReportValue.Of(Statistics.Stats.Median(holds)),
            LargestWin = n == 0 ? ReportValue.Missing("Keine Trades.") : ReportValue.Of((double)trades.Max(t => t.NetPnL)),
            LargestLoss = n == 0 ? ReportValue.Missing("Keine Trades.") : ReportValue.Of((double)trades.Min(t => t.NetPnL)),
            RealizedNetPnL = net - openAtEnd.Sum(t => t.NetPnL),
            UnrealizedAtEnd = openAtEnd.Sum(t => t.NetPnL),
            OpenAtEnd = openAtEnd.Count,
            AmbiguousTrades = trades.Count(t => t.Ambiguous)
        };
    }
}

/// <summary>Ein Punkt einer Kapitalkurve (Zeit, Kapital).</summary>
public sealed record EquitySample(DateTimeOffset Time, double Equity);

/// <summary>Drawdown-Auswertung einer Kapitalkurve (Abschnitt 03).</summary>
public sealed record DrawdownResult
{
    public string Basis { get; init; } = "";
    public double MaxDrawdown { get; init; }
    /// <summary>Maximaler Drawdown relativ zum jeweiligen Kapital-Hoch (inkl. Startkapital).</summary>
    public double MaxDrawdownFraction { get; init; }
    public DateTimeOffset? PeakTime { get; init; }
    public DateTimeOffset? TroughTime { get; init; }
    /// <summary>Zeitpunkt, an dem das Hoch vor dem maximalen Drawdown wieder erreicht wurde; null = offen.</summary>
    public DateTimeOffset? RecoveryTime { get; init; }
    public bool MaxDrawdownOpen { get; init; }
    /// <summary>Zeit vom Hoch bis zur Erholung des maximalen Drawdowns (bzw. bis Datenende, wenn offen).</summary>
    public double? RecoveryDays { get; init; }
    /// <summary>Längste Dauer unter einem früheren Kapital-Hoch (Tage), auch wenn nicht der tiefste Drawdown.</summary>
    public double LongestUnderwaterDays { get; init; }
    public bool LongestUnderwaterOpen { get; init; }
    public bool UnderwaterAtEnd { get; init; }
    public double CurrentDrawdown { get; init; }
    /// <summary>Drawdown-Verlauf (Kapital − laufendes Hoch, ≤ 0), ausgedünnt für die Darstellung.</summary>
    public IReadOnlyList<long> T { get; init; } = Array.Empty<long>();
    public IReadOnlyList<double> Equity { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> Drawdown { get; init; } = Array.Empty<double>();
}

public sealed record LosingStreakResult(int LongestCount, decimal LongestSum, DateTimeOffset? From, DateTimeOffset? To,
    int LongestSessionCount);

public static class DrawdownAnalysis
{
    /// <summary>Wertet eine Kapitalkurve aus. Der erste Punkt muss das Startkapital sein — so wird ein Verlust direkt
    /// nach dem Start korrekt als Drawdown vom Startkapital gezählt.</summary>
    public static DrawdownResult Analyze(IReadOnlyList<EquitySample> curve, string basis, int maxPoints = 1500)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (curve.Count == 0) return new DrawdownResult { Basis = basis };

        double peak = curve[0].Equity;
        DateTimeOffset peakTime = curve[0].Time;
        double maxDd = 0, maxFrac = 0;
        DateTimeOffset? mddPeak = null, mddTrough = null;
        double mddPeakLevel = peak;
        double longestUw = 0; bool longestOpen = false;
        DateTimeOffset? uwStart = null;
        var dd = new double[curve.Count];

        for (int i = 0; i < curve.Count; i++)
        {
            var p = curve[i];
            if (p.Equity >= peak)
            {
                if (uwStart is { } s)
                {
                    double days = (p.Time - s).TotalDays;
                    if (days > longestUw) { longestUw = days; longestOpen = false; }
                    uwStart = null;
                }
                peak = p.Equity; peakTime = p.Time;
            }
            else
            {
                uwStart ??= peakTime;
                double d = peak - p.Equity;
                if (d > maxDd)
                {
                    maxDd = d; mddPeak = peakTime; mddTrough = p.Time; mddPeakLevel = peak;
                }
                if (peak > 0) maxFrac = Math.Max(maxFrac, d / peak);
            }
            dd[i] = p.Equity - peak;
        }
        var end = curve[^1];
        bool uwAtEnd = uwStart is not null;
        if (uwStart is { } open)
        {
            double days = (end.Time - open).TotalDays;
            if (days > longestUw) { longestUw = days; longestOpen = true; }
        }

        DateTimeOffset? recovery = null;
        if (mddTrough is { } tr)
            for (int i = 0; i < curve.Count; i++)
                if (curve[i].Time > tr && curve[i].Equity >= mddPeakLevel) { recovery = curve[i].Time; break; }
        bool mddOpen = maxDd > 0 && recovery is null;
        double? recDays = maxDd > 0 && mddPeak is { } pk ? ((recovery ?? end.Time) - pk).TotalDays : null;

        // Ausdünnen (Extremwerte je Eimer erhalten), nur für die Darstellung.
        var (t, eq, ddv) = Thin(curve, dd, maxPoints);
        return new DrawdownResult
        {
            Basis = basis,
            MaxDrawdown = maxDd,
            MaxDrawdownFraction = maxFrac,
            PeakTime = mddPeak,
            TroughTime = mddTrough,
            RecoveryTime = recovery,
            MaxDrawdownOpen = mddOpen,
            RecoveryDays = recDays,
            LongestUnderwaterDays = longestUw,
            LongestUnderwaterOpen = longestOpen,
            UnderwaterAtEnd = uwAtEnd,
            CurrentDrawdown = -dd[^1],
            T = t, Equity = eq, Drawdown = ddv
        };
    }

    private static (List<long>, List<double>, List<double>) Thin(IReadOnlyList<EquitySample> curve, double[] dd, int maxPoints)
    {
        var t = new List<long>(); var eq = new List<double>(); var d = new List<double>();
        if (curve.Count <= maxPoints)
        {
            for (int i = 0; i < curve.Count; i++) { t.Add(curve[i].Time.ToUnixTimeMilliseconds()); eq.Add(curve[i].Equity); d.Add(dd[i]); }
            return (t, eq, d);
        }
        int bucket = (int)Math.Ceiling(curve.Count / (maxPoints / 2.0));
        for (int b = 0; b < curve.Count; b += bucket)
        {
            int e = Math.Min(curve.Count, b + bucket);
            int lo = b, hi = b;
            for (int i = b; i < e; i++) { if (dd[i] < dd[lo]) lo = i; if (curve[i].Equity > curve[hi].Equity) hi = i; }
            foreach (var i in lo <= hi ? new[] { lo, hi } : new[] { hi, lo })
            {
                if (t.Count > 0 && t[^1] == curve[i].Time.ToUnixTimeMilliseconds()) continue;
                t.Add(curve[i].Time.ToUnixTimeMilliseconds()); eq.Add(curve[i].Equity); d.Add(dd[i]);
            }
        }
        if (t[^1] != curve[^1].Time.ToUnixTimeMilliseconds())
        { t.Add(curve[^1].Time.ToUnixTimeMilliseconds()); eq.Add(curve[^1].Equity); d.Add(dd[^1]); }
        return (t, eq, d);
    }

    /// <summary>Kapitalkurve aus abgeschlossenen Trades: Startpunkt + ein Punkt je Trade-Exit.</summary>
    public static IReadOnlyList<EquitySample> ClosedTradeCurve(IReadOnlyList<ReportTrade> trades, double initialCapital, DateTimeOffset start)
    {
        var list = new List<EquitySample>(trades.Count + 1) { new(start, initialCapital) };
        double eq = initialCapital;
        foreach (var t in trades.OrderBy(t => t.ExitTime).ThenBy(t => t.Index))
        {
            eq += (double)t.NetPnL;
            list.Add(new EquitySample(t.ExitTime, eq));
        }
        return list;
    }

    /// <summary>Längste Serie aufeinanderfolgender Verlusttrades (Netto &lt; 0) und längste Serie verlustreicher Handelstage.</summary>
    public static LosingStreakResult LosingStreak(IReadOnlyList<ReportTrade> trades)
    {
        int best = 0, cur = 0; decimal bestSum = 0, curSum = 0;
        DateTimeOffset? bFrom = null, bTo = null, cFrom = null;
        foreach (var t in trades.OrderBy(t => t.ExitTime).ThenBy(t => t.Index))
        {
            if (t.NetPnL < 0)
            {
                if (cur == 0) { cFrom = t.EntryTime; curSum = 0; }
                cur++; curSum += t.NetPnL;
                if (cur > best) { best = cur; bestSum = curSum; bFrom = cFrom; bTo = t.ExitTime; }
            }
            else cur = 0;
        }
        int bestDays = 0, curDays = 0;
        foreach (var g in trades.GroupBy(t => t.SessionKey).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (g.Sum(t => t.NetPnL) < 0) { curDays++; bestDays = Math.Max(bestDays, curDays); }
            else curDays = 0;
        }
        return new LosingStreakResult(best, bestSum, bFrom, bTo, bestDays);
    }
}
