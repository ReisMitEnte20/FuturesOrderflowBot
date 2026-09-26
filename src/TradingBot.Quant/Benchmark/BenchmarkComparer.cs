using TradingBot.Quant.Metrics;
using TradingBot.Quant.Series;
using TradingBot.Quant.Statistics;

namespace TradingBot.Quant.Benchmark;

/// <summary>Angaben, ohne die ein Benchmarkvergleich nicht redlich ist.</summary>
public sealed record BenchmarkComparisonOptions
{
    public AnnualizationBasis AnnualizationBasis { get; init; } = AnnualizationBasis.Observed;
    public double? FixedPeriodsPerYear { get; init; }
    public double RiskFreeAnnualRate { get; init; }

    /// <summary>
    /// Bestätigung, dass die Strategie-Equity ein VOLL FINANZIERTES Konto abbildet (Startkapital als
    /// Gesamtkapital, nicht nur die hinterlegte Margin). Nur dann ist die Renditebasis mit einem
    /// Index vergleichbar. Ohne Bestätigung wird der Vergleich mit deutlicher Warnung ausgegeben.
    /// </summary>
    public bool StrategyIsFullyFunded { get; init; }

    /// <summary>Beschreibung der Kapitalbasis der Strategie (erscheint im Bericht).</summary>
    public string StrategyCapitalBasisNote { get; init; } =
        "Kapitalbasis = Startkapital des Backtests zuzüglich kumulierter Netto-PnL.";
}

/// <summary>Ergebnis des Benchmarkvergleichs inklusive Annahmen und Unsicherheit.</summary>
public sealed record BenchmarkComparisonResult
{
    public required string StrategyName { get; init; }
    public required string BenchmarkName { get; init; }
    public required string BenchmarkProvenance { get; init; }

    /// <summary>Anzahl gemeinsamer Perioden nach zeitlicher Ausrichtung.</summary>
    public int CommonPeriods { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public double? PeriodsPerYear { get; init; }

    public IReadOnlyList<QuantMetric> Metrics { get; init; } = Array.Empty<QuantMetric>();

    /// <summary>Renditereihen nach der Ausrichtung — Grundlage der Diagramme.</summary>
    public ReturnSeries AlignedStrategy { get; init; } = ReturnSeries.Empty;
    public ReturnSeries AlignedBenchmark { get; init; } = ReturnSeries.Empty;

    /// <summary>Annahmen und Warnungen, die im Bericht sichtbar sein müssen.</summary>
    public IReadOnlyList<string> Assumptions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Kein Vergleich möglich (keine Daten / keine Überschneidung). Grund in <see cref="UnavailableReason"/>.</summary>
    public bool Available { get; init; }
    public string? UnavailableReason { get; init; }
}

/// <summary>
/// Vergleicht eine Strategie-Renditereihe mit einer Benchmark-Renditereihe.
///
/// Es wird ausschließlich auf GEMEINSAMEN Zeitstempeln gerechnet; fehlende Benchmarkwerte werden
/// weder fortgeschrieben noch interpoliert. Liegen keine echten Benchmarkdaten vor, gibt es kein
/// Ergebnis — und ausdrücklich keine erfundene Vergleichskurve.
///
/// Beta/Alpha stammen aus der Kleinste-Quadrate-Regression der Überschussrenditen
/// (r_p − r_f) = α + β · (r_b − r_f) + ε. Standardfehler und t-Werte werden mitgeliefert, weil
/// ein Alpha ohne Streuungsangabe keine belastbare Aussage ist.
/// </summary>
public static class BenchmarkComparer
{
    /// <summary>Wandelt eine Benchmark-Kursreihe in eine Renditereihe der gewünschten Frequenz um.</summary>
    public static ReturnSeries ToReturnSeries(BenchmarkSeries benchmark, ReturnFrequency frequency)
    {
        ArgumentNullException.ThrowIfNull(benchmark);

        // Letzte Beobachtung je beobachteter Kalenderperiode — keine erfundenen Perioden.
        var points = new List<BenchmarkObservation>();
        string? key = null;
        foreach (var o in benchmark.Observations)
        {
            string k = ReturnSeriesBuilder.PeriodKey(o.Time, frequency);
            if (key is not null && k == key) points[^1] = o;
            else { points.Add(o); key = k; }
        }

        var times = new List<DateTimeOffset>();
        var rets = new List<double>();
        var levels = new List<double>();
        for (int i = 1; i < points.Count; i++)
        {
            if (points[i - 1].Level <= 0) continue;
            times.Add(points[i].Time);
            rets.Add(points[i].Level / points[i - 1].Level - 1.0);
            levels.Add(points[i].Level);
        }

        return new ReturnSeries
        {
            Name = benchmark.Name,
            Timestamps = times,
            Returns = rets,
            EquityLevels = levels,
            InitialCapital = points.Count > 0 ? points[0].Level : 0,
            Basis = EquityBasis.Total,
            Frequency = frequency,
            Currency = benchmark.Currency
        };
    }

    public static BenchmarkComparisonResult Compare(
        ReturnSeries strategy,
        BenchmarkSeries? benchmark,
        BenchmarkComparisonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        var o = options ?? new BenchmarkComparisonOptions();

        if (benchmark is null || benchmark.Count < 2)
            return new BenchmarkComparisonResult
            {
                StrategyName = strategy.Name,
                BenchmarkName = benchmark?.Name ?? "(keine Benchmarkdaten)",
                BenchmarkProvenance = benchmark?.Provenance ?? "—",
                Available = false,
                UnavailableReason = "Keine echten Benchmarkdaten hinterlegt. Es wird bewusst keine Vergleichskurve erzeugt."
            };

        var benchSeries = ToReturnSeries(benchmark, strategy.Frequency);
        var aligned = ReturnSeriesBuilder.AlignOnCommonTimestamps(new[] { strategy, benchSeries });
        var p = aligned[0];
        var b = aligned[1];
        int n = p.Count;

        var assumptions = new List<string>
        {
            $"Zeitbasis: gemeinsame {PerformanceMetricsCalculator.FrequencyLabel(strategy.Frequency)}-Perioden (innere Verknüpfung, keine Fortschreibung).",
            $"Währung Strategie {strategy.Currency} / Benchmark {benchmark.Currency}.",
            $"Kapitalbasis Strategie: {o.StrategyCapitalBasisNote}",
            o.RiskFreeAnnualRate == 0
                ? "Risikofreier Zins = 0 % p. a. (gesetzt)."
                : $"Risikofreier Zins = {o.RiskFreeAnnualRate:P2} p. a., geometrisch auf die Periode umgerechnet."
        };
        var warnings = new List<string>();

        if (!benchmark.IsTotalReturn)
            warnings.Add("Die Benchmarkreihe ist nicht als Total Return ausgewiesen. Ohne reinvestierte Dividenden " +
                         "wird die Vergleichsrendite unterschätzt — der Vergleich ist dann zugunsten der Strategie verzerrt.");
        if (!string.Equals(strategy.Currency, benchmark.Currency, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"Unterschiedliche Währungen ({strategy.Currency} vs. {benchmark.Currency}) — ohne Umrechnung nicht vergleichbar.");
        if (!o.StrategyIsFullyFunded)
            warnings.Add("Nicht bestätigt, dass die Strategie-Equity ein voll finanziertes Konto abbildet. " +
                         "Futures werden auf Margin gehandelt: eine auf die Margin bezogene Rendite ist KEINE " +
                         "Gesamtkapitalrendite und darf nicht mit einem Index verglichen werden.");

        if (n < 2)
            return new BenchmarkComparisonResult
            {
                StrategyName = strategy.Name, BenchmarkName = benchmark.Name,
                BenchmarkProvenance = benchmark.Provenance,
                CommonPeriods = n, Available = false,
                Assumptions = assumptions, Warnings = warnings,
                UnavailableReason = $"Nur {n} gemeinsame Perioden zwischen Strategie und Benchmark — kein Vergleich möglich."
            };

        double? ppy = p.PeriodsPerYear(o.AnnualizationBasis, o.FixedPeriodsPerYear);
        double rfPeriod = ppy is > 0 && o.RiskFreeAnnualRate != 0
            ? Math.Pow(1.0 + o.RiskFreeAnnualRate, 1.0 / ppy.Value) - 1.0
            : 0.0;

        string inputs = $"{n} gemeinsame Perioden, Strategie '{strategy.Name}' vs. Benchmark '{benchmark.Name}'.";
        var metrics = new List<QuantMetric>();

        // --- OLS auf Überschussrenditen ---
        var x = new double[n];
        var y = new double[n];
        for (int i = 0; i < n; i++) { x[i] = b.Returns[i] - rfPeriod; y[i] = p.Returns[i] - rfPeriod; }

        double mx = Stats.Mean(x), my = Stats.Mean(y);
        double sxx = 0, sxy = 0;
        for (int i = 0; i < n; i++) { double dx = x[i] - mx; sxx += dx * dx; sxy += dx * (y[i] - my); }

        if (sxx <= 0)
        {
            metrics.Add(QuantMetric.Unavailable("beta", "Beta", "ratio", "OLS-Steigung von (r_p−r_f) auf (r_b−r_f)",
                inputs, "Benchmark-Überschussrenditen sind konstant — Beta nicht bestimmbar.", n));
            metrics.Add(QuantMetric.Unavailable("alpha_annual", "Alpha p. a.", "fraction/yr",
                "OLS-Achsenabschnitt × Perioden/Jahr", inputs, "Regression nicht bestimmbar.", n));
        }
        else
        {
            double beta = sxy / sxx;
            double alphaP = my - beta * mx;

            double rss = 0;
            for (int i = 0; i < n; i++) { double e = y[i] - (alphaP + beta * x[i]); rss += e * e; }
            double? seBeta = null, seAlpha = null, s2 = null;
            if (n > 2)
            {
                s2 = rss / (n - 2);
                seBeta = Math.Sqrt(s2.Value / sxx);
                seAlpha = Math.Sqrt(s2.Value * (1.0 / n + mx * mx / sxx));
            }

            double tss = 0;
            for (int i = 0; i < n; i++) { double d = y[i] - my; tss += d * d; }
            double? r2 = tss > 0 ? 1.0 - rss / tss : null;

            metrics.Add(new QuantMetric
            {
                Key = "beta", Label = "Beta", Unit = "ratio", Value = beta,
                Method = "OLS-Steigung der Regression (r_p − r_f) = α + β·(r_b − r_f) + ε",
                Inputs = inputs, SampleSize = n,
                Limitation = seBeta is null
                    ? "Standardfehler erst ab 3 Perioden bestimmbar."
                    : $"Standardfehler {seBeta:0.####}, t = {beta / seBeta:0.##}. Gilt nur für den ausgewerteten Zeitraum."
            });
            if (seBeta is not null)
                metrics.Add(new QuantMetric
                {
                    Key = "beta_stderr", Label = "Beta — Standardfehler", Unit = "ratio", Value = seBeta,
                    Method = "√(s²/Σ(x−x̄)²) mit s² = RSS/(n−2)", Inputs = inputs, SampleSize = n
                });

            metrics.Add(ppy is null
                ? QuantMetric.Unavailable("alpha_annual", "Alpha p. a.", "fraction/yr",
                    "OLS-Achsenabschnitt × Perioden/Jahr", inputs, "Jahresfaktor nicht bestimmbar.", n)
                : new QuantMetric
                {
                    Key = "alpha_annual", Label = "Alpha p. a.", Unit = "fraction/yr", Value = alphaP * ppy.Value,
                    Method = "Jensen-Alpha: OLS-Achsenabschnitt je Periode × Perioden/Jahr (arithmetisch skaliert)",
                    Inputs = inputs, SampleSize = n,
                    Limitation = seAlpha is null
                        ? "Standardfehler erst ab 3 Perioden bestimmbar."
                        : $"Standardfehler je Periode {seAlpha:0.######}, t = {alphaP / seAlpha:0.##}. " +
                          "Ein Alpha ohne signifikanten t-Wert ist nicht von Zufall zu unterscheiden."
                });
            if (seAlpha is not null)
                metrics.Add(new QuantMetric
                {
                    Key = "alpha_tstat", Label = "Alpha — t-Wert", Unit = "ratio", Value = alphaP / seAlpha,
                    Method = "Achsenabschnitt / Standardfehler des Achsenabschnitts", Inputs = inputs, SampleSize = n,
                    Limitation = "Setzt unabhängige, homoskedastische Residuen voraus; bei Autokorrelation überschätzt."
                });

            metrics.Add(r2 is null
                ? QuantMetric.Unavailable("r_squared", "Bestimmtheitsmaß R²", "ratio", "1 − RSS/TSS", inputs,
                    "Strategierenditen sind konstant.", n)
                : new QuantMetric
                {
                    Key = "r_squared", Label = "Bestimmtheitsmaß R²", Unit = "ratio", Value = r2,
                    Method = "1 − RSS/TSS der Regression", Inputs = inputs, SampleSize = n
                });
        }

        // --- Aktive Rendite / Tracking Error / Information Ratio ---
        var active = new List<double>(n);
        for (int i = 0; i < n; i++) active.Add(p.Returns[i] - b.Returns[i]);
        double? teSd = Stats.StdDevSample(active);

        // Bezugsgröße für „Tracking Error praktisch 0": die Streuung der beiden Ausgangsreihen.
        // Ohne diesen Bezug würde reine Gleitkomma-Rundung eine gewaltige Information Ratio erzeugen.
        double returnScale = Math.Max(Stats.MaxAbs(p.Returns), Stats.MaxAbs(b.Returns));

        if (teSd is null || ppy is null)
            metrics.Add(QuantMetric.Unavailable("information_ratio", "Information Ratio", "ratio",
                "Mittel(aktive Rendite) / StdAbw(aktive Rendite) × √(Perioden/Jahr)", inputs,
                teSd is null ? "Weniger als 2 gemeinsame Perioden." : "Jahresfaktor nicht bestimmbar.", n));
        else if (Stats.IsNegligibleDeviation(teSd.Value, returnScale, 1e-9))
            metrics.Add(QuantMetric.Unavailable("information_ratio", "Information Ratio", "ratio",
                "Mittel(aktive Rendite) / StdAbw(aktive Rendite) × √(Perioden/Jahr)", inputs,
                "Tracking Error = 0 — Information Ratio nicht definiert.", n));
        else
        {
            metrics.Add(new QuantMetric
            {
                Key = "tracking_error_annual", Label = "Tracking Error p. a.", Unit = "fraction/yr",
                Value = teSd.Value * Math.Sqrt(ppy.Value),
                Method = "StdAbw(r_p − r_b, n−1) × √(Perioden/Jahr)", Inputs = inputs, SampleSize = n
            });
            metrics.Add(new QuantMetric
            {
                Key = "information_ratio", Label = "Information Ratio", Unit = "ratio",
                Value = Stats.Mean(active) / teSd.Value * Math.Sqrt(ppy.Value),
                Method = "Mittel(r_p − r_b) / StdAbw(r_p − r_b) × √(Perioden/Jahr)",
                Inputs = inputs, SampleSize = n,
                Limitation = "Keine Aussage über künftige Outperformance; hängt stark vom gewählten Zeitraum ab."
            });
        }

        var corr = Stats.Correlation(p.Returns, b.Returns);
        metrics.Add(corr is null
            ? QuantMetric.Unavailable("correlation", "Korrelation zur Benchmark", "ratio", "Pearson-Korrelation", inputs,
                "Mindestens eine Reihe ist konstant.", n)
            : new QuantMetric
            {
                Key = "correlation", Label = "Korrelation zur Benchmark", Unit = "ratio", Value = corr,
                Method = "Pearson-Korrelation der Periodenrenditen", Inputs = inputs, SampleSize = n
            });

        return new BenchmarkComparisonResult
        {
            StrategyName = strategy.Name,
            BenchmarkName = benchmark.Name,
            BenchmarkProvenance = benchmark.Provenance,
            CommonPeriods = n,
            From = p.Timestamps.Count > 0 ? p.Timestamps[0] : null,
            To = p.Timestamps.Count > 0 ? p.Timestamps[^1] : null,
            PeriodsPerYear = ppy,
            Metrics = metrics,
            AlignedStrategy = p,
            AlignedBenchmark = b,
            Assumptions = assumptions,
            Warnings = warnings,
            Available = true
        };
    }
}
