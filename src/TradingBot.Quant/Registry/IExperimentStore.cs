namespace TradingBot.Quant.Registry;

/// <summary>Wird ausgelöst, wenn eine Registerregel verletzt wird (Budget, Holdout-Schutz, Sperre).</summary>
public sealed class ExperimentRegistryException : Exception
{
    public string Code { get; }
    public ExperimentRegistryException(string code, string message) : base(message) => Code = code;
}

/// <summary>
/// Persistentes Versuchsregister. Hält Suchkampagnen und alle zugehörigen Versuche — einschließlich
/// negativer, fehlgeschlagener und verworfener. Ohne diese vollständige Erfassung ist die spätere
/// Mehrfachtest-Korrektur (PBO/DSR) nicht sauber möglich.
/// </summary>
public interface IExperimentStore
{
    /// <summary>
    /// Legt eine Kampagne an. Budget, Suchraum und Auswahlkriterium müssen gesetzt sein und werden
    /// mit dem Anlegen gesperrt (Vorab-Registrierung).
    /// </summary>
    Task<CampaignRecord> CreateCampaignAsync(CampaignRecord campaign, CancellationToken ct = default);

    Task<CampaignRecord?> GetCampaignAsync(string campaignId, CancellationToken ct = default);

    Task<IReadOnlyList<CampaignRecord>> ListCampaignsAsync(CancellationToken ct = default);

    /// <summary>
    /// Fügt einen Versuch hinzu. Erzwingt Budget und Holdout-Schutz.
    /// </summary>
    Task<TrialRecord> AddTrialAsync(TrialRecord trial, CancellationToken ct = default);

    /// <summary>Aktualisiert einen bestehenden Versuch (z. B. Status/Ergebnisse nach dem Lauf).</summary>
    Task<TrialRecord> UpdateTrialAsync(TrialRecord trial, CancellationToken ct = default);

    Task<TrialRecord?> GetTrialAsync(string trialId, CancellationToken ct = default);

    /// <summary>Alle Versuche, optional auf eine Kampagne eingegrenzt. Chronologisch aufsteigend.</summary>
    Task<IReadOnlyList<TrialRecord>> ListTrialsAsync(string? campaignId = null, CancellationToken ct = default);

    /// <summary>
    /// Markiert den finalen Holdout einer Kampagne als verbraucht. Danach sind weitere
    /// Holdout-Auswertungen für diese Kampagne gesperrt.
    /// </summary>
    Task<CampaignRecord> ConsumeHoldoutAsync(string campaignId, CancellationToken ct = default);
}
