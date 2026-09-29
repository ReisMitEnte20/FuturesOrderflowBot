using TradingBot.Quant.Registry;

namespace TradingBot.Quant.Generation;

/// <summary>Ein Strategievorschlag eines Generators. Enthält NIE Ausführungslogik — nur Parameter und Begründung.</summary>
public sealed record StrategyProposal
{
    public required string Id { get; init; }
    public required string StrategyId { get; init; }
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }
    /// <summary>Begründung des Vorschlags (Hypothese, Bezug zu einer Quelle).</summary>
    public required string Rationale { get; init; }
    public StrategyOrigin Origin { get; init; } = StrategyOrigin.Ai;
    public string? OriginReference { get; init; }
}

/// <summary>
/// Rückmeldung an den Generator. Sie enthält AUSSCHLIESSLICH Trainings- und Validierungsergebnisse.
/// Holdout-Kennzahlen dürfen hier niemals stehen — sonst fließt der finale Holdout über die
/// Rückkopplung in die Suche ein und verliert seine Aussagekraft.
/// </summary>
public sealed record EvaluationFeedback
{
    public required string ProposalId { get; init; }
    public required string TrialId { get; init; }
    public TrialStatus Status { get; init; }
    /// <summary>Kennzahlen aus Training/Validierung. Null-Werte = nicht berechenbar.</summary>
    public IReadOnlyDictionary<string, double?> Metrics { get; init; } = new Dictionary<string, double?>();
    public string? Note { get; init; }
}

/// <summary>
/// Erzeugt Strategievorschläge. Der Generator kennt den Suchraum und die bisherigen Rückmeldungen,
/// aber NICHT den finalen Holdout und nicht dessen Ergebnisse.
/// </summary>
public interface IStrategyGenerator
{
    string Name { get; }

    /// <summary>Nächste Vorschläge, höchstens <paramref name="count"/> Stück.</summary>
    Task<IReadOnlyList<StrategyProposal>> ProposeAsync(
        CampaignRecord campaign,
        IReadOnlyList<EvaluationFeedback> previousFeedback,
        int count,
        CancellationToken ct = default);
}

/// <summary>
/// Bewertet Vorschläge. Strikt vom Generator getrennt: der Evaluator führt die Backtests aus,
/// schreibt ins Versuchsregister und entscheidet, welche Kennzahlen zurückfließen dürfen.
/// </summary>
public interface IStrategyEvaluator
{
    Task<EvaluationFeedback> EvaluateAsync(CampaignRecord campaign, StrategyProposal proposal, CancellationToken ct = default);
}

/// <summary>
/// Schützt den finalen Holdout und das Versuchsbudget in einer Generierungs-Kampagne.
///
/// Zwei Regeln werden hier durchgesetzt, weil sie sonst leicht unbemerkt verletzt werden:
/// 1. Rückmeldungen an den Generator dürfen keine Holdout-Kennzahlen enthalten.
/// 2. Es werden nie mehr Vorschläge bewertet, als das vorab gespeicherte Budget erlaubt.
///
/// Diese Klasse startet nichts von selbst und handelt nicht: sie orchestriert ausschließlich
/// Vorschlag → Bewertung → Registereintrag. Es gibt kein autonomes Live-Trading.
/// </summary>
public sealed class GenerationCampaignRunner
{
    /// <summary>Kennzahlen-Präfix, an dem Holdout-Ergebnisse erkannt werden.</summary>
    public const string HoldoutMetricPrefix = "holdout.";

    private readonly IExperimentStore _store;

    public GenerationCampaignRunner(IExperimentStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// Entfernt Holdout-Kennzahlen aus einer Rückmeldung. Gibt zusätzlich zurück, ob etwas entfernt
    /// wurde — der Aufrufer muss das melden, weil es auf einen Fehler im Evaluator hindeutet.
    /// </summary>
    public static (EvaluationFeedback Sanitized, bool Removed) SanitizeFeedback(EvaluationFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        var clean = feedback.Metrics
            .Where(kv => !kv.Key.StartsWith(HoldoutMetricPrefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        bool removed = clean.Count != feedback.Metrics.Count;
        return (feedback with { Metrics = clean }, removed);
    }

    /// <summary>
    /// Führt eine Generierungs-Kampagne aus: Vorschläge holen, bewerten, Rückmeldung bereinigen,
    /// Budget einhalten. Bricht ab, sobald das Budget erschöpft ist oder der Generator nichts mehr liefert.
    /// </summary>
    public async Task<IReadOnlyList<EvaluationFeedback>> RunAsync(
        string campaignId,
        IStrategyGenerator generator,
        IStrategyEvaluator evaluator,
        int batchSize = 5,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(evaluator);
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));

        var campaign = await _store.GetCampaignAsync(campaignId, ct)
            ?? throw new ExperimentRegistryException("CAMPAIGN_UNKNOWN",
                $"Kampagne '{campaignId}' existiert nicht. Budget, Suchraum und Auswahlkriterium müssen VOR der Suche gespeichert sein.");

        var history = new List<EvaluationFeedback>();
        int used = (await _store.ListTrialsAsync(campaignId, ct)).Count;

        while (used < campaign.TrialBudget)
        {
            ct.ThrowIfCancellationRequested();
            int remaining = campaign.TrialBudget - used;
            var proposals = await generator.ProposeAsync(campaign, history, Math.Min(batchSize, remaining), ct);
            if (proposals.Count == 0) break;

            foreach (var p in proposals)
            {
                if (used >= campaign.TrialBudget) break;
                ct.ThrowIfCancellationRequested();

                var raw = await evaluator.EvaluateAsync(campaign, p, ct);
                var (clean, removed) = SanitizeFeedback(raw);
                if (removed)
                    clean = clean with
                    {
                        Note = (clean.Note is null ? "" : clean.Note + " ") +
                               "Holdout-Kennzahlen wurden aus der Rückmeldung entfernt — sie dürfen den Generator nicht erreichen."
                    };

                history.Add(clean);
                used++;
                progress?.Report(used / (double)campaign.TrialBudget);
            }
        }

        return history;
    }
}
