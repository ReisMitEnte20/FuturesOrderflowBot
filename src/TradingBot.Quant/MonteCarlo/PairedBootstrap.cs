using TradingBot.Quant.Statistics;

namespace TradingBot.Quant.MonteCarlo;

/// <summary>Verteilung einer gepaarten Differenz (Reihe Links − Reihe Rechts) über die Replikationen.</summary>
public sealed record PairedBootstrapDifference
{
    public required string LeftName { get; init; }
    public required string RightName { get; init; }
    /// <summary>Endkapital-Differenz (Links − Rechts) je Replikation, in Kapitaleinheiten.</summary>
    public required MonteCarloDistribution FinalCapitalDelta { get; init; }
    /// <summary>Endrendite-Differenz (Links − Rechts) je Replikation, als Bruchteil des Startkapitals.</summary>
    public required MonteCarloDistribution ReturnDelta { get; init; }
    /// <summary>Anteil der Replikationen, in denen Links ein höheres Endkapital erreicht als Rechts — KEINE
    /// Wahrscheinlichkeitsaussage über die Zukunft, sondern eine bedingte Auszählung unter dem Resampling.</summary>
    public double ShareLeftBeatsRight { get; init; }
    public double ShareTie { get; init; }
}

/// <summary>Ergebnis eines gepaarten Block-Bootstraps über mehrere ausgerichtete Renditereihen.</summary>
public sealed record PairedBootstrapResult
{
    public required IReadOnlyList<MonteCarloResult> PerSeries { get; init; }
    /// <summary>Alle ungeordneten Paare (Links/Rechts nach Name kanonisiert), damit die Kandidatenreihenfolge
    /// das Ergebnis nicht verändert.</summary>
    public required IReadOnlyList<PairedBootstrapDifference> Differences { get; init; }
    public required IReadOnlyList<string> Assumptions { get; init; }
    public int CompletedIterations { get; init; }
    public int ObservationCount { get; init; }
    public int EffectiveBlockLength { get; init; }
    public int EffectiveHorizon { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Gepaarter Block-Bootstrap für den FAIREN Vergleich mehrerer Strategien (und optional einer Benchmark) auf
/// zeitlich ausgerichteten Netto-OOS-Renditen. In JEDER Replikation wird EIN einziger Index-/Blockpfad gezogen
/// und auf ALLE Reihen angewendet (über <see cref="Resampling.ApplyJointly"/>), sodass die Reihen exakt
/// dieselbe umsortierte Zeitstruktur teilen. Dadurch sind die gepaarten Differenzen aussagekräftig und die
/// Ergebnisse hängen nicht von der Reihenfolge der Kandidaten ab.
///
/// Einordnung (immer mitzuführen): Die Verteilungen sind SZENARIEN unter der Resampling-Annahme, keine
/// Wahrscheinlichkeiten für zukünftige Marktverläufe. Es wird KEIN künftiger Erfolg behauptet.
/// </summary>
public static class PairedBootstrap
{
    public static PairedBootstrapResult Run(
        IReadOnlyList<IReadOnlyList<double>> series,
        IReadOnlyList<string> names,
        MonteCarloOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(options);
        if (series.Count != names.Count) throw new ArgumentException("Zu jeder Reihe gehört ein Name.", nameof(names));
        if (series.Count < 2) throw new ArgumentException("Ein gepaarter Vergleich verlangt mindestens zwei Reihen.", nameof(series));

        int n = series[0].Count;
        foreach (var s in series)
            if (s.Count != n)
                throw new ArgumentException("Gemeinsames Resampling verlangt gleich lange, zeitlich ausgerichtete Reihen.", nameof(series));
        if (n == 0) throw new ArgumentException("Leere Renditereihen — kein Vergleich möglich.", nameof(series));

        int horizon = options.Horizon is > 0 ? options.Horizon.Value : n;
        int blockLength = options.BlockLength ?? Resampling.DefaultBlockLength(Math.Max(1, n));
        int m = series.Count;
        double initial = options.InitialCapital;

        // Ein einziger RNG, aus dem Seed; die gesamte Pfadfolge ist damit reproduzierbar und von der
        // Reihenfolge der Reihen unabhängig (BuildIndexPath hängt nur an method/n/horizon/blockLength/rng).
        var rng = new DeterministicRng(options.Seed);

        var finals = new List<double>[m];
        var dds = new List<double>[m];
        var streaks = new List<double>[m];
        var below = new int[m];
        var breach = new int[m];
        for (int k = 0; k < m; k++) { finals[k] = new(options.Iterations); dds[k] = new(options.Iterations); streaks[k] = new(options.Iterations); }

        // Kanonische, von der Eingabereihenfolge unabhängige Paarliste: (i<j) nach NAME sortiert.
        var order = Enumerable.Range(0, m).OrderBy(k => names[k], StringComparer.Ordinal).ToArray();
        var pairs = new List<(int Left, int Right)>();
        for (int a = 0; a < m; a++)
            for (int b = a + 1; b < m; b++)
                pairs.Add((order[a], order[b]));
        var deltaFinals = pairs.Select(_ => new List<double>(options.Iterations)).ToList();
        var leftBeats = new int[pairs.Count];
        var ties = new int[pairs.Count];

        int completed = 0;
        var finalThisRep = new double[m];
        for (int it = 0; it < options.Iterations; it++)
        {
            ct.ThrowIfCancellationRequested();
            var path = Resampling.BuildIndexPath(options.Method, n, rng, horizon, blockLength);
            var samples = Resampling.ApplyJointly(series, path);
            for (int k = 0; k < m; k++)
            {
                var (final, dd, streak, breached) = MonteCarloEngine.Simulate(samples[k], options);
                finals[k].Add(final); dds[k].Add(dd); streaks[k].Add(streak);
                if (final < initial) below[k]++;
                if (breached) breach[k]++;
                finalThisRep[k] = final;
            }
            for (int p = 0; p < pairs.Count; p++)
            {
                double d = finalThisRep[pairs[p].Left] - finalThisRep[pairs[p].Right];
                deltaFinals[p].Add(d);
                if (d > 0) leftBeats[p]++;
                else if (d == 0) ties[p]++;
            }
            completed++;
            if (progress is not null && (it & 63) == 0) progress.Report((it + 1) / (double)options.Iterations);
        }
        progress?.Report(1.0);

        var assumptions = new List<string>
        {
            $"Verfahren: gepaarter {MethodLabel(options.Method)} über {n} ausgerichtete OOS-Beobachtungen, " +
            $"{completed} Replikationen, Blocklänge {blockLength}, Horizont {horizon}, Seed {options.Seed}.",
            "Gepaart: In jeder Replikation wurde DERSELBE Index-/Blockpfad auf alle Strategien (und die Benchmark) " +
            "angewendet — die Differenzen vergleichen die Strategien auf identisch umsortierter Zeitstruktur.",
            "Kennzahlen und Verteilungen stammen aus ALLEN Replikationen; eine etwaige Pfad-/Fächergrenze betrifft nur die Anzeige.",
            "Szenarien unter der Resampling-Annahme — KEINE Wahrscheinlichkeit künftigen Erfolgs, kein Nachweis, den S&P 500 zu schlagen."
        };

        var perSeries = new List<MonteCarloResult>(m);
        for (int k = 0; k < m; k++)
            perSeries.Add(new MonteCarloResult
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

        var diffs = new List<PairedBootstrapDifference>(pairs.Count);
        for (int p = 0; p < pairs.Count; p++)
        {
            int li = pairs[p].Left, ri = pairs[p].Right;
            var deltaReturns = deltaFinals[p].Select(d => initial != 0 ? d / initial : 0.0).ToList();
            diffs.Add(new PairedBootstrapDifference
            {
                LeftName = names[li],
                RightName = names[ri],
                FinalCapitalDelta = MonteCarloDistribution.From("final_delta",
                    $"Endkapital-Differenz — {names[li]} − {names[ri]}", "currency", deltaFinals[p]),
                ReturnDelta = MonteCarloDistribution.From("return_delta",
                    $"Endrendite-Differenz — {names[li]} − {names[ri]}", "fraction", deltaReturns),
                ShareLeftBeatsRight = completed == 0 ? double.NaN : (double)leftBeats[p] / completed,
                ShareTie = completed == 0 ? double.NaN : (double)ties[p] / completed
            });
        }

        return new PairedBootstrapResult
        {
            PerSeries = perSeries,
            Differences = diffs,
            Assumptions = assumptions,
            CompletedIterations = completed,
            ObservationCount = n,
            EffectiveBlockLength = blockLength,
            EffectiveHorizon = horizon
        };
    }

    private static string MethodLabel(ResamplingMethod m) => m switch
    {
        ResamplingMethod.Permutation => "Trade-/Reihenfolge-Permutation",
        ResamplingMethod.MovingBlock => "Moving-Block-Bootstrap",
        ResamplingMethod.Stationary => "stationärer Bootstrap",
        _ => m.ToString()
    };
}
