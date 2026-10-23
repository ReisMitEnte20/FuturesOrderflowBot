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

        // Session-ausgerichteter Modus (z. B. ORB): Fold-Grenzen ausschließlich auf Session-Anfänge.
        if (options.SessionStarts is not null)
            return PlanBySessions(barTimes, options);

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
    /// SESSION-AUSGERICHTETE Aufteilung: Train/Test umfassen GANZE Sessions; Fold-Grenzen liegen ausschließlich auf
    /// den übergebenen Session-Anfängen (<see cref="WalkForwardOptions.SessionStarts"/>, aus Zeitstempeln + Kalender,
    /// NICHT aus Trades). Jeder Abschnitt beginnt an einem Session-Start und enthält vollständige Sessions (inkl.
    /// Session-Schluss). OOS-Abschnitte sind disjunkt (Schritt = Testlänge). Purging (<see cref="WalkForwardOptions.LabelSpanBars"/>),
    /// Embargo und Warmup wirken wie im Bar-Modus. Fehlende Schlussdaten einer Session werden NICHT hier, sondern von der
    /// nachgelagerten Ungültigkeitsprüfung (unvollständige Session) abgelehnt — diese wird dadurch nicht abgeschwächt.
    /// </summary>
    private static WalkForwardPlan PlanBySessions(IReadOnlyList<DateTimeOffset> barTimes, WalkForwardOptions o)
    {
        var notes = new List<string>();
        int total = barTimes.Count;
        var starts = o.SessionStarts!;
        int nSessions = starts.Count;

        if (o.TrainSessions <= 0 || o.TestSessions <= 0)
            throw new ArgumentException("TrainSessions und TestSessions müssen > 0 sein (session-ausgerichtet).", nameof(o));
        if (o.HoldoutFraction is < 0 or >= 1)
            throw new ArgumentException("HoldoutFraction muss in [0,1) liegen.", nameof(o));

        int StartIdx(int s) => s < nSessions ? starts[s] : total;   // Bar-Index am Session-Anfang (s==nSessions ⇒ Datenende)

        int holdoutSessions = (int)Math.Floor(nSessions * o.HoldoutFraction);
        int usableSessions = nSessions - holdoutSessions;
        DataSplit? holdout = null;
        if (holdoutSessions > 0 && usableSessions < nSessions)
        {
            int hStart = StartIdx(usableSessions);
            holdout = Make("holdout", SplitRole.Holdout, hStart, total, barTimes);
            notes.Add($"Finaler Holdout reserviert: {holdoutSessions} Session(s) ab {barTimes[Math.Clamp(hStart, 0, Math.Max(0, total - 1))]:u}. " +
                      "Er wird von der Suche nicht berührt.");
        }

        var folds = new List<WalkForwardFold>();
        if (usableSessions < o.TrainSessions + o.TestSessions)
        {
            notes.Add($"Zu wenige Sessions für ein vollständiges Fenster: {usableSessions} verfügbar, " +
                      $"{o.TrainSessions + o.TestSessions} nötig (Train {o.TrainSessions} + Test {o.TestSessions} Sessions).");
            return new WalkForwardPlan { Folds = folds, Holdout = holdout, Options = o, TotalBars = total, Notes = notes };
        }

        int usableEndIdx = StartIdx(usableSessions);
        int step = o.TestSessions;   // disjunkte OOS-Abschnitte
        int foldIndex = 0;
        for (int a = 0; a + o.TrainSessions + o.TestSessions <= usableSessions; a += step)
        {
            int trainStartIdx = StartIdx(o.Mode == WalkForwardMode.Anchored ? 0 : a);
            int testStartIdx = StartIdx(a + o.TrainSessions);
            int testEndIdx = StartIdx(a + o.TrainSessions + o.TestSessions);

            var test = Make($"fold{foldIndex}-test", SplitRole.Test, testStartIdx, testEndIdx, barTimes);

            // Purging (bar-basiert, identisch zum Bar-Modus): Trainingsbars, deren Label ins Testfenster reicht, entfernen.
            int purgedFrom = Math.Max(trainStartIdx, testStartIdx - o.LabelSpanBars);
            int purged = Math.Max(0, testStartIdx - purgedFrom);
            var trainParts = new List<DataSplit>();
            if (purgedFrom > trainStartIdx)
                trainParts.Add(Make($"fold{foldIndex}-train", SplitRole.Train, trainStartIdx, purgedFrom, barTimes));

            int embargoEnd = Math.Min(usableEndIdx, testEndIdx + o.EmbargoBars);

            if (trainParts.Count == 0 || trainParts.Sum(t => t.Count) <= o.WarmupBars)
            {
                notes.Add($"Fold {foldIndex} übersprungen: nach Purging bleiben {trainParts.Sum(t => t.Count)} " +
                          $"Trainingsbars (Warmup {o.WarmupBars}).");
                foldIndex++;
                continue;
            }

            folds.Add(new WalkForwardFold
            {
                Index = foldIndex,
                Train = trainParts,
                Test = test,
                PurgedBars = purged,
                EmbargoBars = Math.Max(0, embargoEnd - testEndIdx),
                WarmupBars = o.WarmupBars
            });
            foldIndex++;
        }

        notes.Add($"Session-ausgerichteter Walk-forward: {nSessions} Session(s) aus Zeitstempeln + Session-Kalender " +
                  $"(nicht aus Trades), Train {o.TrainSessions} / Test {o.TestSessions} Sessions, disjunkte OOS-Abschnitte; " +
                  "jeder Abschnitt beginnt an einem Session-Start und enthält vollständige Sessions.");
        if (o.WarmupBars > 0)
            notes.Add($"Je Abschnitt dienen die ersten {o.WarmupBars} Bars nur dem Indikator-Warmup; in dieser Zeit werden keine Signale gewertet.");
        notes.Add("Offene Positionen werden am Ende jedes Abschnitts zwangsweise geschlossen (EndOfData) — " +
                  "Kosten dieses Schlusses fallen im jeweiligen Abschnitt an und werden nicht in den nächsten übertragen.");
        return new WalkForwardPlan { Folds = folds, Holdout = holdout, Options = o, TotalBars = total, Notes = notes };
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
