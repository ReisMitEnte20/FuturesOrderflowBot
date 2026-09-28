using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingBot.Quant.Registry;

/// <summary>
/// Dateibasiertes Versuchsregister (eine JSON-Datei je Kampagne bzw. Versuch). Bewusst schlicht und
/// menschenlesbar: das Register muss auch ohne laufende Anwendung prüfbar sein.
///
/// Ablage (Standard): &lt;root&gt;/campaigns/&lt;campaignId&gt;.json und
/// &lt;root&gt;/trials/&lt;campaignId&gt;/&lt;trialId&gt;.json
/// </summary>
public sealed class JsonExperimentStore : IExperimentStore
{
    private readonly string _root;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonExperimentStore(string rootDirectory)
    {
        _root = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
        Directory.CreateDirectory(CampaignDir);
        Directory.CreateDirectory(TrialRoot);
        Directory.CreateDirectory(HoldoutDir);
    }

    private string CampaignDir => Path.Combine(_root, "campaigns");
    private string TrialRoot => Path.Combine(_root, "trials");
    private string HoldoutDir => Path.Combine(_root, "holdout");
    private string CampaignPath(string id) => Path.Combine(CampaignDir, Sanitize(id) + ".json");
    private string TrialDir(string campaignId) => Path.Combine(TrialRoot, Sanitize(campaignId));
    private string TrialPath(string campaignId, string trialId) => Path.Combine(TrialDir(campaignId), Sanitize(trialId) + ".json");
    private string HoldoutPath(string campaignId) => Path.Combine(HoldoutDir, Sanitize(campaignId) + ".json");

    public async Task<CampaignRecord> CreateCampaignAsync(CampaignRecord campaign, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        if (string.IsNullOrWhiteSpace(campaign.Id)) throw new ExperimentRegistryException("CAMPAIGN_ID_MISSING", "Kampagne braucht eine Id.");
        if (campaign.TrialBudget <= 0) throw new ExperimentRegistryException("BUDGET_INVALID", "Versuchsbudget muss > 0 sein.");
        if (string.IsNullOrWhiteSpace(campaign.SearchSpace)) throw new ExperimentRegistryException("SEARCHSPACE_MISSING", "Suchraum muss vor der Kampagne beschrieben sein.");
        if (string.IsNullOrWhiteSpace(campaign.SelectionMetric)) throw new ExperimentRegistryException("SELECTION_MISSING", "Auswahlkriterium muss vor der Kampagne festgelegt sein.");

        await _lock.WaitAsync(ct);
        try
        {
            if (File.Exists(CampaignPath(campaign.Id)))
                throw new ExperimentRegistryException("CAMPAIGN_EXISTS", $"Kampagne '{campaign.Id}' existiert bereits.");
            var locked = campaign with { Locked = true };
            await WriteAsync(CampaignPath(locked.Id), locked, ct);
            return locked;
        }
        finally { _lock.Release(); }
    }

    public async Task<CampaignRecord?> GetCampaignAsync(string campaignId, CancellationToken ct = default)
    {
        var path = CampaignPath(campaignId);
        return File.Exists(path) ? await ReadAsync<CampaignRecord>(path, ct) : null;
    }

    public async Task<IReadOnlyList<CampaignRecord>> ListCampaignsAsync(CancellationToken ct = default)
    {
        var list = new List<CampaignRecord>();
        if (!Directory.Exists(CampaignDir)) return list;
        foreach (var f in Directory.EnumerateFiles(CampaignDir, "*.json").OrderBy(x => x))
        {
            var c = await ReadAsync<CampaignRecord>(f, ct);
            if (c is not null) list.Add(c);
        }
        return list.OrderBy(c => c.CreatedUtc).ToList();
    }

    public async Task<TrialRecord> AddTrialAsync(TrialRecord trial, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trial);
        if (string.IsNullOrWhiteSpace(trial.Id)) throw new ExperimentRegistryException("TRIAL_ID_MISSING", "Versuch braucht eine Id.");

        // Kampagnen-/Holdout-Prüfung, Existenz- und Budgetprüfung sowie das Schreiben laufen unter EINER
        // Sperre — sonst könnte zwischen Prüfung und Schreiben ein paralleler Request das Budget sprengen
        // oder den bereits verbrauchten Holdout erneut treffen (Time-of-check/Time-of-use).
        await _lock.WaitAsync(ct);
        try
        {
            var campaign = await GetCampaignAsync(trial.CampaignId, ct)
                ?? throw new ExperimentRegistryException("CAMPAIGN_UNKNOWN", $"Kampagne '{trial.CampaignId}' existiert nicht. Kampagne vor den Versuchen anlegen.");

            GuardHoldout(campaign, trial);

            Directory.CreateDirectory(TrialDir(trial.CampaignId));
            if (File.Exists(TrialPath(trial.CampaignId, trial.Id)))
                throw new ExperimentRegistryException("TRIAL_EXISTS", $"Versuch '{trial.Id}' existiert bereits.");

            int used = Directory.EnumerateFiles(TrialDir(trial.CampaignId), "*.json").Count();
            if (used >= campaign.TrialBudget)
                throw new ExperimentRegistryException("BUDGET_EXCEEDED",
                    $"Versuchsbudget der Kampagne '{campaign.Id}' erschöpft ({used}/{campaign.TrialBudget}). " +
                    "Budget wurde vor der Kampagne festgelegt und wird nicht stillschweigend erhöht.");

            await WriteAsync(TrialPath(trial.CampaignId, trial.Id), trial, ct);
            return trial;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<TrialRecord>> ReserveTrialsAsync(string campaignId, IReadOnlyList<TrialRecord> trials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trials);
        if (trials.Count == 0) return Array.Empty<TrialRecord>();

        await _lock.WaitAsync(ct);
        try
        {
            var campaign = await GetCampaignAsync(campaignId, ct)
                ?? throw new ExperimentRegistryException("CAMPAIGN_UNKNOWN", $"Kampagne '{campaignId}' existiert nicht. Kampagne vor den Versuchen anlegen.");

            Directory.CreateDirectory(TrialDir(campaignId));

            // Erst prüfen (alle Versuche), dann atomar schreiben — passt nicht alles, wird NICHTS reserviert.
            foreach (var t in trials)
            {
                if (string.IsNullOrWhiteSpace(t.Id))
                    throw new ExperimentRegistryException("TRIAL_ID_MISSING", "Versuch braucht eine Id.");
                if (!string.Equals(t.CampaignId, campaignId, StringComparison.Ordinal))
                    throw new ExperimentRegistryException("TRIAL_CAMPAIGN_MISMATCH", $"Versuch '{t.Id}' gehört nicht zur Kampagne '{campaignId}'.");
                GuardHoldout(campaign, t);
                if (File.Exists(TrialPath(campaignId, t.Id)))
                    throw new ExperimentRegistryException("TRIAL_EXISTS", $"Versuch '{t.Id}' existiert bereits.");
            }

            int used = Directory.EnumerateFiles(TrialDir(campaignId), "*.json").Count();
            if (used + trials.Count > campaign.TrialBudget)
                throw new ExperimentRegistryException("BUDGET_EXCEEDED",
                    $"Versuchsbudget der Kampagne '{campaign.Id}' reicht nicht ({used} belegt + {trials.Count} angefordert > {campaign.TrialBudget}). " +
                    "Budget wurde vor der Kampagne festgelegt und wird nicht stillschweigend erhöht.");

            foreach (var t in trials)
                await WriteAsync(TrialPath(campaignId, t.Id), t, ct);
            return trials;
        }
        finally { _lock.Release(); }
    }

    public async Task<TrialRecord> UpdateTrialAsync(TrialRecord trial, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trial);
        var path = TrialPath(trial.CampaignId, trial.Id);
        if (!File.Exists(path))
            throw new ExperimentRegistryException("TRIAL_UNKNOWN", $"Versuch '{trial.Id}' existiert nicht.");
        if (trial.Status is TrialStatus.Failed or TrialStatus.Discarded && string.IsNullOrWhiteSpace(trial.StatusReason))
            throw new ExperimentRegistryException("STATUS_REASON_MISSING",
                "Für 'Failed'/'Discarded' ist ein Grund verpflichtend (Nachvollziehbarkeit negativer Ergebnisse).");

        await _lock.WaitAsync(ct);
        try { await WriteAsync(path, trial, ct); return trial; }
        finally { _lock.Release(); }
    }

    public async Task<TrialRecord?> GetTrialAsync(string trialId, CancellationToken ct = default)
    {
        if (!Directory.Exists(TrialRoot)) return null;
        foreach (var dir in Directory.EnumerateDirectories(TrialRoot))
        {
            var p = Path.Combine(dir, Sanitize(trialId) + ".json");
            if (File.Exists(p)) return await ReadAsync<TrialRecord>(p, ct);
        }
        return null;
    }

    public async Task<IReadOnlyList<TrialRecord>> ListTrialsAsync(string? campaignId = null, CancellationToken ct = default)
    {
        var list = new List<TrialRecord>();
        if (!Directory.Exists(TrialRoot)) return list;

        var dirs = campaignId is null
            ? Directory.EnumerateDirectories(TrialRoot)
            : Directory.Exists(TrialDir(campaignId)) ? new[] { TrialDir(campaignId) } : Array.Empty<string>();

        foreach (var dir in dirs)
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
            {
                var t = await ReadAsync<TrialRecord>(f, ct);
                if (t is not null) list.Add(t);
            }

        return list.OrderBy(t => t.CreatedUtc).ThenBy(t => t.Id, StringComparer.Ordinal).ToList();
    }

    public async Task<CampaignRecord> ConsumeHoldoutAsync(string campaignId, string? evaluationReference = null, CancellationToken ct = default)
    {
        // Prüfen UND Verbrauchen unter einer Sperre — sonst könnten zwei parallele finale Auswertungen
        // den (nur einmal gültigen) Holdout beide „unberührt" vorfinden und beide auswerten.
        await _lock.WaitAsync(ct);
        try
        {
            var campaign = await GetCampaignAsync(campaignId, ct)
                ?? throw new ExperimentRegistryException("CAMPAIGN_UNKNOWN", $"Kampagne '{campaignId}' existiert nicht.");
            if (campaign.HoldoutFrom is null && campaign.HoldoutTo is null)
                throw new ExperimentRegistryException("NO_HOLDOUT", "Für diese Kampagne ist kein Holdout definiert.");
            if (campaign.HoldoutConsumed)
                throw new ExperimentRegistryException("HOLDOUT_CONSUMED",
                    "Der finale Holdout dieser Kampagne wurde bereits ausgewertet und ist verbraucht.");

            var updated = campaign with
            {
                HoldoutConsumed = true,
                HoldoutConsumedUtc = DateTimeOffset.UtcNow,
                HoldoutEvaluatedReference = evaluationReference ?? campaign.HoldoutEvaluatedReference
            };
            await WriteAsync(CampaignPath(campaignId), updated, ct);
            return updated;
        }
        finally { _lock.Release(); }
    }

    public async Task<HoldoutEvaluationRecord?> GetHoldoutEvaluationAsync(string campaignId, CancellationToken ct = default)
    {
        var path = HoldoutPath(campaignId);
        return File.Exists(path) ? await ReadAsync<HoldoutEvaluationRecord>(path, ct) : null;
    }

    public async Task<HoldoutEvaluationRecord> ReserveHoldoutEvaluationAsync(string campaignId, HoldoutEvaluationRecord reserved, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reserved);

        // Prüfen UND Reservieren unter EINER Sperre: sonst könnten zwei parallele Requests beide einen freien
        // Holdout vorfinden und je einen Lauf starten. Ein bereits existierender Auswertungssatz ODER ein
        // gesetztes Verbrauch-Flag der Kampagne blockiert jede weitere Reservierung (kein verstecktes Retry).
        await _lock.WaitAsync(ct);
        try
        {
            var campaign = await GetCampaignAsync(campaignId, ct)
                ?? throw new ExperimentRegistryException("CAMPAIGN_UNKNOWN", $"Kampagne '{campaignId}' existiert nicht.");
            if (campaign.HoldoutFrom is null && campaign.HoldoutTo is null)
                throw new ExperimentRegistryException("NO_HOLDOUT", "Für diese Kampagne ist kein Holdout definiert.");
            if (File.Exists(HoldoutPath(campaignId)))
                throw new ExperimentRegistryException("HOLDOUT_CONSUMED",
                    "Für diese Kampagne existiert bereits eine finale Holdout-Auswertung; sie kann nicht erneut gestartet werden.");
            if (campaign.HoldoutConsumed)
                throw new ExperimentRegistryException("HOLDOUT_CONSUMED",
                    "Der finale Holdout dieser Kampagne wurde bereits verbraucht und kann nicht erneut ausgewertet werden.");

            var record = reserved with { CampaignId = campaignId, Status = HoldoutEvaluationStatus.Reserved };
            // Erst den Auswertungssatz schreiben, dann den Verbrauch-Flag der Kampagne setzen — beides atomar
            // unter der Sperre, gebunden an die eingefrorene Konfiguration (kein zweiter Verbrauchspfad).
            await WriteAsync(HoldoutPath(campaignId), record, ct);
            var updated = campaign with
            {
                HoldoutConsumed = true,
                HoldoutConsumedUtc = DateTimeOffset.UtcNow,
                HoldoutEvaluatedReference = record.Config.CandidateReference
            };
            await WriteAsync(CampaignPath(campaignId), updated, ct);
            return record;
        }
        finally { _lock.Release(); }
    }

    public async Task<HoldoutEvaluationRecord> UpdateHoldoutEvaluationAsync(HoldoutEvaluationRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(HoldoutPath(record.CampaignId)))
                throw new ExperimentRegistryException("HOLDOUT_UNKNOWN",
                    $"Für Kampagne '{record.CampaignId}' existiert keine reservierte Holdout-Auswertung.");
            // Der einmalige Verbrauch-Flag der Kampagne wird hier NIE zurückgesetzt.
            await WriteAsync(HoldoutPath(record.CampaignId), record, ct);
            return record;
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Schützt den finalen Holdout: Versuche dürfen ihn nur mit ausgewiesener Rolle "holdout"
    /// berühren, und nur solange er nicht verbraucht ist. Ein Trainings-/Validierungszeitraum, der
    /// in den Holdout hineinragt, wird abgelehnt (Leakage).
    /// </summary>
    private static void GuardHoldout(CampaignRecord campaign, TrialRecord trial)
    {
        if (campaign.HoldoutFrom is null && campaign.HoldoutTo is null) return;

        bool isHoldoutRole = string.Equals(trial.PeriodRole, "holdout", StringComparison.OrdinalIgnoreCase);
        bool overlaps = Overlaps(trial.PeriodFrom, trial.PeriodTo, campaign.HoldoutFrom, campaign.HoldoutTo);

        if (isHoldoutRole)
        {
            if (campaign.HoldoutConsumed)
                throw new ExperimentRegistryException("HOLDOUT_CONSUMED",
                    "Der finale Holdout wurde bereits ausgewertet; weitere Holdout-Läufe sind gesperrt.");
            return;
        }

        if (overlaps)
            throw new ExperimentRegistryException("HOLDOUT_LEAKAGE",
                $"Versuchszeitraum überschneidet den finalen Holdout ({campaign.HoldoutFrom:u} – {campaign.HoldoutTo:u}), " +
                $"ist aber als '{trial.PeriodRole}' deklariert. Holdout nur mit PeriodRole='holdout' auswerten.");
    }

    private static bool Overlaps(DateTimeOffset? aFrom, DateTimeOffset? aTo, DateTimeOffset? bFrom, DateTimeOffset? bTo)
    {
        var a1 = aFrom ?? DateTimeOffset.MinValue;
        var a2 = aTo ?? DateTimeOffset.MaxValue;
        var b1 = bFrom ?? DateTimeOffset.MinValue;
        var b2 = bTo ?? DateTimeOffset.MaxValue;
        return a1 < b2 && b1 < a2;
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(value, Json), ct);
        File.Move(tmp, path, overwrite: true);
    }

    private static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<T>(text, Json);
    }

    /// <summary>Dateinamen dürfen keine Pfadanteile enthalten; Ids werden entsprechend entschärft.</summary>
    private static string Sanitize(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = id.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim();
        return string.IsNullOrEmpty(s) ? "_" : s;
    }
}
