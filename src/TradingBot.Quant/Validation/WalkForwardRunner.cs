using TradingBot.Quant.Registry;
using TradingBot.Quant.Series;

namespace TradingBot.Quant.Validation;

/// <summary>Ergebnis einer Auswertung eines Parametersatzes auf einem Abschnitt.</summary>
public sealed record SegmentOutcome
{
    /// <summary>Wert der Auswahlmetrik; null = nicht berechenbar (zählt nicht als „schlechtestes Ergebnis").</summary>
    public double? SelectionValue { get; init; }
    public IReadOnlyDictionary<string, double?> Metrics { get; init; } = new Dictionary<string, double?>();
    public int TradeCount { get; init; }
    /// <summary>Netto-Renditereihe des Abschnitts — für die Verkettung der Out-of-Sample-Kurve.</summary>
    public ReturnSeries? Returns { get; init; }
    public string? Error { get; init; }

    public static SegmentOutcome Failed(string error) => new() { Error = error };
}

/// <summary>Wertet EINEN Parametersatz auf den übergebenen Abschnitten aus.</summary>
public delegate Task<SegmentOutcome> SegmentEvaluator(
    IReadOnlyDictionary<string, string> parameters,
    IReadOnlyList<DataSplit> segments,
    SplitRole role,
    CancellationToken ct);

/// <summary>Ein benannter Parametersatz, der als Kandidat geprüft wird.</summary>
public sealed record ParameterCandidate(string Id, IReadOnlyDictionary<string, string> Parameters);

/// <summary>Ergebnis eines Walk-Forward-Fensters.</summary>
public sealed record WalkForwardFoldResult
{
    public required WalkForwardFold Fold { get; init; }
    /// <summary>Der im TRAINING ausgewählte Kandidat. Die Auswahl nutzt ausschließlich Trainingsdaten.</summary>
    public ParameterCandidate? Selected { get; init; }
    public double? TrainSelectionValue { get; init; }
    public SegmentOutcome? Test { get; init; }
    /// <summary>Trainings-Auswahlwert je Kandidat (Grundlage für die Overfitting-Analyse).</summary>
    public IReadOnlyDictionary<string, double?> TrainValues { get; init; } = new Dictionary<string, double?>();
    /// <summary>Test-Auswahlwert je Kandidat — nur zur späteren Analyse, NICHT zur Auswahl verwendet.</summary>
    public IReadOnlyDictionary<string, double?> TestValues { get; init; } = new Dictionary<string, double?>();
    public string? Note { get; init; }
}

/// <summary>Gesamtergebnis der Walk-Forward-Analyse.</summary>
public sealed record WalkForwardRunResult
{
    public required WalkForwardPlan Plan { get; init; }
    public required IReadOnlyList<WalkForwardFoldResult> Folds { get; init; }
    public required string SelectionMetric { get; init; }
    public SelectionDirection Direction { get; init; }

    /// <summary>
    /// Verkettete Out-of-Sample-Renditen aller Fenster in zeitlicher Reihenfolge — die einzige
    /// Reihe, die für eine Aussage über unbekannte Daten herangezogen werden darf.
    /// </summary>
    public ReturnSeries OutOfSampleReturns { get; init; } = ReturnSeries.Empty;

    /// <summary>
    /// Kandidaten-Renditematrix für die spätere PBO-Analyse: je Kandidat die verkettete
    /// Renditereihe über ALLE Testfenster (gleiche Länge, gleiche Zeitachse).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<double>> CandidateTestReturns { get; init; }
        = new Dictionary<string, IReadOnlyList<double>>();

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Führt eine Walk-Forward-Analyse aus. Kernregel: Die Parameterauswahl je Fenster geschieht
/// ausschließlich auf dem Trainingsabschnitt. Die Testabschnitte werden zwar für ALLE Kandidaten
/// ausgewertet — aber nur, um später Overfitting messen zu können; sie fließen nie in die Auswahl ein.
/// </summary>
public static class WalkForwardRunner
{
    public static async Task<WalkForwardRunResult> RunAsync(
        WalkForwardPlan plan,
        IReadOnlyList<ParameterCandidate> candidates,
        SegmentEvaluator evaluator,
        string selectionMetric,
        SelectionDirection direction = SelectionDirection.HigherIsBetter,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(evaluator);
        if (candidates.Count == 0) throw new ArgumentException("Mindestens ein Kandidat nötig.", nameof(candidates));

        var foldResults = new List<WalkForwardFoldResult>();
        var notes = new List<string>(plan.Notes);
        var oosTimes = new List<DateTimeOffset>();
        var oosReturns = new List<double>();
        var oosLevels = new List<double>();
        var perCandidate = candidates.ToDictionary(c => c.Id, _ => new List<double>());

        int totalSteps = Math.Max(1, plan.Folds.Count * candidates.Count * 2);
        int done = 0;
        double equity = 1.0;

        foreach (var fold in plan.Folds)
        {
            ct.ThrowIfCancellationRequested();

            var trainValues = new Dictionary<string, double?>();
            var testValues = new Dictionary<string, double?>();
            var testOutcomes = new Dictionary<string, SegmentOutcome>();

            foreach (var c in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var tr = await evaluator(c.Parameters, fold.Train, SplitRole.Train, ct);
                trainValues[c.Id] = tr.Error is null ? tr.SelectionValue : null;
                progress?.Report(++done / (double)totalSteps);
            }

            foreach (var c in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var te = await evaluator(c.Parameters, new[] { fold.Test }, SplitRole.Test, ct);
                testOutcomes[c.Id] = te;
                testValues[c.Id] = te.Error is null ? te.SelectionValue : null;
                if (te.Returns is not null) perCandidate[c.Id].AddRange(te.Returns.Returns);
                progress?.Report(++done / (double)totalSteps);
            }

            var selected = SelectBest(candidates, trainValues, direction);
            string? note = null;
            if (selected is null)
            {
                note = $"Fold {fold.Index}: kein Kandidat lieferte im Training einen gültigen Wert für '{selectionMetric}' — " +
                       "kein Out-of-Sample-Ergebnis für dieses Fenster.";
                notes.Add(note);
            }
            else
            {
                var te = testOutcomes[selected.Id];
                if (te.Returns is not null)
                    for (int i = 0; i < te.Returns.Count; i++)
                    {
                        oosTimes.Add(te.Returns.Timestamps[i]);
                        oosReturns.Add(te.Returns.Returns[i]);
                        equity *= 1.0 + te.Returns.Returns[i];
                        oosLevels.Add(equity);
                    }
                else if (te.Error is not null)
                {
                    note = $"Fold {fold.Index}: Testlauf fehlgeschlagen ({te.Error}).";
                    notes.Add(note);
                }
            }

            foldResults.Add(new WalkForwardFoldResult
            {
                Fold = fold,
                Selected = selected,
                TrainSelectionValue = selected is null ? null : trainValues[selected.Id],
                Test = selected is null ? null : testOutcomes[selected.Id],
                TrainValues = trainValues,
                TestValues = testValues,
                Note = note
            });
        }

        var oos = new ReturnSeries
        {
            Name = "Out-of-Sample (verkettet)",
            Timestamps = oosTimes,
            Returns = oosReturns,
            EquityLevels = oosLevels,
            InitialCapital = 1.0,
            Basis = EquityBasis.Total,
            Frequency = ReturnFrequency.Bar
        };

        if (oosReturns.Count == 0)
            notes.Add("Keine Out-of-Sample-Renditen erzeugt — die Walk-Forward-Analyse liefert keine auswertbare Reihe.");

        return new WalkForwardRunResult
        {
            Plan = plan,
            Folds = foldResults,
            SelectionMetric = selectionMetric,
            Direction = direction,
            OutOfSampleReturns = oos,
            CandidateTestReturns = perCandidate.ToDictionary(k => k.Key, v => (IReadOnlyList<double>)v.Value),
            Notes = notes
        };
    }

    /// <summary>
    /// Wählt den besten Kandidaten anhand der Trainingswerte. Kandidaten ohne gültigen Wert
    /// nehmen NICHT teil (sie gelten nicht als schlechtestes Ergebnis). Bei Gleichstand entscheidet
    /// die Kandidaten-Id lexikografisch — deterministisch und nachvollziehbar.
    /// </summary>
    internal static ParameterCandidate? SelectBest(
        IReadOnlyList<ParameterCandidate> candidates,
        IReadOnlyDictionary<string, double?> values,
        SelectionDirection direction)
    {
        ParameterCandidate? best = null;
        double bestVal = 0;
        foreach (var c in candidates)
        {
            if (!values.TryGetValue(c.Id, out var v) || v is null || double.IsNaN(v.Value) || double.IsInfinity(v.Value))
                continue;
            if (best is null)
            {
                best = c; bestVal = v.Value; continue;
            }
            bool better = direction == SelectionDirection.HigherIsBetter ? v.Value > bestVal : v.Value < bestVal;
            bool tie = v.Value == bestVal && string.CompareOrdinal(c.Id, best.Id) < 0;
            if (better || tie) { best = c; bestVal = v.Value; }
        }
        return best;
    }
}
