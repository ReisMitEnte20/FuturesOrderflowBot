using TradingBot.Quant.Statistics;

namespace TradingBot.Quant.Overfitting;

/// <summary>Ergebnis einer einzelnen CSCV-Kombination.</summary>
public sealed record CscvTrial
{
    public int Index { get; init; }
    /// <summary>Blöcke, die das Training bilden (die übrigen bilden den Test).</summary>
    public required IReadOnlyList<int> TrainBlocks { get; init; }
    /// <summary>Im Training bester Kandidat (Spaltenindex).</summary>
    public int SelectedColumn { get; init; }
    public double TrainPerformance { get; init; }
    public double TestPerformance { get; init; }
    /// <summary>Mittlerer Rang des gewählten Kandidaten im Test: 1 = schlechtester, N = bester.</summary>
    public double TestRank { get; init; }
    /// <summary>Relativer Rang ω = Rang/(N+1) in (0,1).</summary>
    public double RelativeRank { get; init; }
    /// <summary>Logit λ = ln(ω/(1−ω)). λ &lt; 0 bedeutet: unterdurchschnittlich out-of-sample.</summary>
    public double Logit { get; init; }
}

/// <summary>Ergebnis der PBO-Schätzung nach CSCV.</summary>
public sealed record PboResult
{
    /// <summary>Wahrscheinlichkeit für Backtest-Overfitting: Anteil der Kombinationen mit λ &lt; 0.</summary>
    public double? Pbo { get; init; }

    public int Observations { get; init; }
    public int Candidates { get; init; }
    public int Blocks { get; init; }
    public int Combinations { get; init; }

    public IReadOnlyList<CscvTrial> Trials { get; init; } = Array.Empty<CscvTrial>();

    /// <summary>In-Sample- gegen Out-of-Sample-Performance des jeweils gewählten Kandidaten (für das Streudiagramm).</summary>
    public IReadOnlyList<(double InSample, double OutOfSample)> Pairs { get; init; } = Array.Empty<(double, double)>();

    /// <summary>Anteil der Kombinationen, in denen der gewählte Kandidat out-of-sample Verlust macht (Performance &lt; 0).</summary>
    public double? ShareNegativeOutOfSample { get; init; }

    /// <summary>Spalten, die wegen ungültiger Werte ausgeschlossen wurden.</summary>
    public IReadOnlyList<int> ExcludedColumns { get; init; } = Array.Empty<int>();

    public required IReadOnlyList<string> Definitions { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public bool Available => Pbo.HasValue;
    public string? UnavailableReason { get; init; }
}

/// <summary>
/// Probability of Backtest Overfitting (PBO) über Combinatorially Symmetric Cross-Validation (CSCV)
/// nach Bailey, Borwein, López de Prado und Zhu, „The Probability of Backtest Overfitting".
///
/// Verfahren:
/// 1. Die Renditematrix M (T Perioden × N Kandidaten) wird in S disjunkte, gleich große Zeitblöcke
///    geteilt (S gerade).
/// 2. Für jede der C(S, S/2) Kombinationen bilden S/2 Blöcke das Training, die übrigen den Test.
/// 3. Im Training wird der beste Kandidat n* nach der Vorauswahlmetrik bestimmt.
/// 4. Im Test wird der Rang von n* unter allen N Kandidaten bestimmt; daraus ω = Rang/(N+1)
///    und λ = ln(ω/(1−ω)).
/// 5. PBO = Anteil der Kombinationen mit λ &lt; 0, also: wie oft landet die im Training beste
///    Variante out-of-sample unterhalb des Medians.
///
/// Ausdrücklich festgelegt:
/// - Vorauswahlmetrik: frei wählbar, Standard ist der Sharpe je Periode (Mittelwert/Standardabweichung, n−1).
/// - Rangrichtung: höher ist besser; Rang 1 = schlechtester Kandidat.
/// - Gleichstände: mittlere Ränge (Mid-Rank), damit identische Kandidaten nicht künstlich getrennt werden.
/// - Blockaufteilung: zusammenhängende Zeitblöcke gleicher Länge; überzählige Perioden am ENDE
///   werden verworfen und im Ergebnis vermerkt (kein Auffüllen).
/// - Fehlende/ungültige Werte: Spalten mit nicht endlichen Werten werden vollständig ausgeschlossen
///   und ausgewiesen. Es wird nichts ersetzt.
/// </summary>
public static class CscvPbo
{
    /// <summary>Standard-Vorauswahlmetrik: Sharpe je Periode ohne Annualisierung.</summary>
    public static double? SharpePerPeriod(IReadOnlyList<double> x)
    {
        var sd = Stats.StdDevSample(x);
        // Rechnerisch konstante Reihen liefern keinen Sharpe, sondern „nicht definiert".
        if (sd is null || Stats.IsNegligibleDeviation(sd.Value, Stats.MaxAbs(x))) return null;
        return Stats.Mean(x) / sd.Value;
    }

    /// <param name="matrix">Renditematrix: matrix[t][n] = Rendite der Periode t für Kandidat n. Alle Zeilen gleich lang.</param>
    /// <param name="blocks">Anzahl Zeitblöcke S (gerade, ≥ 2). Üblich: 16.</param>
    /// <param name="performance">Vorauswahlmetrik je Kandidat auf einer Teilmenge der Perioden.</param>
    public static PboResult Compute(
        IReadOnlyList<IReadOnlyList<double>> matrix,
        int blocks = 16,
        Func<IReadOnlyList<double>, double?>? performance = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        var perf = performance ?? SharpePerPeriod;
        var notes = new List<string>();

        var definitions = new List<string>
        {
            "Vorauswahlmetrik: " + (performance is null ? "Sharpe je Periode (Mittelwert / Stichproben-Standardabweichung)" : "vom Aufrufer gestellt"),
            "Rangrichtung: höher ist besser (Rang 1 = schlechtester Kandidat).",
            "Gleichstände: mittlere Ränge.",
            "Blockaufteilung: zusammenhängende, gleich lange Zeitblöcke; Rest am Ende wird verworfen.",
            "Ungültige Werte: betroffene Kandidatenspalten werden ausgeschlossen, nichts wird ersetzt.",
            "PBO = Anteil der Kombinationen, in denen der in-sample beste Kandidat out-of-sample unter dem Median liegt."
        };

        int t = matrix.Count;
        if (t == 0)
            return Unavailable("Leere Renditematrix.", definitions, notes);

        int n = matrix[0].Count;
        for (int i = 0; i < t; i++)
            if (matrix[i].Count != n)
                throw new ArgumentException("Alle Zeilen der Renditematrix müssen gleich viele Kandidaten enthalten.", nameof(matrix));

        if (n < 2)
            return Unavailable($"Nur {n} Kandidat(en) — PBO braucht mehrere vergleichbare Kandidaten.", definitions, notes);
        if (blocks < 2 || blocks % 2 != 0)
            return Unavailable($"Blockzahl S = {blocks} ist ungültig: S muss gerade und ≥ 2 sein.", definitions, notes);
        if (t < blocks * 2)
            return Unavailable($"Zu wenige Perioden: {t} bei S = {blocks} (mindestens 2 Perioden je Block nötig).", definitions, notes);

        // --- Spalten mit ungültigen Werten ausschließen ---
        var excluded = new List<int>();
        var keep = new List<int>();
        for (int c = 0; c < n; c++)
        {
            bool ok = true;
            for (int i = 0; i < t && ok; i++)
            {
                double v = matrix[i][c];
                if (double.IsNaN(v) || double.IsInfinity(v)) ok = false;
            }
            if (ok) keep.Add(c); else excluded.Add(c);
        }
        if (excluded.Count > 0)
            notes.Add($"{excluded.Count} von {n} Kandidatenspalten enthielten ungültige Werte und wurden ausgeschlossen.");
        if (keep.Count < 2)
            return Unavailable("Nach dem Ausschluss ungültiger Spalten bleiben weniger als 2 Kandidaten übrig.",
                definitions, notes, excluded);

        // --- Identische Kandidaten melden (sie verzerren die Rangverteilung) ---
        int duplicates = CountDuplicateColumns(matrix, keep);
        if (duplicates > 0)
            notes.Add($"{duplicates} Kandidatenspalte(n) sind mit einer anderen identisch. " +
                      "Identische Varianten sind keine unabhängigen Versuche und drücken die Aussagekraft von PBO.");

        // --- Blockaufteilung ---
        int blockSize = t / blocks;
        int dropped = t - blockSize * blocks;
        if (dropped > 0) notes.Add($"{dropped} Periode(n) am Ende passen in keinen vollständigen Block und wurden verworfen.");

        var blockRows = new List<int[]>(blocks);
        for (int b = 0; b < blocks; b++)
            blockRows.Add(Enumerable.Range(b * blockSize, blockSize).ToArray());

        // --- Alle Kombinationen C(S, S/2) ---
        var trials = new List<CscvTrial>();
        var pairs = new List<(double, double)>();
        int half = blocks / 2;
        int below = 0, negativeOos = 0, counted = 0;
        int combinationIndex = 0;

        foreach (var trainBlocks in Combinations(blocks, half))
        {
            ct.ThrowIfCancellationRequested();
            var trainSet = new HashSet<int>(trainBlocks);
            var trainRows = new List<int>();
            var testRows = new List<int>();
            for (int b = 0; b < blocks; b++)
                (trainSet.Contains(b) ? trainRows : testRows).AddRange(blockRows[b]);

            var trainPerf = new double?[keep.Count];
            var testPerf = new double?[keep.Count];
            for (int k = 0; k < keep.Count; k++)
            {
                trainPerf[k] = perf(Column(matrix, keep[k], trainRows));
                testPerf[k] = perf(Column(matrix, keep[k], testRows));
            }

            int best = -1;
            double bestVal = double.NegativeInfinity;
            for (int k = 0; k < keep.Count; k++)
                if (trainPerf[k] is { } v && v > bestVal) { bestVal = v; best = k; }

            combinationIndex++;
            if (best < 0) continue;                       // kein Kandidat mit gültiger Trainingsmetrik
            if (testPerf[best] is not { } oos) continue;  // gewählter Kandidat im Test nicht bewertbar

            var finiteTest = testPerf.Select(v => v ?? double.NegativeInfinity).ToArray();
            double rank = MidRank(finiteTest, best);
            double omega = rank / (keep.Count + 1.0);
            double logit = Math.Log(omega / (1.0 - omega));

            trials.Add(new CscvTrial
            {
                Index = combinationIndex - 1,
                TrainBlocks = trainBlocks.ToArray(),
                SelectedColumn = keep[best],
                TrainPerformance = bestVal,
                TestPerformance = oos,
                TestRank = rank,
                RelativeRank = omega,
                Logit = logit
            });
            pairs.Add((bestVal, oos));
            if (logit < 0) below++;
            if (oos < 0) negativeOos++;
            counted++;
        }

        if (counted == 0)
            return Unavailable("Keine Kombination lieferte auswertbare Werte (Vorauswahlmetrik überall undefiniert).",
                definitions, notes, excluded);

        notes.Add($"{counted} von {combinationIndex} Kombinationen waren auswertbar.");

        return new PboResult
        {
            Pbo = (double)below / counted,
            Observations = blockSize * blocks,
            Candidates = keep.Count,
            Blocks = blocks,
            Combinations = counted,
            Trials = trials,
            Pairs = pairs,
            ShareNegativeOutOfSample = (double)negativeOos / counted,
            ExcludedColumns = excluded,
            Definitions = definitions,
            Notes = notes
        };
    }

    /// <summary>Mittlerer Rang (1 = kleinster Wert) des Eintrags <paramref name="index"/>; Gleichstände teilen sich den Rang.</summary>
    internal static double MidRank(IReadOnlyList<double> values, int index)
    {
        double v = values[index];
        int less = 0, equal = 0;
        foreach (var x in values)
        {
            if (x < v) less++;
            else if (x == v) equal++;
        }
        // Ränge less+1 .. less+equal, Mittelwert davon:
        return less + (equal + 1) / 2.0;
    }

    private static double[] Column(IReadOnlyList<IReadOnlyList<double>> m, int col, IReadOnlyList<int> rows)
    {
        var outp = new double[rows.Count];
        for (int i = 0; i < rows.Count; i++) outp[i] = m[rows[i]][col];
        return outp;
    }

    private static int CountDuplicateColumns(IReadOnlyList<IReadOnlyList<double>> m, IReadOnlyList<int> cols)
    {
        var seen = new HashSet<string>();
        int dup = 0;
        foreach (var c in cols)
        {
            var key = string.Join(';', Enumerable.Range(0, m.Count).Select(i => m[i][c].ToString("R")));
            if (!seen.Add(key)) dup++;
        }
        return dup;
    }

    /// <summary>Alle k-elementigen Teilmengen von {0..n−1} in lexikografischer Reihenfolge.</summary>
    internal static IEnumerable<int[]> Combinations(int n, int k)
    {
        var idx = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return (int[])idx.Clone();
            int i = k - 1;
            while (i >= 0 && idx[i] == n - k + i) i--;
            if (i < 0) yield break;
            idx[i]++;
            for (int j = i + 1; j < k; j++) idx[j] = idx[j - 1] + 1;
        }
    }

    private static PboResult Unavailable(string reason, IReadOnlyList<string> definitions, IReadOnlyList<string> notes,
        IReadOnlyList<int>? excluded = null) => new()
    {
        Pbo = null,
        UnavailableReason = reason,
        Definitions = definitions,
        Notes = notes,
        ExcludedColumns = excluded ?? Array.Empty<int>()
    };
}
