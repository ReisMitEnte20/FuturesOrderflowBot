namespace TradingBot.Quant.Validation;

/// <summary>
/// Erzeugt zeitlich geordnete Walk-Forward-Aufteilungen (rollend und verankert) mit Purging und
/// Embargo.
///
/// Warum kein Zufallssplit: Finanzzeitreihen sind zeitlich abhängig. Ein zufälliger Split würde
/// Trainingsdaten aus der Zukunft des Tests enthalten und die Ergebnisse systematisch zu gut machen.
/// Deshalb sind hier ausschließlich chronologische Aufteilungen möglich.
///
/// Purging: Reicht das Informationsintervall eines Trainingslabels (<see cref="WalkForwardOptions.LabelSpanBars"/>)
/// in das Testfenster hinein, überlappen sich Trainings- und Testinformation. Solche Trainingsbars
/// werden entfernt (nach López de Prado).
///
/// Embargo: Unmittelbar nach dem Testfenster liegende Bars bleiben für spätere Trainings gesperrt,
/// weil sie über Autokorrelation noch Testinformation tragen.
/// </summary>
public static class WalkForwardPlanner
{
    public static WalkForwardPlan Plan(IReadOnlyList<DateTimeOffset> barTimes, WalkForwardOptions options)
    {
        ArgumentNullException.ThrowIfNull(barTimes);
        ArgumentNullException.ThrowIfNull(options);

        var notes = new List<string>();
        int total = barTimes.Count;

        if (options.TrainBars <= 0 || options.TestBars <= 0)
            throw new ArgumentException("TrainBars und TestBars müssen > 0 sein.", nameof(options));
        if (options.HoldoutFraction is < 0 or >= 1)
            throw new ArgumentException("HoldoutFraction muss in [0,1) liegen.", nameof(options));

        // --- Finaler Holdout am Ende reservieren ---
        int holdoutBars = (int)Math.Floor(total * options.HoldoutFraction);
        int usable = total - holdoutBars;
        DataSplit? holdout = null;
        if (holdoutBars > 0)
        {
            holdout = Make("holdout", SplitRole.Holdout, usable, total, barTimes);
            notes.Add($"Finaler Holdout reserviert: {holdoutBars} Bars ab {barTimes[usable]:u}. " +
                      "Er wird von der Suche nicht berührt.");
        }

        int step = options.StepBars is > 0 ? options.StepBars.Value : options.TestBars;
        var folds = new List<WalkForwardFold>();

        // Überlappende Testfenster ablehnen: Die Auswertung liefert EINE verkettete Out-of-Sample-Kurve.
        // Bei StepBars < TestBars überschneiden sich aufeinanderfolgende Testfenster; eine reine
        // Hintereinander-Verkettung würde dieselben Perioden mehrfach zählen und die Zeitachse
        // rückwärts laufen lassen. Eine korrekte Zusammenführung überlappender OOS-Segmente (Ensemble)
        // ist bewusst NICHT implementiert — deshalb wird die Aufteilung nachvollziehbar abgelehnt.
        if (step < options.TestBars)
        {
            notes.Add($"Schrittweite {step} Bars ist kleiner als das Testfenster {options.TestBars} Bars — " +
                      "die Testfenster würden sich überlappen. Für die einzelne, verkettete Out-of-Sample-Kurve " +
                      "existiert keine korrekte Zusammenführung überlappender Perioden; die Aufteilung wird " +
                      "abgelehnt, um doppelt gezählte Perioden und rückwärts laufende Zeitstempel zu vermeiden. " +
                      "Bitte StepBars ≥ TestBars wählen (Standard: StepBars = TestBars, lückenlose disjunkte Tests).");
            return new WalkForwardPlan { Folds = folds, Holdout = holdout, Options = options, TotalBars = total, Notes = notes };
        }

        if (usable < options.TrainBars + options.TestBars)
        {
            notes.Add($"Zu wenige Bars für ein vollständiges Fenster: {usable} verfügbar, " +
                      $"{options.TrainBars + options.TestBars} nötig (Train {options.TrainBars} + Test {options.TestBars}).");
            return new WalkForwardPlan { Folds = folds, Holdout = holdout, Options = options, TotalBars = total, Notes = notes };
        }

        int foldIndex = 0;
        for (int trainEnd = options.TrainBars; trainEnd + options.TestBars <= usable; trainEnd += step)
        {
            int trainStart = options.Mode == WalkForwardMode.Anchored ? 0 : trainEnd - options.TrainBars;
            int testStart = trainEnd;
            int testEnd = trainEnd + options.TestBars;

            var test = Make($"fold{foldIndex}-test", SplitRole.Test, testStart, testEnd, barTimes);

            // Purging: Trainingsbars, deren Label bis in das Testfenster reicht, entfernen.
            int purgedFrom = Math.Max(trainStart, testStart - options.LabelSpanBars);
            int purged = Math.Max(0, trainEnd - purgedFrom);

            var trainParts = new List<DataSplit>();
            if (purgedFrom > trainStart)
                trainParts.Add(Make($"fold{foldIndex}-train", SplitRole.Train, trainStart, purgedFrom, barTimes));

            // Embargo nach dem Test: für DIESES Fenster ohnehin außerhalb des Trainings; für
            // verankerte/rollende Folgefenster wird der Bereich beim nächsten Fenster berücksichtigt.
            int embargoEnd = Math.Min(usable, testEnd + options.EmbargoBars);
            if (options.Mode == WalkForwardMode.Anchored && options.EmbargoBars > 0)
                notes.Add($"Fold {foldIndex}: Embargo von {embargoEnd - testEnd} Bars nach dem Test.");

            if (trainParts.Count == 0 || trainParts.Sum(t => t.Count) <= options.WarmupBars)
            {
                notes.Add($"Fold {foldIndex} übersprungen: nach Purging bleiben {trainParts.Sum(t => t.Count)} " +
                          $"Trainingsbars (Warmup {options.WarmupBars}).");
                foldIndex++;
                continue;
            }

            folds.Add(new WalkForwardFold
            {
                Index = foldIndex,
                Train = trainParts,
                Test = test,
                PurgedBars = purged,
                EmbargoBars = Math.Max(0, embargoEnd - testEnd),
                WarmupBars = options.WarmupBars
            });
            foldIndex++;
        }

        if (options.WarmupBars > 0)
            notes.Add($"Je Abschnitt dienen die ersten {options.WarmupBars} Bars nur dem Indikator-Warmup; " +
                      "in dieser Zeit werden keine Signale gewertet.");
        notes.Add("Offene Positionen werden am Ende jedes Abschnitts zwangsweise geschlossen (EndOfData) — " +
                  "Kosten dieses Schlusses fallen im jeweiligen Abschnitt an und werden nicht in den nächsten übertragen.");

        return new WalkForwardPlan { Folds = folds, Holdout = holdout, Options = options, TotalBars = total, Notes = notes };
    }

    /// <summary>
    /// Entfernt aus einer Menge von Trainingsindizes alle, die durch Purging (Labelüberlappung mit
    /// dem Testfenster) oder Embargo (unmittelbar nach dem Testfenster) ausgeschlossen sind.
    /// Allgemein gehalten, damit dieselbe Regel später auch für CPCV gilt, wo Testblöcke mitten in
    /// der Reihe liegen können.
    /// </summary>
    public static IReadOnlyList<int> ApplyPurgeAndEmbargo(
        IEnumerable<int> trainIndices, int testStart, int testEnd, int labelSpanBars, int embargoBars)
    {
        ArgumentNullException.ThrowIfNull(trainIndices);
        if (labelSpanBars < 0) throw new ArgumentOutOfRangeException(nameof(labelSpanBars));
        if (embargoBars < 0) throw new ArgumentOutOfRangeException(nameof(embargoBars));

        int purgeFrom = testStart - labelSpanBars;   // Label startet hier, reicht aber in den Test
        int embargoTo = testEnd + embargoBars;       // exklusiv

        var kept = new List<int>();
        foreach (var i in trainIndices)
        {
            bool inTest = i >= testStart && i < testEnd;
            bool purged = i >= purgeFrom && i < testStart;
            bool embargoed = i >= testEnd && i < embargoTo;
            if (!inTest && !purged && !embargoed) kept.Add(i);
        }
        return kept;
    }

    private static DataSplit Make(string label, SplitRole role, int start, int end, IReadOnlyList<DateTimeOffset> times) => new()
    {
        Label = label,
        Role = role,
        Start = start,
        End = end,
        FromTime = times[Math.Clamp(start, 0, times.Count - 1)],
        ToTime = times[Math.Clamp(end - 1, 0, times.Count - 1)]
    };
}
