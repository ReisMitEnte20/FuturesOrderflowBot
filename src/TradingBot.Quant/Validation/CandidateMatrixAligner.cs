namespace TradingBot.Quant.Validation;

/// <summary>Renditereihe EINES Kandidaten in EINEM Testfenster, mit Zeitstempeln.</summary>
public sealed record CandidateFoldReturns(
    string CandidateId,
    IReadOnlyList<DateTimeOffset> Timestamps,
    IReadOnlyList<double> Returns);

/// <summary>
/// Wie <see cref="CandidateFoldReturns"/>, aber mit dem Abbruch-Flag aus <c>ToReturnSeries</c>
/// (<c>Truncated == true</c> ⇒ die Reihe wurde beim ersten Kapital ≤ 0 abgebrochen). Das Flag ist der
/// EINZIGE zuverlässige Abbruch-Nachweis, wenn ALLE Kandidaten eines Folds zum GLEICHEN Zeitpunkt
/// abbrechen: dann sind alle Reihen gleich lang, die Zeitstempel-Schnittmenge verwirft nichts
/// (DroppedPeriods == 0) und nur <see cref="Truncated"/> deckt den Abbruch auf.
/// </summary>
public sealed record CandidateFoldSeries(
    string CandidateId,
    IReadOnlyList<DateTimeOffset> Timestamps,
    IReadOnlyList<double> Returns,
    bool Truncated);

/// <summary>
/// Richtet die Out-of-Sample-Renditereihen mehrerer Kandidaten für die PBO-Matrix ZEITLICH aus.
///
/// Grundregel (Befund 6): Gleiche Länge bedeutet nicht gleiche Beobachtungsintervalle. Deshalb wird je
/// Fold nur behalten, was bei ALLEN Kandidaten zum selben Zeitstempel vorliegt. Bricht ein Kandidat in
/// einem Fold vorzeitig ab (Kapital ≤ 0, keine Perioden), fallen dessen fehlende Perioden für alle
/// Kandidaten dieses Folds weg — die späteren Werte anderer Kandidaten werden nie gegen frühere
/// Zeiträume verglichen. Weggefallene Perioden werden ausdrücklich vermerkt.
/// </summary>
public static class CandidateMatrixAligner
{
    public static (Dictionary<string, IReadOnlyList<double>> Series, IReadOnlyList<string> Notes, int DroppedPeriods) AlignByCommonTimestamps(
        IReadOnlyList<string> candidateIds,
        IReadOnlyList<IReadOnlyList<CandidateFoldReturns>> folds)
    {
        ArgumentNullException.ThrowIfNull(candidateIds);
        ArgumentNullException.ThrowIfNull(folds);

        var aligned = candidateIds.ToDictionary(id => id, _ => new List<double>());
        var notes = new List<string>();
        int droppedTotal = 0;

        for (int foldIndex = 0; foldIndex < folds.Count; foldIndex++)
        {
            var byId = new Dictionary<string, CandidateFoldReturns>();
            foreach (var f in folds[foldIndex]) byId[f.CandidateId] = f;

            // Referenzreihenfolge = erster vorhandener Kandidat. Jeder gemeinsame Zeitstempel liegt in
            // JEDEM Kandidaten vor (Schnittmenge), also auch im ersten — dessen Reihenfolge genügt.
            CandidateFoldReturns? reference = null;
            foreach (var id in candidateIds)
                if (byId.TryGetValue(id, out var f)) { reference = f; break; }
            if (reference is null) continue;

            var maps = new Dictionary<string, Dictionary<DateTimeOffset, double>>();
            int maxPeriods = 0;
            foreach (var id in candidateIds)
            {
                var map = new Dictionary<DateTimeOffset, double>();
                if (byId.TryGetValue(id, out var f))
                {
                    for (int k = 0; k < f.Returns.Count && k < f.Timestamps.Count; k++)
                        map[f.Timestamps[k]] = f.Returns[k];
                    maxPeriods = Math.Max(maxPeriods, map.Count);
                }
                maps[id] = map;
            }

            var common = reference.Timestamps.Where(t => candidateIds.All(id => maps[id].ContainsKey(t))).ToList();
            foreach (var t in common)
                foreach (var id in candidateIds)
                    aligned[id].Add(maps[id][t]);

            if (common.Count < maxPeriods)
            {
                droppedTotal += maxPeriods - common.Count;
                notes.Add($"Fold {foldIndex}: {maxPeriods - common.Count} Periode(n) nicht bei allen Kandidaten vorhanden " +
                          "(vorzeitiger Abbruch/Kapital ≤ 0) — nur zeitlich übereinstimmende Perioden gehen in CSCV ein.");
            }
        }

        return (aligned.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<double>)kv.Value), notes, droppedTotal);
    }

    /// <summary>
    /// Richtet die Kandidatenreihen zeitlich aus (wie <see cref="AlignByCommonTimestamps"/>) UND sammelt
    /// zusätzlich die tatsächlich abgebrochenen Reihen aus dem <see cref="CandidateFoldSeries.Truncated"/>-Flag.
    /// Damit wird ein Abbruch (Kapital ≤ 0) auch dann erkannt, wenn ALLE Kandidaten eines Folds zum GLEICHEN
    /// Zeitpunkt abbrechen — in diesem Fall bleibt <c>DroppedPeriods == 0</c> (gleiche Reihenlänge, keine
    /// Schnittmengen-Kürzung), sodass die Drop-Zahl allein den Abbruch NICHT bemerken würde.
    /// </summary>
    public static (Dictionary<string, IReadOnlyList<double>> Series, IReadOnlyList<string> Notes, int DroppedPeriods, IReadOnlyList<string> Truncations)
        AlignAndDetectAborts(
            IReadOnlyList<string> candidateIds,
            IReadOnlyList<IReadOnlyList<CandidateFoldSeries>> folds)
    {
        ArgumentNullException.ThrowIfNull(candidateIds);
        ArgumentNullException.ThrowIfNull(folds);

        var basic = folds
            .Select(f => (IReadOnlyList<CandidateFoldReturns>)f
                .Select(x => new CandidateFoldReturns(x.CandidateId, x.Timestamps, x.Returns)).ToList())
            .ToList();

        var (series, notes, dropped) = AlignByCommonTimestamps(candidateIds, basic);

        var truncations = new List<string>();
        for (int foldIndex = 0; foldIndex < folds.Count; foldIndex++)
            foreach (var c in folds[foldIndex])
                if (c.Truncated)
                    truncations.Add($"Kandidat '{c.CandidateId}' in Fold {foldIndex}: Kapital ≤ 0 (Reihe abgebrochen).");

        return (series, notes, dropped, truncations);
    }

    /// <summary>
    /// Politik zur Nachprüfung von Befund 6 (PBO): PBO darf NICHT berechnet werden, wenn
    /// (a) mindestens ein Kandidat mit einem Fehler ausfiel, (b) mindestens eine Kandidatenreihe vorzeitig
    /// abbrach (Kapital ≤ 0, <paramref name="truncations"/>) — auch wenn ALLE Kandidaten im selben Fold zum
    /// gleichen Zeitpunkt abbrechen und deshalb <paramref name="droppedPeriods"/> == 0 bleibt — oder
    /// (c) die zeitliche Ausrichtung Perioden verwerfen musste (<paramref name="droppedPeriods"/> &gt; 0).
    /// Sonst würde PBO auf einer stillschweigend verkürzten Datenbasis beruhen. Rückgabe: der nachvollziehbare
    /// Grund, warum PBO nicht berechenbar ist — oder <c>null</c>, wenn PBO berechnet werden darf.
    /// </summary>
    public static string? PboBlockedReason(int droppedPeriods, IReadOnlyList<string> candidateErrors, IReadOnlyList<string> truncations)
    {
        ArgumentNullException.ThrowIfNull(candidateErrors);
        ArgumentNullException.ThrowIfNull(truncations);

        if (candidateErrors.Count > 0)
            return "PBO nicht berechenbar: mindestens ein Kandidat fiel in einem Fold mit einem Fehler aus " +
                   $"({string.Join(" | ", candidateErrors)}). PBO wird nicht auf einer verkürzten Datenbasis berechnet.";

        if (truncations.Count > 0)
            return "PBO nicht berechenbar: mindestens eine Kandidatenreihe brach vorzeitig ab (Kapital ≤ 0): " +
                   $"{string.Join(" | ", truncations)}. Auch wenn dabei ALLE Kandidaten eines Folds zum gleichen " +
                   "Zeitpunkt abbrechen (gleiche Reihenlänge, keine Schnittmengen-Kürzung), wird PBO nicht auf der " +
                   "verkürzten Basis berechnet, sondern als nicht berechenbar gemeldet.";

        if (droppedPeriods > 0)
            return $"PBO nicht berechenbar: die zeitliche Ausrichtung musste {droppedPeriods} Periode(n) verwerfen, " +
                   "weil mindestens ein Kandidat in einem Fold vorzeitig abbrach (Kapital ≤ 0). Eine Zeitstempel-" +
                   "Schnittmenge würde die gemeinsame Datenbasis für ALLE Kandidaten stillschweigend verkürzen; PBO " +
                   "wird daher als nicht berechenbar gemeldet, statt einen Wert auf verkürzter Basis auszuweisen.";

        return null;
    }
}
