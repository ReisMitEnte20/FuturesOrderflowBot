using TradingBot.Quant.MonteCarlo;
using TradingBot.Quant.Series;

namespace TradingBot.DevDashboard.Services.Quant;

/// <summary>
/// Baut den gemeinsamen Mehrstrategie-Vergleich aus den fertigen Familien-Läufen EINER Vergleichs-Kampagne.
/// Kern: die verketteten Netto-OOS-Renditen jeder Familie werden auf die GEMEINSAME Zeitachse ausgerichtet
/// (innere Verknüpfung, keine stillen Null-Renditen) und mit dem gepaarten Block-Bootstrap verglichen —
/// pro Replikation derselbe Index-/Blockpfad auf alle Familien. Es werden die AUSWAHL-VERFAHREN der Familien
/// verglichen (Kandidatenwahl nur im Training, Auswertung im Test), nicht die beste Parameterwahl auf allen
/// Testdaten. Kein Gesamt-Score, kein Erfolgsversprechen.
/// </summary>
public static class ResearchComparisonBuilder
{
    public static ResearchComparison Build(IReadOnlyList<ResearchRunRecord> familyRuns)
    {
        ArgumentNullException.ThrowIfNull(familyRuns);

        // Nur Familien mit erfolgreichem Walk-forward und nicht-leerer OOS-Reihe können verglichen werden.
        var eligible = familyRuns
            .Where(r => r.WalkForward is { Ok: true } wf && wf.OosT.Count > 0 && wf.OosEquity.Count == wf.OosT.Count)
            .OrderBy(r => r.FamilyKey ?? r.RunId, StringComparer.Ordinal)
            .ToList();

        var notes = new List<string>();
        if (eligible.Count < 2)
            return new ResearchComparison
            {
                Available = false,
                UnavailableReason = eligible.Count == 0
                    ? "Noch keine Familie mit auswertbarer Out-of-Sample-Reihe."
                    : "Für einen gemeinsamen Vergleich werden mindestens zwei Familien mit OOS-Reihe benötigt.",
                Families = familyRuns.Select(r => RowShell(r)).ToList()
            };

        // Erste Familie legt die Grundparameter des Vergleichs fest (alle Familien teilen sie per Konstruktion).
        var cfg = eligible[0].Config;
        int seed = cfg.Seed;
        int iterations = Math.Clamp(cfg.MonteCarloIterations, 1, 100_000);
        var method = ParseMethod(cfg.MonteCarloMethod);
        double initialCapital = (double)cfg.Run.InitialBalance;

        string frequency = string.IsNullOrWhiteSpace(cfg.Options?.Frequency) ? "Bar" : cfg.Options!.Frequency;
        int minObs = cfg.MinComparisonObservations > 0 ? cfg.MinComparisonObservations : 20;

        // OOS-Reihen je Familie als INTERVALLE rekonstruieren: pro Punkt (StartAnker, Ende, Rendite).
        // Die Ausrichtung erfolgt NICHT nur über den Endzeitpunkt — zwei Renditen werden nur dann gepaart,
        // wenn sie DASSELBE Zeitintervall abdecken (gleicher Start UND gleiches Ende). So können zwei gleiche
        // Endzeitstempel mit unterschiedlichen Startankern nie ungeprüft gemeinsam resamplet werden.
        var keys = eligible.Select(r => r.FamilyKey ?? r.RunId).ToList();
        var oos = keys.Select((k, i) => ReconstructOosIntervals(eligible[i].WalkForward!)).ToList();

        // Endzeitpunkte, die in ALLEN Familien vorkommen.
        HashSet<long>? commonEnds = null;
        foreach (var s in oos)
        {
            var ends = new HashSet<long>(s.Keys);
            commonEnds = commonEnds is null ? ends : new HashSet<long>(commonEnds.Intersect(ends));
        }
        var candidateEnds = (commonEnds ?? new HashSet<long>()).OrderBy(t => t).ToList();

        // Nur Endzeitpunkte behalten, an denen ALLE Familien denselben Startanker haben (identisches Intervall).
        var keptEnds = new List<long>();
        int excludedMismatch = 0;
        foreach (var end in candidateEnds)
        {
            long? anchor = oos[0][end].StartT;
            bool sameInterval = oos.All(s => Nullable.Equals(s[end].StartT, anchor));
            if (sameInterval) keptEnds.Add(end);
            else excludedMismatch++;
        }

        // Fehlende Perioden je Familie dokumentieren (kein stiller Null-Fill, keine Renditeberechnung über Lücken).
        for (int i = 0; i < eligible.Count; i++)
        {
            int missing = oos[i].Count - keptEnds.Count;
            if (missing > 0)
                notes.Add($"Familie {DisplayName(eligible[i])}: {missing} von {oos[i].Count} OOS-Perioden nicht im gemeinsamen, intervallgleichen Vergleich (ausgeschlossen, nicht als Nullrendite behandelt).");
        }
        if (excludedMismatch > 0)
            notes.Add($"{excludedMismatch} gemeinsame Endzeitpunkte hatten uneinheitliche Startanker (z. B. Fold-/Warmup-/Lückenränder) und wurden ausgeschlossen — nur intervallgleiche Renditen werden verglichen.");

        int common = keptEnds.Count;
        if (common < 2)
            return new ResearchComparison
            {
                Available = false,
                UnavailableReason = common == 0
                    ? "Die Familien haben keine intervallgleichen gemeinsamen OOS-Perioden (unterschiedliche Fenster/Daten)."
                    : "Zu wenige intervallgleiche gemeinsame OOS-Perioden für einen gepaarten Vergleich.",
                CommonObservations = common, Frequency = frequency, MinObservations = minObs,
                Sufficient = false, ExcludedIntervalMismatch = excludedMismatch,
                Families = familyRuns.Select(r => RowShell(r)).ToList(),
                Notes = notes
            };

        // Ausgerichtete Renditereihen (Originalrenditen der jeweiligen Perioden, NICHT aus einem gefilterten
        // Equity-Array neu über Lücken hinweg berechnet).
        var alignedByKey = keys.Select((k, i) => (k, ret: keptEnds.Select(e => oos[i][e].Return).ToList())).ToList();
        var axisT = keptEnds;

        bool sufficient = common >= minObs;
        int blockLength = cfg.BlockLength ?? Resampling.DefaultBlockLength(Math.Max(1, common));
        var options = new MonteCarloOptions
        {
            Method = method,
            Accumulation = AccumulationMode.Multiplicative, // OOS-Reihen sind Renditen
            Iterations = iterations,
            Seed = seed,
            BlockLength = cfg.BlockLength,
            InitialCapital = initialCapital,
            CapitalBarrier = cfg.CapitalBarrier
        };

        var paired = PairedBootstrap.Run(
            alignedByKey.Select(x => (IReadOnlyList<double>)x.ret).ToList(),
            keys, options);

        var perSeriesByName = paired.PerSeries.ToDictionary(p => ExtractName(p.FinalCapital.Label), p => p);
        var rows = new List<ComparisonFamilyRow>(eligible.Count);
        for (int i = 0; i < eligible.Count; i++)
        {
            var run = eligible[i];
            string key = keys[i];
            var wf = run.WalkForward!;
            var mc = perSeriesByName.TryGetValue(key, out var m) ? m : null;

            rows.Add(new ComparisonFamilyRow
            {
                FamilyKey = key,
                FamilyName = DisplayName(run),
                StrategyId = run.Config.Run.Strategy,
                RunId = run.RunId,
                Status = run.Status,
                Candidates = run.Config.Candidates.Count,
                SelectedCandidate = MostSelected(wf) ?? run.HoldoutProposal?.CandidateReference,
                OosObservations = wf.OosT.Count,
                OosReturn = wf.OosEquity.Count > 0 ? wf.OosEquity[^1] - 1.0 : (double?)null,
                OosSharpe = MetricValue(wf, "sharpe"),
                OosMaxDrawdown = MetricValue(wf, "max_drawdown"),
                McMedianFinal = mc?.FinalCapital.Median,
                McP5Final = mc?.FinalCapital.P5,
                McP95Final = mc?.FinalCapital.P95,
                McShareBelowStart = mc?.ShareOfRunsBelowStart,
                McMedianMaxDrawdown = mc?.MaxDrawdown.Median,
                McMedianLosingStreak = mc?.LongestLosingStreak.Median,
                Pbo = run.Overfitting?.Pbo,
                Psr = run.Overfitting?.Psr,
                Dsr = run.Overfitting?.Dsr,
                SharedOosEquity = NormalizedEquity(alignedByKey[i].ret, initialCapital)
            });
        }

        var diffs = paired.Differences.Select(d => new ComparisonDifference
        {
            Left = NameForKey(eligible, d.LeftName),
            Right = NameForKey(eligible, d.RightName),
            DeltaMedianFinal = d.FinalCapitalDelta.Median,
            DeltaP5Final = d.FinalCapitalDelta.P5,
            DeltaP95Final = d.FinalCapitalDelta.P95,
            ShareLeftBeatsRight = d.ShareLeftBeatsRight,
            ShareTie = d.ShareTie
        }).ToList();

        notes.Add("Verglichen werden die AUSWAHL-VERFAHREN der Familien auf identischen Fenstern (Auswahl nur im Training, Auswertung im Test), nicht die beste Parameterwahl auf allen Testdaten.");
        notes.Add("Benchmark: im gemeinsamen gepaarten Bootstrap (noch) nicht enthalten — je Familie separat im Detail ausgewiesen.");

        string? insufficientReason = sufficient ? null :
            $"Nur {common} intervallgleiche gemeinsame OOS-{(frequency == "Bar" ? "Bar-" : frequency + "-")}Perioden (Konvention: mindestens {minObs}). " +
            "Für diese Datengrundlage KEIN Sieger, keine Erfolgswahrscheinlichkeit und kein positiver Freigabestatus — die Zahlen zeigen nur die Technik. " +
            "Mehr Bootstrap-Replikationen erhöhen NICHT die Zahl historischer Beobachtungen.";
        if (!sufficient) notes.Insert(0, insufficientReason!);

        return new ResearchComparison
        {
            Available = true,
            CommonObservations = common,
            Frequency = frequency,
            Sufficient = sufficient,
            MinObservations = minObs,
            InsufficientReason = insufficientReason,
            ExcludedIntervalMismatch = excludedMismatch,
            FromT = axisT.Count > 0 ? axisT[0] : null,
            ToT = axisT.Count > 0 ? axisT[^1] : null,
            AxisT = axisT,
            InitialCapital = initialCapital,
            Method = MethodLabel(method),
            Iterations = paired.CompletedIterations,
            Seed = seed,
            BlockLength = paired.EffectiveBlockLength,
            Families = rows,
            Differences = diffs,
            Assumptions = paired.Assumptions,
            Notes = notes
        };
    }

    private static ComparisonFamilyRow RowShell(ResearchRunRecord r) => new()
    {
        FamilyKey = r.FamilyKey ?? r.RunId,
        FamilyName = DisplayName(r),
        StrategyId = r.Config.Run.Strategy,
        RunId = r.RunId,
        Status = r.Status,
        Candidates = r.Config.Candidates.Count,
        OosObservations = r.WalkForward?.OosT.Count ?? 0
    };

    /// <summary>
    /// Rekonstruiert je OOS-Punkt (Endzeitpunkt → Startanker + Rendite) aus <c>OosT</c>/<c>OosStartT</c>/<c>OosEquity</c>.
    /// Die Rendite ist die ORIGINAL-Periodenrendite (aus der kumulierten OOS-Equity: <c>eq[i]/eq[i-1]-1</c>, für i=0
    /// gegen den Startindex 1,0) — sie wird NICHT aus einem gefilterten Array über ausgelassene Abschnitte hinweg neu
    /// gebildet. Der Startanker stammt aus <c>OosStartT</c> (explizit mitgeführtes Intervall); fehlt er (Alt-Ergebnis
    /// ohne Startzeiten), wird der vorherige Endzeitpunkt als Anker genutzt (i=0 → null = Reihen-/Fold-Anfang).
    /// </summary>
    private static Dictionary<long, (long? StartT, double Return)> ReconstructOosIntervals(QuantWalkForwardResponse wf)
    {
        var map = new Dictionary<long, (long?, double)>();
        var end = wf.OosT;
        var eq = wf.OosEquity;
        var start = wf.OosStartT;
        for (int i = 0; i < end.Count && i < eq.Count; i++)
        {
            double ret = i == 0 ? eq[0] - 1.0 : (eq[i - 1] != 0 ? eq[i] / eq[i - 1] - 1.0 : 0.0);
            long? anchor = start is not null && i < start.Count
                ? start[i]
                : (i > 0 ? end[i - 1] : (long?)null);
            map[end[i]] = (anchor, ret);   // gleicher Endzeitpunkt doppelt = letzter gewinnt (in der Praxis eindeutig)
        }
        return map;
    }

    private static IReadOnlyList<double> NormalizedEquity(IReadOnlyList<double> returns, double initial)
    {
        var eq = new double[returns.Count + 1];
        eq[0] = initial;
        double c = initial;
        for (int i = 0; i < returns.Count; i++) { c *= 1.0 + returns[i]; eq[i + 1] = c; }
        return eq;
    }

    private static string? MostSelected(QuantWalkForwardResponse wf)
    {
        var counts = wf.Folds.Where(f => !string.IsNullOrEmpty(f.SelectedCandidate))
            .GroupBy(f => f.SelectedCandidate!)
            .Select(g => (Id: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToList();
        return counts.Count == 0 ? null : $"{counts[0].Id} ({counts[0].Count}/{wf.Folds.Count} Fenster)";
    }

    private static double? MetricValue(QuantWalkForwardResponse wf, string key)
        => wf.OosMetrics.FirstOrDefault(m => m.Key == key)?.Value;

    private static string DisplayName(ResearchRunRecord r)
        => r.FamilyName ?? r.Config.Run.Strategy;

    private static string NameForKey(IReadOnlyList<ResearchRunRecord> runs, string key)
        => runs.FirstOrDefault(r => (r.FamilyKey ?? r.RunId) == key) is { } m ? DisplayName(m) : key;

    // Der PairedBootstrap-Label ist "Endkapital — <name>"; hier den Namen zurückgewinnen.
    private static string ExtractName(string label)
    {
        int i = label.IndexOf('—');
        return i >= 0 ? label[(i + 1)..].Trim() : label.Trim();
    }

    private static ResamplingMethod ParseMethod(string? m) => (m ?? "").Trim().ToLowerInvariant() switch
    {
        "permutation" => ResamplingMethod.Permutation,
        "stationary" => ResamplingMethod.Stationary,
        _ => ResamplingMethod.MovingBlock, // Vergleich standardmäßig über Block-Bootstrap (Zeitstruktur erhalten)
    };

    private static string MethodLabel(ResamplingMethod m) => m switch
    {
        ResamplingMethod.Permutation => "Trade-/Reihenfolge-Permutation",
        ResamplingMethod.MovingBlock => "Moving-Block-Bootstrap",
        ResamplingMethod.Stationary => "stationärer Bootstrap",
        _ => m.ToString()
    };
}
