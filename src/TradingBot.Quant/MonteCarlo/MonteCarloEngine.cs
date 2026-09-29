using TradingBot.Quant.Statistics;

namespace TradingBot.Quant.MonteCarlo;

/// <summary>Wie aus einer Beobachtungsfolge eine Kapitalkurve wird.</summary>
public enum AccumulationMode
{
    /// <summary>Beobachtungen sind Geldbeträge (Trade-NetPnL): Kapital = Start + Σ Beträge.</summary>
    Additive = 0,
    /// <summary>Beobachtungen sind Renditen: Kapital = Start · Π (1 + r).</summary>
    Multiplicative = 1
}

/// <summary>Alle Festlegungen eines Monte-Carlo-Laufs. Werden vollständig mit dem Ergebnis gespeichert.</summary>
public sealed record MonteCarloOptions
{
    public ResamplingMethod Method { get; init; } = ResamplingMethod.Permutation;
    public AccumulationMode Accumulation { get; init; } = AccumulationMode.Additive;

    public int Iterations { get; init; } = 1000;
    public int Seed { get; init; } = 12345;

    /// <summary>Block- bzw. mittlere Blocklänge; null = Vorgabe n^(1/3).</summary>
    public int? BlockLength { get; init; }

    /// <summary>Länge der simulierten Reihe; null = Länge der Originalreihe.</summary>
    public int? Horizon { get; init; }

    public double InitialCapital { get; init; } = 10_000.0;

    /// <summary>
    /// Ausdrückliche Kapitalgrenze (absoluter Kapitalstand). Nur wenn gesetzt, wird der Anteil der
    /// Läufe berichtet, die diese Grenze innerhalb des Horizonts unterschreiten.
    /// </summary>
    public double? CapitalBarrier { get; init; }

    /// <summary>Obergrenze der Rechenzeit; danach wird mit den bis dahin fertigen Läufen abgeschlossen.</summary>
    public TimeSpan? TimeLimit { get; init; }
}

/// <summary>Verteilungskennzahlen einer simulierten Größe.</summary>
public sealed record MonteCarloDistribution
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Unit { get; init; }
    public double Min { get; init; }
    public double P5 { get; init; }
    public double P25 { get; init; }
    public double Median { get; init; }
    public double P75 { get; init; }
    public double P95 { get; init; }
    public double Max { get; init; }
    public double Mean { get; init; }
    /// <summary>Alle Einzelwerte — Grundlage für Histogramme im Dashboard.</summary>
    public IReadOnlyList<double> Values { get; init; } = Array.Empty<double>();

    public static MonteCarloDistribution From(string key, string label, string unit, List<double> values) => new()
    {
        Key = key, Label = label, Unit = unit,
        Min = values.Count == 0 ? double.NaN : values.Min(),
        Max = values.Count == 0 ? double.NaN : values.Max(),
        Mean = Stats.Mean(values),
        P5 = Stats.Percentile(values, 0.05),
        P25 = Stats.Percentile(values, 0.25),
        Median = Stats.Percentile(values, 0.50),
        P75 = Stats.Percentile(values, 0.75),
        P95 = Stats.Percentile(values, 0.95),
        Values = values
    };
}

/// <summary>Ergebnis eines Monte-Carlo-Laufs samt aller Annahmen.</summary>
public sealed record MonteCarloResult
{
    public required MonteCarloOptions Options { get; init; }
    public int CompletedIterations { get; init; }
    public int ObservationCount { get; init; }
    public int EffectiveBlockLength { get; init; }
    public int EffectiveHorizon { get; init; }
    public bool StoppedByTimeLimit { get; init; }

    public MonteCarloDistribution FinalCapital { get; init; } = MonteCarloDistribution.From("final_capital", "Endkapital", "currency", new List<double>());
    public MonteCarloDistribution MaxDrawdown { get; init; } = MonteCarloDistribution.From("max_drawdown", "Maximaler Drawdown", "fraction", new List<double>());
    public MonteCarloDistribution LongestLosingStreak { get; init; } = MonteCarloDistribution.From("losing_streak", "Längste Verlustserie", "count", new List<double>());

    /// <summary>Anteil der simulierten Läufe mit Endkapital unter dem Startkapital (KEINE Wahrscheinlichkeitsaussage).</summary>
    public double ShareOfRunsBelowStart { get; init; }

    /// <summary>Anteil der Läufe, die die ausdrücklich gesetzte Kapitalgrenze im Horizont unterschreiten. Null, wenn keine Grenze gesetzt.</summary>
    public double? ShareOfRunsBreachingBarrier { get; init; }

    /// <summary>Für den Bericht verpflichtende Einordnung des Verfahrens.</summary>
    public required IReadOnlyList<string> Assumptions { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Monte-Carlo-Analyse auf einer Beobachtungsreihe (Trade-NetPnL oder Periodenrenditen).
///
/// Einordnung, die in jedem Bericht mitgeführt wird: Die Ergebnisse sind SZENARIEN unter den
/// gewählten Resampling-Annahmen. Sie sind keine empirischen Wahrscheinlichkeiten für zukünftige
/// Marktverläufe — die Simulation zieht ausschließlich aus der beobachteten Vergangenheit und kann
/// nichts über Regimewechsel oder bisher nicht beobachtete Verluste aussagen.
/// </summary>
public static class MonteCarloEngine
{
    public static MonteCarloResult Run(
        IReadOnlyList<double> observations,
        MonteCarloOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Iterations <= 0) throw new ArgumentException("Iterations muss > 0 sein.", nameof(options));

        int n = observations.Count;
        var notes = new List<string>();
        int horizon = options.Horizon is > 0 ? options.Horizon.Value : n;
        int blockLength = options.BlockLength ?? Resampling.DefaultBlockLength(Math.Max(1, n));

        var assumptions = BuildAssumptions(options, n, horizon, blockLength);

        if (n == 0)
        {
            notes.Add("Keine Beobachtungen — keine Simulation möglich.");
            return new MonteCarloResult
            {
                Options = options, ObservationCount = 0, CompletedIterations = 0,
                EffectiveBlockLength = blockLength, EffectiveHorizon = horizon,
                Assumptions = assumptions, Notes = notes
            };
        }
        if (n < 20)
            notes.Add($"Nur {n} Beobachtungen — die Verteilungen sind entsprechend grob und wenig belastbar.");

        var rng = new DeterministicRng(options.Seed);
        var finals = new List<double>(options.Iterations);
        var dds = new List<double>(options.Iterations);
        var streaks = new List<double>(options.Iterations);
        int belowStart = 0, breaches = 0;
        bool stopped = false;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int i = 0;
        for (; i < options.Iterations; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (options.TimeLimit is { } limit && sw.Elapsed > limit)
            {
                stopped = true;
                notes.Add($"Zeitlimit {limit.TotalSeconds:0.#} s erreicht — ausgewertet wurden {i} von {options.Iterations} Läufen.");
                break;
            }

            var path = Resampling.BuildIndexPath(options.Method, n, rng, horizon, blockLength);
            var sample = Resampling.Apply(observations, path);

            var (final, maxDd, streak, breached) = Simulate(sample, options);
            finals.Add(final);
            dds.Add(maxDd);
            streaks.Add(streak);
            if (final < options.InitialCapital) belowStart++;
            if (breached) breaches++;

            if (progress is not null && (i & 63) == 0) progress.Report((i + 1) / (double)options.Iterations);
        }
        progress?.Report(1.0);

        int done = finals.Count;
        return new MonteCarloResult
        {
            Options = options,
            CompletedIterations = done,
            ObservationCount = n,
            EffectiveBlockLength = blockLength,
            EffectiveHorizon = horizon,
            StoppedByTimeLimit = stopped,
            FinalCapital = MonteCarloDistribution.From("final_capital", "Endkapital", "currency", finals),
            MaxDrawdown = MonteCarloDistribution.From("max_drawdown", "Maximaler Drawdown", "fraction", dds),
            LongestLosingStreak = MonteCarloDistribution.From("losing_streak", "Längste Verlustserie", "count", streaks),
            ShareOfRunsBelowStart = done == 0 ? double.NaN : (double)belowStart / done,
            ShareOfRunsBreachingBarrier = options.CapitalBarrier is null || done == 0 ? null : (double)breaches / done,
            Assumptions = assumptions,
            Notes = notes
        };
    }

    /// <summary>
    /// Gemeinsames Resampling mehrerer zeitlich ausgerichteter Reihen mit EINEM Index-Pfad je Lauf.
    /// Liefert je Reihe ein eigenes Ergebnis; die Abhängigkeit zwischen den Reihen bleibt erhalten,
    /// weil in jedem Lauf dieselben Zeitpunkte gezogen werden.
    /// </summary>
    public static IReadOnlyList<MonteCarloResult> RunJointly(
        IReadOnlyList<IReadOnlyList<double>> series,
        IReadOnlyList<string> names,
        MonteCarloOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(names);
        if (series.Count != names.Count) throw new ArgumentException("Zu jeder Reihe gehört ein Name.", nameof(names));
        if (series.Count == 0) return Array.Empty<MonteCarloResult>();

        int n = series[0].Count;
        foreach (var s in series)
            if (s.Count != n)
                throw new ArgumentException("Gemeinsames Resampling verlangt gleich lange, zeitlich ausgerichtete Reihen.", nameof(series));

        int horizon = options.Horizon is > 0 ? options.Horizon.Value : n;
        int blockLength = options.BlockLength ?? Resampling.DefaultBlockLength(Math.Max(1, n));
        var rng = new DeterministicRng(options.Seed);

        var finals = series.Select(_ => new List<double>(options.Iterations)).ToList();
        var dds = series.Select(_ => new List<double>(options.Iterations)).ToList();
        var streaks = series.Select(_ => new List<double>(options.Iterations)).ToList();
        var below = new int[series.Count];
        var breach = new int[series.Count];

        for (int it = 0; it < options.Iterations; it++)
        {
            ct.ThrowIfCancellationRequested();
            var path = Resampling.BuildIndexPath(options.Method, n, rng, horizon, blockLength);
            var samples = Resampling.ApplyJointly(series, path);
            for (int k = 0; k < samples.Count; k++)
            {
                var (final, dd, streak, breached) = Simulate(samples[k], options);
                finals[k].Add(final); dds[k].Add(dd); streaks[k].Add(streak);
                if (final < options.InitialCapital) below[k]++;
                if (breached) breach[k]++;
            }
            if (progress is not null && (it & 63) == 0) progress.Report((it + 1) / (double)options.Iterations);
        }
        progress?.Report(1.0);

        var assumptions = BuildAssumptions(options, n, horizon, blockLength).ToList();
        assumptions.Add("Gemeinsames Resampling: In jedem Lauf wurde für ALLE Reihen derselbe Index-Pfad verwendet, " +
                        "damit die Abhängigkeit zwischen den Reihen erhalten bleibt.");

        var results = new List<MonteCarloResult>(series.Count);
        for (int k = 0; k < series.Count; k++)
            results.Add(new MonteCarloResult
            {
                Options = options,
                CompletedIterations = finals[k].Count,
                ObservationCount = n,
                EffectiveBlockLength = blockLength,
                EffectiveHorizon = horizon,
                FinalCapital = MonteCarloDistribution.From("final_capital", $"Endkapital — {names[k]}", "currency", finals[k]),
                MaxDrawdown = MonteCarloDistribution.From("max_drawdown", $"Maximaler Drawdown — {names[k]}", "fraction", dds[k]),
                LongestLosingStreak = MonteCarloDistribution.From("losing_streak", $"Längste Verlustserie — {names[k]}", "count", streaks[k]),
                ShareOfRunsBelowStart = finals[k].Count == 0 ? double.NaN : (double)below[k] / finals[k].Count,
                ShareOfRunsBreachingBarrier = options.CapitalBarrier is null || finals[k].Count == 0 ? null : (double)breach[k] / finals[k].Count,
                Assumptions = assumptions
            });
        return results;
    }

    /// <summary>Ein simulierter Pfad: Endkapital, maximaler relativer Drawdown, längste Verlustserie, Grenzverletzung.</summary>
    internal static (double Final, double MaxDrawdown, int LosingStreak, bool BreachedBarrier) Simulate(
        IReadOnlyList<double> sample, MonteCarloOptions options)
    {
        double capital = options.InitialCapital;
        double peak = capital;
        double maxDd = 0;
        int streak = 0, maxStreak = 0;
        bool breached = false;

        foreach (var v in sample)
        {
            capital = options.Accumulation == AccumulationMode.Additive ? capital + v : capital * (1.0 + v);

            if (v < 0) { streak++; if (streak > maxStreak) maxStreak = streak; }
            else streak = 0;

            if (capital > peak) peak = capital;
            if (peak > 0)
            {
                double dd = (peak - capital) / peak;
                if (dd > maxDd) maxDd = dd;
            }
            if (options.CapitalBarrier is { } barrier && capital <= barrier) breached = true;
        }
        return (capital, maxDd, maxStreak, breached);
    }

    private static IReadOnlyList<string> BuildAssumptions(MonteCarloOptions o, int n, int horizon, int blockLength)
    {
        var a = new List<string>
        {
            $"Verfahren: {MethodLabel(o.Method)}.",
            $"Seed {o.Seed}, {o.Iterations} Wiederholungen, Horizont {horizon} Beobachtungen (Original: {n}).",
            $"Kapitalfortschreibung: {(o.Accumulation == AccumulationMode.Additive ? "additiv (Geldbeträge je Trade)" : "multiplikativ (Periodenrenditen)")}, Startkapital {o.InitialCapital:0.##}.",
            "Ergebnisse sind SZENARIEN unter diesen Annahmen — keine empirischen Wahrscheinlichkeiten für künftige Marktverläufe."
        };
        if (o.Method is ResamplingMethod.MovingBlock or ResamplingMethod.Stationary)
            a.Add($"Blocklänge {blockLength} ({(o.BlockLength is null ? "Vorgabe n^(1/3)" : "vorgegeben")}) — sie ist eine Annahme über die Reichweite serieller Abhängigkeit.");
        if (o.Method == ResamplingMethod.Permutation)
            a.Add("Permutation verändert nur die Reihenfolge: die Summe der Beobachtungen bleibt unverändert, " +
                  "es variieren ausschließlich Pfad, Drawdown und Verlustserien. Serielle Abhängigkeit wird zerstört.");
        if (o.CapitalBarrier is { } b)
            a.Add($"Kapitalgrenze {b:0.##} — der berichtete Anteil gilt ausschließlich für diese Grenze und diesen Horizont.");
        return a;
    }

    /// <summary>Anzeigename des Verfahrens — Teil der Annahmenliste im Bericht.</summary>
    public static string MethodLabel(ResamplingMethod m) => m switch
    {
        ResamplingMethod.Permutation => "Permutation der Beobachtungen (Reihenfolgeanalyse)",
        ResamplingMethod.MovingBlock => "Moving-Block-Bootstrap (feste Blocklänge, mit Zurücklegen)",
        ResamplingMethod.Stationary => "Stationärer Bootstrap (Politis/Romano, geometrische Blocklängen)",
        _ => m.ToString()
    };
}
