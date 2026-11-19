using TradingBot.Quant.MonteCarlo;
using TradingBot.Quant.Statistics;

namespace TradingBot.Quant.Report;

/// <summary>Verteilung einer simulierten Größe inkl. Histogramm (für den Bericht).</summary>
public sealed record SimDistribution
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
    public IReadOnlyList<int> Histogram { get; init; } = Array.Empty<int>();
    public double HistogramMin { get; init; }
    public double HistogramMax { get; init; }

    public static SimDistribution From(string key, string label, string unit, List<double> values, int bins = 30)
    {
        // Perzentile über die vorhandene, geprüfte Verteilungsfunktion der Monte-Carlo-Engine.
        var d = MonteCarloDistribution.From(key, label, unit, values);
        var hist = new int[bins];
        double lo = values.Count == 0 ? 0 : d.Min, hi = values.Count == 0 ? 0 : d.Max;
        if (values.Count > 0)
        {
            double w = hi > lo ? (hi - lo) / bins : 1;
            foreach (var v in values)
            {
                int b = hi > lo ? (int)Math.Floor((v - lo) / w) : bins / 2;
                hist[Math.Clamp(b, 0, bins - 1)]++;
            }
        }
        return new SimDistribution
        {
            Key = key, Label = label, Unit = unit,
            Min = d.Min, P5 = d.P5, P25 = d.P25, Median = d.Median, P75 = d.P75, P95 = d.P95, Max = d.Max, Mean = d.Mean,
            Histogram = hist, HistogramMin = lo, HistogramMax = hi
        };
    }
}

/// <summary>Ergebnis EINES Simulationsverfahrens (eine klar benannte Frage).</summary>
public sealed record SimulationResult
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    /// <summary>Welche Frage dieses Verfahren beantwortet.</summary>
    public required string Question { get; init; }
    public required string Method { get; init; }
    /// <summary>Was die Beobachtungen sind (z. B. "Netto je Trade", "Netto je Handelstag").</summary>
    public required string ObservationUnit { get; init; }
    public int Observations { get; init; }
    public int Iterations { get; init; }
    public int Seed { get; init; }
    public int? BlockLength { get; init; }
    public SimDistribution? FinalNet { get; init; }
    public SimDistribution? MaxDrawdown { get; init; }
    public SimDistribution? MaxDrawdownFraction { get; init; }
    public SimDistribution? LosingStreak { get; init; }
    /// <summary>Anteil der Pfade mit Endergebnis &lt; 0 (Szenarioanteil, KEINE Wahrscheinlichkeit für die Zukunft).</summary>
    public double? ShareLossPaths { get; init; }
    /// <summary>Nur Permutation: wahr, wenn das End-Netto in ALLEN Pfaden identisch zum Original war (Pflichtprüfung).</summary>
    public bool? FinalNetInvariant { get; init; }
    public string? UnavailableReason { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Pfadsimulation auf Geldbeträgen (Netto je Trade bzw. je Handelstag) bei FESTER Positionsgröße. Nutzt die geprüften
/// Index-Pfade aus <see cref="Resampling"/> und den deterministischen Zufallsgenerator — gleicher Seed ⇒ gleiche
/// Ergebnisse. Weil die Kontraktzahl fix ist, ist das Umordnen fertiger Dollar-P&amp;Ls zulässig: kein Trade ändert die
/// Größe eines späteren. (Bei kapitalabhängiger Größe wäre das NICHT zulässig — dieser Bericht verwendet sie nicht.)
/// </summary>
public static class TradeResampling
{
    public const int DefaultIterations = 5000;

    public static SimulationResult Run(
        string key, string title, string question, string observationUnit,
        IReadOnlyList<double> observations, ResamplingMethod method, int? blockLength,
        int iterations, int seed, double initialCapital, CancellationToken ct = default,
        Func<double[], DeterministicRng, double[]>? transform = null, string? methodLabel = null)
    {
        int n = observations.Count;
        string label = methodLabel ?? MethodText(method, blockLength);
        if (n < 2)
            return new SimulationResult
            {
                Key = key, Title = title, Question = question, Method = label, ObservationUnit = observationUnit,
                Observations = n, Iterations = 0, Seed = seed, BlockLength = blockLength,
                UnavailableReason = $"Zu wenige Beobachtungen ({n}) für eine Simulation (mind. 2)."
            };

        var rng = new DeterministicRng(seed);
        int bl = blockLength ?? Resampling.DefaultBlockLength(n);
        var finals = new List<double>(iterations);
        var dds = new List<double>(iterations);
        var ddFrac = new List<double>(iterations);
        var streaks = new List<double>(iterations);
        int lossPaths = 0;
        double original = observations.Sum();
        bool invariant = true;

        for (int it = 0; it < iterations; it++)
        {
            if ((it & 255) == 0) ct.ThrowIfCancellationRequested();
            var path = Resampling.BuildIndexPath(method, n, rng, n, bl);
            var sample = Resampling.Apply(observations, path);
            if (transform is not null) sample = transform(sample, rng);
            double eq = initialCapital, peak = initialCapital, maxDd = 0, maxF = 0;
            int cur = 0, best = 0;
            foreach (var x in sample)
            {
                eq += x;
                if (eq > peak) peak = eq;
                double d = peak - eq;
                if (d > maxDd) maxDd = d;
                if (peak > 0 && d / peak > maxF) maxF = d / peak;
                if (x < 0) { cur++; if (cur > best) best = cur; } else cur = 0;
            }
            double fin = eq - initialCapital;
            finals.Add(fin); dds.Add(maxDd); ddFrac.Add(maxF); streaks.Add(best);
            if (fin < 0) lossPaths++;
            if (Math.Abs(fin - original) > 1e-6 * Math.Max(1, Math.Abs(original))) invariant = false;
        }

        var notes = new List<string>();
        if (n < 30) notes.Add($"Nur {n} Beobachtungen — die Verteilung ist grob und wenig belastbar.");
        notes.Add("Simulierte Perzentile sind Szenarien aus der beobachteten Stichprobe, KEINE Verlustobergrenze.");
        return new SimulationResult
        {
            Key = key, Title = title, Question = question, Method = label, ObservationUnit = observationUnit,
            Observations = n, Iterations = iterations, Seed = seed,
            BlockLength = method == ResamplingMethod.Permutation ? null : bl,
            FinalNet = SimDistribution.From("final_net", "End-Netto", "currency", finals),
            MaxDrawdown = SimDistribution.From("max_dd", "Maximaler Drawdown", "currency", dds),
            MaxDrawdownFraction = SimDistribution.From("max_dd_frac", "Maximaler Drawdown (Anteil vom Hoch)", "fraction", ddFrac),
            LosingStreak = SimDistribution.From("losing_streak", "Längste Verlustserie", "count", streaks),
            ShareLossPaths = lossPaths / (double)iterations,
            FinalNetInvariant = method == ResamplingMethod.Permutation && transform is null ? invariant : null,
            Notes = notes
        };
    }

    /// <summary>
    /// Ausführungsstress „verpasste Fills“: Reihenfolge bleibt erhalten, jeder Trade entfällt unabhängig mit
    /// Wahrscheinlichkeit <paramref name="missShare"/> (hypothetisches Szenario, keine beobachtete Fill-Quote).
    /// </summary>
    public static SimulationResult RunMissedFills(IReadOnlyList<double> observations, double missShare, int iterations, int seed,
        double initialCapital, CancellationToken ct = default)
    {
        int n = observations.Count;
        string q = $"Wie verändern sich Ergebnis und Drawdown, wenn zufällig {missShare:P0} der Einstiege nicht gefüllt werden?";
        if (n < 2)
            return new SimulationResult
            {
                Key = "missed", Title = "Verpasste Fills", Question = q, Method = "Zufälliges Auslassen", ObservationUnit = "Netto je Trade",
                Observations = n, Seed = seed, UnavailableReason = $"Zu wenige Trades ({n})."
            };
        var rng = new DeterministicRng(seed);
        var finals = new List<double>(iterations); var dds = new List<double>(iterations);
        var ddf = new List<double>(iterations); var streaks = new List<double>(iterations);
        int loss = 0;
        for (int it = 0; it < iterations; it++)
        {
            if ((it & 255) == 0) ct.ThrowIfCancellationRequested();
            double eq = initialCapital, peak = initialCapital, maxDd = 0, maxF = 0; int cur = 0, best = 0;
            foreach (var x in observations)
            {
                if (rng.NextDouble() < missShare) continue;
                eq += x; if (eq > peak) peak = eq;
                double d = peak - eq; if (d > maxDd) maxDd = d; if (peak > 0 && d / peak > maxF) maxF = d / peak;
                if (x < 0) { cur++; if (cur > best) best = cur; } else cur = 0;
            }
            double fin = eq - initialCapital;
            finals.Add(fin); dds.Add(maxDd); ddf.Add(maxF); streaks.Add(best); if (fin < 0) loss++;
        }
        return new SimulationResult
        {
            Key = "missed", Title = "Verpasste Fills (Ausführungsstress)", Question = q,
            Method = $"Jeder Trade entfällt unabhängig mit {missShare:P0}; Reihenfolge unverändert", ObservationUnit = "Netto je Trade",
            Observations = n, Iterations = iterations, Seed = seed,
            FinalNet = SimDistribution.From("final_net", "End-Netto", "currency", finals),
            MaxDrawdown = SimDistribution.From("max_dd", "Maximaler Drawdown", "currency", dds),
            MaxDrawdownFraction = SimDistribution.From("max_dd_frac", "Maximaler Drawdown (Anteil vom Hoch)", "fraction", ddf),
            LosingStreak = SimDistribution.From("losing_streak", "Längste Verlustserie", "count", streaks),
            ShareLossPaths = loss / (double)iterations,
            Notes = new[] { "Hypothetisches Szenario: aus OHLC-/Trade-Daten ist keine echte Fill-Quote beobachtbar." }
        };
    }

    public static string MethodText(ResamplingMethod m, int? blockLength) => m switch
    {
        ResamplingMethod.Permutation => "Permutation (Umordnen ohne Zurücklegen)",
        ResamplingMethod.MovingBlock when blockLength == 1 => "Bootstrap mit Zurücklegen (Einzelziehung)",
        ResamplingMethod.MovingBlock => $"Moving-Block-Bootstrap (Blocklänge {blockLength})",
        ResamplingMethod.Stationary => $"Stationärer Bootstrap (mittlere Blocklänge {blockLength})",
        _ => m.ToString()
    };

    /// <summary>Begründete Blocklängen für die Sensitivitätsprüfung: 2, n^(1/3) und 2·n^(1/3) (eindeutig, &lt; n).</summary>
    public static IReadOnlyList<int> BlockLengths(int n)
    {
        int b = Resampling.DefaultBlockLength(Math.Max(1, n));
        return new[] { 2, b, 2 * b }.Where(x => x >= 2 && x < n).Distinct().OrderBy(x => x).ToList();
    }
}
