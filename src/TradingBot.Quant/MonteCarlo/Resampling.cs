namespace TradingBot.Quant.MonteCarlo;

/// <summary>Resampling-Verfahren. Jedes zerstört bestimmte Strukturen — das ist bei der Deutung entscheidend.</summary>
public enum ResamplingMethod
{
    /// <summary>
    /// Permutation ohne Zurücklegen: dieselben Beobachtungen in anderer Reihenfolge.
    /// AUSDRÜCKLICH NUR eine Reihenfolgeanalyse — die Summe bleibt unverändert, es variiert nur der Pfad
    /// (und damit Drawdown/Verlustserien). Serielle Abhängigkeit wird vollständig zerstört.
    /// </summary>
    Permutation = 0,

    /// <summary>
    /// Moving-Block-Bootstrap: Ziehen von Blöcken FESTER Länge mit Zurücklegen. Erhält Abhängigkeiten
    /// innerhalb eines Blocks, zerstört sie an den Blockgrenzen.
    /// </summary>
    MovingBlock = 1,

    /// <summary>
    /// Stationärer Bootstrap (Politis/Romano): geometrisch verteilte Blocklängen mit Erwartungswert L.
    /// Die resampelte Reihe bleibt stationär; keine feste Blockgrenze.
    /// </summary>
    Stationary = 2
}

/// <summary>
/// Erzeugt Index-PFADE für das Resampling. Der Pfad wird getrennt von den Daten erzeugt, damit
/// mehrere Reihen (z. B. Strategie und Benchmark, oder mehrere Strategien) mit DEMSELBEN Pfad
/// resampelt werden können. Nur so bleiben die Abhängigkeiten zwischen den Reihen erhalten.
/// </summary>
public static class Resampling
{
    /// <summary>Index-Pfad der Länge <paramref name="horizon"/> für die gewählte Methode.</summary>
    /// <param name="n">Länge der Originalreihe.</param>
    /// <param name="horizon">Gewünschte Länge des Pfads (Standard: n).</param>
    /// <param name="blockLength">Block- bzw. mittlere Blocklänge (nur bei Bootstrap-Verfahren).</param>
    public static int[] BuildIndexPath(ResamplingMethod method, int n, DeterministicRng rng,
        int? horizon = null, int? blockLength = null)
    {
        ArgumentNullException.ThrowIfNull(rng);
        if (n <= 0) return Array.Empty<int>();
        int h = horizon is > 0 ? horizon.Value : n;

        return method switch
        {
            ResamplingMethod.Permutation => Permutation(n, h, rng),
            ResamplingMethod.MovingBlock => MovingBlock(n, h, blockLength ?? DefaultBlockLength(n), rng),
            ResamplingMethod.Stationary => Stationary(n, h, blockLength ?? DefaultBlockLength(n), rng),
            _ => throw new ArgumentOutOfRangeException(nameof(method))
        };
    }

    /// <summary>
    /// Übliche Faustregel für die Blocklänge: L ≈ n^(1/3), mindestens 1. Bewusst als Vorgabe und nicht
    /// als „optimaler" Wert — die Blocklänge ist eine Annahme und wird im Ergebnis mit ausgewiesen.
    /// </summary>
    public static int DefaultBlockLength(int n) => Math.Max(1, (int)Math.Round(Math.Pow(n, 1.0 / 3.0)));

    private static int[] Permutation(int n, int horizon, DeterministicRng rng)
    {
        var idx = Enumerable.Range(0, n).ToList();
        rng.Shuffle(idx);
        if (horizon == n) return idx.ToArray();

        // Für Horizonte ≠ n wird die Permutation zyklisch fortgesetzt bzw. abgeschnitten.
        var path = new int[horizon];
        for (int i = 0; i < horizon; i++) path[i] = idx[i % n];
        return path;
    }

    private static int[] MovingBlock(int n, int horizon, int blockLength, DeterministicRng rng)
    {
        int L = Math.Clamp(blockLength, 1, n);
        int starts = n - L + 1;
        var path = new int[horizon];
        int pos = 0;
        while (pos < horizon)
        {
            int start = rng.NextInt(starts);
            for (int k = 0; k < L && pos < horizon; k++, pos++) path[pos] = start + k;
        }
        return path;
    }

    private static int[] Stationary(int n, int horizon, int meanBlockLength, DeterministicRng rng)
    {
        int L = Math.Max(1, meanBlockLength);
        double p = 1.0 / L;
        var path = new int[horizon];
        int cur = rng.NextInt(n);
        for (int i = 0; i < horizon; i++)
        {
            path[i] = cur;
            cur = rng.NextDouble() < p ? rng.NextInt(n) : (cur + 1) % n;
        }
        return path;
    }

    /// <summary>Wendet einen Index-Pfad auf eine Reihe an.</summary>
    public static double[] Apply(IReadOnlyList<double> series, IReadOnlyList<int> path)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(path);
        var outp = new double[path.Count];
        for (int i = 0; i < path.Count; i++) outp[i] = series[path[i]];
        return outp;
    }

    /// <summary>
    /// Wendet DENSELBEN Pfad auf mehrere gleich lange Reihen an (gemeinsames Resampling).
    /// Erforderlich, sobald mehrere Strategien oder eine Strategie plus Benchmark gemeinsam
    /// betrachtet werden: getrenntes Resampling würde deren Abhängigkeit zerstören.
    /// </summary>
    public static IReadOnlyList<double[]> ApplyJointly(IReadOnlyList<IReadOnlyList<double>> series, IReadOnlyList<int> path)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (series.Count == 0) return Array.Empty<double[]>();
        int n = series[0].Count;
        foreach (var s in series)
            if (s.Count != n)
                throw new ArgumentException("Für gemeinsames Resampling müssen alle Reihen gleich lang und zeitlich ausgerichtet sein.", nameof(series));
        return series.Select(s => Apply(s, path)).ToList();
    }
}
