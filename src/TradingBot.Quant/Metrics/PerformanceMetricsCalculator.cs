using TradingBot.Quant.Series;
using TradingBot.Quant.Statistics;

namespace TradingBot.Quant.Metrics;

/// <summary>Festlegungen, die das Ergebnis verändern und deshalb immer mit ausgewiesen werden.</summary>
public sealed record PerformanceMetricsOptions
{
    /// <summary>Wie Perioden pro Jahr bestimmt werden.</summary>
    public AnnualizationBasis AnnualizationBasis { get; init; } = AnnualizationBasis.Observed;

    /// <summary>Fester Jahresfaktor, wenn <see cref="AnnualizationBasis.Fixed"/> gewählt ist (z. B. 252).</summary>
    public double? FixedPeriodsPerYear { get; init; }

    /// <summary>Risikofreier Zins p. a. als Dezimalzahl (0,04 = 4 %). Standard 0 — dann ist Sharpe eine reine Rendite/Risiko-Kennzahl.</summary>
    public double RiskFreeAnnualRate { get; init; }

    /// <summary>Signifikanzniveau für Expected Shortfall / VaR (0,05 = schlechteste 5 %).</summary>
    public double ExpectedShortfallAlpha { get; init; } = 0.05;

    /// <summary>Mindestzahl an Renditeperioden, unterhalb derer Kennzahlen als nicht belastbar gelten.</summary>
    public int MinimumPeriods { get; init; } = 20;
}

/// <summary>Ergebnis der Kennzahlenberechnung samt Kontext für den Bericht.</summary>
public sealed record PerformanceMetricsResult
{
    public required string Name { get; init; }
    public required IReadOnlyList<QuantMetric> Metrics { get; init; }
    public DrawdownInfo Drawdown { get; init; } = DrawdownInfo.Empty;

    public EquityBasis Basis { get; init; }
    public ReturnFrequency Frequency { get; init; }
    public int SampleSize { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }

    public double? PeriodsPerYear { get; init; }
    public required string AnnualizationNote { get; init; }
    public required string RiskFreeNote { get; init; }

    /// <summary>Hinweise, die im Bericht sichtbar sein müssen (z. B. zu kleine Stichprobe).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public QuantMetric? Get(string key) => Metrics.FirstOrDefault(m => m.Key == key);
    public double? Value(string key) => Get(key)?.Value;
}

/// <summary>
/// Berechnet die Kern-Performancekennzahlen aus einer einheitlichen Netto-Renditereihe.
///
/// Festgelegte Konventionen (bewusst explizit, weil sie das Ergebnis verändern):
/// - Renditen sind EINFACHE Periodenrenditen der gewählten Kapitalbasis (realisiert oder gesamt).
/// - Volatilität: Stichproben-Standardabweichung (Nenner n−1), annualisiert mit √(Perioden/Jahr).
/// - Sharpe: (Mittelwert der Überschussrendite / Standardabweichung) × √(Perioden/Jahr).
///   Der risikofreie Zins wird GEOMETRISCH auf die Periode heruntergebrochen.
/// - Sortino: gleiche Zählerdefinition; Nenner = Downside-Deviation gegenüber derselben Mindestrendite,
///   Quadratsumme über ALLE Perioden geteilt (nicht nur über die negativen).
/// - CAGR: geometrisch aus Anfangs-/Endkapital über die tatsächlich überspannte Kalenderzeit.
/// - Calmar: CAGR / maximaler relativer Drawdown.
/// - Expected Shortfall: historischer Mittelwert der schlechtesten α-Quantil-Perioden (kein Modell).
/// Nicht definierte Fälle liefern null mit Begründung — nie 0 als Ersatz.
/// </summary>
public static class PerformanceMetricsCalculator
{
    public static PerformanceMetricsResult Compute(ReturnSeries series, PerformanceMetricsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(series);
        var o = options ?? new PerformanceMetricsOptions();
        var notes = new List<string>();
        var metrics = new List<QuantMetric>();

        int n = series.Count;
        double? ppy = series.PeriodsPerYear(o.AnnualizationBasis, o.FixedPeriodsPerYear);

        string annNote = o.AnnualizationBasis == AnnualizationBasis.Fixed
            ? $"Fester Jahresfaktor: {o.FixedPeriodsPerYear?.ToString("0.###") ?? "nicht gesetzt"} Perioden/Jahr."
            : ppy is null
                ? "Jahresfaktor empirisch — mangels ausreichender Zeitspanne nicht bestimmbar."
                : $"Jahresfaktor empirisch aus der Beobachtungsdichte: {ppy:0.##} Perioden/Jahr " +
                  $"({n} Renditeperioden über {(series.Timestamps[^1] - series.Timestamps[0]).TotalDays / 365.25:0.###} Jahre).";

        string rfNote = o.RiskFreeAnnualRate == 0
            ? "Risikofreier Zins = 0 % p. a. (gesetzt). Sharpe/Sortino sind damit reine Rendite-Risiko-Verhältnisse."
            : $"Risikofreier Zins = {o.RiskFreeAnnualRate:P2} p. a., geometrisch auf die Periode umgerechnet.";

        string inputs = $"{n} {FrequencyLabel(series.Frequency)}-Nettorenditen, Kapitalbasis {BasisLabel(series.Basis)}.";

        if (n == 0)
        {
            notes.Add("Keine Renditeperioden vorhanden — es kann keine Kennzahl berechnet werden.");
            return Empty(series, annNote, rfNote, notes);
        }
        if (n < o.MinimumPeriods)
            notes.Add($"Nur {n} Renditeperioden (< {o.MinimumPeriods}). Alle Kennzahlen sind statistisch wenig belastbar.");

        var r = series.Returns;
        var levels = LevelsWithStart(series);
        var times = TimesWithStart(series);
        // Skalenniveau der Reihe — Streuungen darunter gelten als rechnerisch 0 (siehe Stats.IsNegligibleDeviation).
        double scale = Stats.MaxAbs(r);

        // --- Drawdown (Basis für Calmar) ---
        var dd = ComputeDrawdown(levels, times);

        // --- Mittelwert / Volatilität ---
        double mean = Stats.Mean(r);
        double? sd = Stats.StdDevSample(r);

        metrics.Add(new QuantMetric
        {
            Key = "mean_return", Label = "Mittlere Periodenrendite", Unit = "fraction", Value = mean,
            Method = "arithmetisches Mittel der einfachen Periodenrenditen", Inputs = inputs, SampleSize = n
        });

        metrics.Add(sd is null
            ? QuantMetric.Unavailable("volatility_annual", "Volatilität p. a.", "fraction/yr",
                "Stichproben-Standardabweichung × √(Perioden/Jahr)", inputs, "Weniger als 2 Renditeperioden.", n)
            : ppy is null
                ? QuantMetric.Unavailable("volatility_annual", "Volatilität p. a.", "fraction/yr",
                    "Stichproben-Standardabweichung × √(Perioden/Jahr)", inputs, "Jahresfaktor nicht bestimmbar.", n)
                : new QuantMetric
                {
                    Key = "volatility_annual", Label = "Volatilität p. a.", Unit = "fraction/yr",
                    Value = sd.Value * Math.Sqrt(ppy.Value),
                    Method = "Stichproben-Standardabweichung (n−1) × √(Perioden/Jahr)",
                    Inputs = inputs, SampleSize = n,
                    Limitation = "Setzt näherungsweise unkorrelierte Perioden voraus; Autokorrelation verzerrt die Skalierung."
                });

        // --- Risikofreier Zins je Periode (geometrisch) ---
        double? rfPeriod = null;
        if (ppy is > 0)
            rfPeriod = o.RiskFreeAnnualRate == 0 ? 0.0 : Math.Pow(1.0 + o.RiskFreeAnnualRate, 1.0 / ppy.Value) - 1.0;

        // --- Sharpe ---
        if (sd is null || ppy is null)
            metrics.Add(QuantMetric.Unavailable("sharpe", "Sharpe Ratio", "ratio",
                "(Mittel der Überschussrendite / Standardabweichung) × √(Perioden/Jahr)", inputs,
                sd is null ? "Weniger als 2 Renditeperioden." : "Jahresfaktor nicht bestimmbar.", n));
        else if (Stats.IsNegligibleDeviation(sd.Value, scale))
            metrics.Add(QuantMetric.Unavailable("sharpe", "Sharpe Ratio", "ratio",
                "(Mittel der Überschussrendite / Standardabweichung) × √(Perioden/Jahr)", inputs,
                "Standardabweichung = 0 (konstante Renditereihe) — Sharpe ist nicht definiert.", n));
        else
            metrics.Add(new QuantMetric
            {
                Key = "sharpe", Label = "Sharpe Ratio", Unit = "ratio",
                Value = (mean - (rfPeriod ?? 0.0)) / sd.Value * Math.Sqrt(ppy.Value),
                Method = "(Mittel(r) − r_f,Periode) / StdAbw(r, n−1) × √(Perioden/Jahr)",
                Inputs = inputs + " " + rfNote, SampleSize = n,
                Limitation = "Nicht normalverteilte, autokorrelierte oder kurze Reihen überschätzen die Aussagekraft; siehe PSR/DSR."
            });

        // --- Sortino ---
        double mar = rfPeriod ?? 0.0;
        double downsideSq = 0;
        int negCount = 0;
        foreach (var v in r)
        {
            double d = Math.Min(v - mar, 0.0);
            if (d < 0) negCount++;
            downsideSq += d * d;
        }
        double downside = Math.Sqrt(downsideSq / n);
        if (ppy is null)
            metrics.Add(QuantMetric.Unavailable("sortino", "Sortino Ratio", "ratio",
                "(Mittel(r) − MAR) / Downside-Deviation × √(Perioden/Jahr)", inputs, "Jahresfaktor nicht bestimmbar.", n));
        else if (negCount == 0 || Stats.IsNegligibleDeviation(downside, scale))
            metrics.Add(QuantMetric.Unavailable("sortino", "Sortino Ratio", "ratio",
                "(Mittel(r) − MAR) / Downside-Deviation × √(Perioden/Jahr)", inputs,
                "Keine Periode unterhalb der Mindestrendite — Downside-Deviation = 0, Sortino nicht definiert.", n));
        else
            metrics.Add(new QuantMetric
            {
                Key = "sortino", Label = "Sortino Ratio", Unit = "ratio",
                Value = (mean - mar) / downside * Math.Sqrt(ppy.Value),
                Method = "(Mittel(r) − MAR) / √(Σ min(r−MAR,0)² / n) × √(Perioden/Jahr); MAR = risikofreier Zins je Periode",
                Inputs = inputs + " " + rfNote, SampleSize = n,
                Limitation = "Nenner über ALLE Perioden gemittelt (nicht nur über Verlustperioden) — abweichende Konventionen existieren."
            });

        // --- CAGR ---
        double startLevel = levels[0], endLevel = levels[^1];
        double years = (times[^1] - times[0]).TotalDays / 365.25;
        if (startLevel <= 0)
            metrics.Add(QuantMetric.Unavailable("cagr", "CAGR", "fraction/yr",
                "(Endkapital / Anfangskapital)^(1/Jahre) − 1", inputs, "Anfangskapital ≤ 0.", n));
        else if (years <= 0)
            metrics.Add(QuantMetric.Unavailable("cagr", "CAGR", "fraction/yr",
                "(Endkapital / Anfangskapital)^(1/Jahre) − 1", inputs, "Überspannte Zeit = 0.", n));
        else if (endLevel <= 0)
            metrics.Add(QuantMetric.Unavailable("cagr", "CAGR", "fraction/yr",
                "(Endkapital / Anfangskapital)^(1/Jahre) − 1", inputs,
                "Endkapital ≤ 0 (Totalverlust) — geometrische Rendite nicht definiert.", n));
        else
            metrics.Add(new QuantMetric
            {
                Key = "cagr", Label = "CAGR", Unit = "fraction/yr",
                Value = Math.Pow(endLevel / startLevel, 1.0 / years) - 1.0,
                Method = "(Endkapital / Anfangskapital)^(1/Jahre) − 1, Jahre = Kalenderspanne / 365,25",
                Inputs = inputs + $" Kapital {startLevel:0.##} → {endLevel:0.##} über {years:0.###} Jahre.",
                SampleSize = n,
                Limitation = years < 1
                    ? "Zeitraum unter einem Jahr — die Hochrechnung auf ein Jahr ist entsprechend unsicher."
                    : null
            });

        // --- Drawdown-Kennzahlen ---
        metrics.Add(new QuantMetric
        {
            Key = "max_drawdown", Label = "Maximaler Drawdown", Unit = "fraction",
            Value = dd.MaxDrawdownFraction,
            Method = "größter relativer Rückgang vom laufenden Höchststand der Kapitalkurve",
            Inputs = inputs + $" Kapitalbasis {BasisLabel(series.Basis)}.", SampleSize = n,
            Limitation = "Auf der gewählten Kapitalbasis; realisierte und gesamte Drawdowns unterscheiden sich."
        });
        metrics.Add(new QuantMetric
        {
            Key = "max_drawdown_abs", Label = "Maximaler Drawdown (absolut)", Unit = "currency",
            Value = dd.MaxDrawdownAbsolute,
            Method = "größter absoluter Rückgang vom laufenden Höchststand", Inputs = inputs, SampleSize = n
        });
        metrics.Add(new QuantMetric
        {
            Key = "drawdown_duration_days", Label = "Längste Unterwasserphase", Unit = "days",
            Value = dd.LongestUnderwaterDays,
            Method = "längste Zeitspanne zwischen einem Höchststand und dessen Wiedererreichen",
            Inputs = inputs, SampleSize = n,
            Limitation = dd.UnderwaterAtEnd ? "Die Reihe endet unter Wasser — die Phase ist nach unten offen." : null
        });

        // --- Calmar ---
        double? cagr = metrics.First(m => m.Key == "cagr").Value;
        if (cagr is null)
            metrics.Add(QuantMetric.Unavailable("calmar", "Calmar Ratio", "ratio", "CAGR / max. Drawdown", inputs,
                "CAGR nicht berechenbar.", n));
        else if (dd.MaxDrawdownFraction <= 0)
            metrics.Add(QuantMetric.Unavailable("calmar", "Calmar Ratio", "ratio", "CAGR / max. Drawdown", inputs,
                "Maximaler Drawdown = 0 — Calmar nicht definiert.", n));
        else
            metrics.Add(new QuantMetric
            {
                Key = "calmar", Label = "Calmar Ratio", Unit = "ratio",
                Value = cagr.Value / dd.MaxDrawdownFraction,
                Method = "CAGR / maximaler relativer Drawdown", Inputs = inputs, SampleSize = n,
                Limitation = "Beide Bestandteile hängen stark vom Zeitraum ab; über kurze Reihen kaum vergleichbar."
            });

        // --- VaR / Expected Shortfall (historisch) ---
        double alpha = o.ExpectedShortfallAlpha;
        int tail = (int)Math.Floor(alpha * n);
        if (alpha <= 0 || alpha >= 1)
            metrics.Add(QuantMetric.Unavailable("expected_shortfall", "Expected Shortfall", "fraction",
                "Mittel der schlechtesten α-Quantil-Perioden", inputs, "α muss in (0,1) liegen.", n));
        else if (tail < 1)
            metrics.Add(QuantMetric.Unavailable("expected_shortfall", $"Expected Shortfall ({alpha:P0})", "fraction",
                "Mittel der schlechtesten α-Quantil-Perioden", inputs,
                $"Zu wenige Perioden: α·n = {alpha * n:0.##} < 1 Beobachtung im Verlustende.", n));
        else
        {
            var sorted = r.OrderBy(v => v).Take(tail).ToList();
            metrics.Add(new QuantMetric
            {
                Key = "expected_shortfall", Label = $"Expected Shortfall ({alpha:P0})", Unit = "fraction",
                Value = Stats.Mean(sorted),
                Method = $"historischer Mittelwert der {tail} schlechtesten von {n} Perioden (kein Verteilungsmodell)",
                Inputs = inputs, SampleSize = tail,
                Limitation = "Rein empirisch — beschreibt nur den beobachteten Zeitraum, keine Wahrscheinlichkeitsaussage für die Zukunft."
            });
            metrics.Add(new QuantMetric
            {
                Key = "var_historic", Label = $"VaR ({alpha:P0}, historisch)", Unit = "fraction",
                Value = Stats.Percentile(r, alpha),
                Method = "empirisches α-Quantil der Periodenrenditen (lineare Interpolation)",
                Inputs = inputs, SampleSize = n,
                Limitation = "Rein empirisch; keine Aussage über Verluste jenseits des beobachteten Bereichs."
            });
        }

        return new PerformanceMetricsResult
        {
            Name = series.Name,
            Metrics = metrics,
            Drawdown = dd,
            Basis = series.Basis,
            Frequency = series.Frequency,
            SampleSize = n,
            From = times.Count > 0 ? times[0] : null,
            To = times.Count > 0 ? times[^1] : null,
            PeriodsPerYear = ppy,
            AnnualizationNote = annNote,
            RiskFreeNote = rfNote,
            Notes = notes
        };
    }

    /// <summary>
    /// Drawdown-Analyse auf einer Kapitalkurve. Der laufende Höchststand startet beim ersten
    /// Kurvenwert (nicht bei 0), damit relative Drawdowns wohldefiniert sind.
    /// </summary>
    public static DrawdownInfo ComputeDrawdown(IReadOnlyList<double> levels, IReadOnlyList<DateTimeOffset> times)
    {
        if (levels.Count == 0) return DrawdownInfo.Empty;

        double peak = levels[0];
        int peakIdx = 0;
        double maxFrac = 0, maxAbs = 0;
        int maxPeakIdx = 0, maxTroughIdx = 0;
        int longestPeriods = 0;
        double longestDays = 0;
        int currentPeakIdx = 0;
        bool underwater = false;
        DateTimeOffset? recovery = null;
        int bestRecoveryIdx = -1;

        for (int i = 1; i < levels.Count; i++)
        {
            if (levels[i] >= peak)
            {
                if (underwater)
                {
                    int periods = i - currentPeakIdx;
                    double days = (times[i] - times[currentPeakIdx]).TotalDays;
                    if (periods > longestPeriods) { longestPeriods = periods; longestDays = days; }
                    underwater = false;
                }
                peak = levels[i];
                peakIdx = i;
                currentPeakIdx = i;
                continue;
            }

            if (!underwater) { underwater = true; currentPeakIdx = peakIdx; }

            double abs = peak - levels[i];
            double frac = peak > 0 ? abs / peak : 0;
            if (frac > maxFrac || (frac == maxFrac && abs > maxAbs))
            {
                maxFrac = frac; maxAbs = abs;
                maxPeakIdx = peakIdx; maxTroughIdx = i;
                bestRecoveryIdx = -1;
            }
        }

        if (underwater)
        {
            int periods = levels.Count - 1 - currentPeakIdx;
            double days = (times[^1] - times[currentPeakIdx]).TotalDays;
            if (periods > longestPeriods) { longestPeriods = periods; longestDays = days; }
        }

        // Erholungszeitpunkt des größten Drawdowns bestimmen.
        if (maxFrac > 0)
            for (int i = maxTroughIdx + 1; i < levels.Count; i++)
                if (levels[i] >= levels[maxPeakIdx]) { bestRecoveryIdx = i; recovery = times[i]; break; }

        return new DrawdownInfo
        {
            MaxDrawdownFraction = maxFrac,
            MaxDrawdownAbsolute = maxAbs,
            PeakTime = maxFrac > 0 ? times[maxPeakIdx] : null,
            TroughTime = maxFrac > 0 ? times[maxTroughIdx] : null,
            RecoveryTime = recovery,
            LongestUnderwaterPeriods = longestPeriods,
            LongestUnderwaterDays = longestDays,
            UnderwaterAtEnd = underwater && bestRecoveryIdx < 0 && levels[^1] < peak
        };
    }

    /// <summary>Kapitalkurve inklusive Startpunkt (vor der ersten Rendite), aus der Reihe rekonstruiert.</summary>
    internal static List<double> LevelsWithStart(ReturnSeries s)
    {
        var levels = new List<double>(s.Count + 1);
        if (s.Count == 0) return levels;
        double first = 1.0 + s.Returns[0];
        double start = Math.Abs(first) > 1e-12 ? s.EquityLevels[0] / first : s.InitialCapital;
        levels.Add(start);
        levels.AddRange(s.EquityLevels);
        return levels;
    }

    /// <summary>Zeitachse passend zu <see cref="LevelsWithStart"/>; der Startpunkt erhält den ersten Zeitstempel.</summary>
    internal static List<DateTimeOffset> TimesWithStart(ReturnSeries s)
    {
        var times = new List<DateTimeOffset>(s.Count + 1);
        if (s.Count == 0) return times;
        // Der Startpunkt liegt eine Periode vor dem ersten Renditezeitstempel; die Spanne wird aus
        // dem ersten beobachteten Abstand geschätzt und ist nur für Dauerangaben relevant.
        var step = s.Count >= 2 ? s.Timestamps[1] - s.Timestamps[0] : TimeSpan.Zero;
        times.Add(s.Timestamps[0] - step);
        times.AddRange(s.Timestamps);
        return times;
    }

    private static PerformanceMetricsResult Empty(ReturnSeries s, string annNote, string rfNote, List<string> notes) => new()
    {
        Name = s.Name, Metrics = Array.Empty<QuantMetric>(), Basis = s.Basis, Frequency = s.Frequency,
        SampleSize = 0, PeriodsPerYear = null, AnnualizationNote = annNote, RiskFreeNote = rfNote, Notes = notes
    };

    /// <summary>Anzeigename der Frequenz — Teil der Methodenangabe im Bericht.</summary>
    public static string FrequencyLabel(ReturnFrequency f) => f switch
    {
        ReturnFrequency.Bar => "Bar",
        ReturnFrequency.Daily => "Tages",
        ReturnFrequency.Weekly => "Wochen",
        ReturnFrequency.Monthly => "Monats",
        _ => f.ToString()
    };

    internal static string BasisLabel(EquityBasis b) =>
        b == EquityBasis.Total ? "GESAMT (inkl. offener Positionen, Mark-to-Market)" : "REALISIERT (nur abgeschlossene Trades)";
}
