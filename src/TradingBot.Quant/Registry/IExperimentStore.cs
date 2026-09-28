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

    /// <summary>
    /// Reserviert mehrere Versuche ATOMAR: Budget- und Holdout-Prüfung sowie das Schreiben aller
    /// Versuche geschehen unter einer einzigen Sperre. Passt die Gesamtzahl nicht mehr ins Budget,
    /// wird KEIN Versuch geschrieben (parallele Requests können das Budget so nicht überschreiten).
    /// Die reservierten Versuche werden üblicherweise mit Status <see cref="TrialStatus.Running"/>
    /// angelegt und nach dem Lauf per <see cref="UpdateTrialAsync"/> finalisiert.
    /// </summary>
    Task<IReadOnlyList<TrialRecord>> ReserveTrialsAsync(string campaignId, IReadOnlyList<TrialRecord> trials, CancellationToken ct = default);

    /// <summary>Aktualisiert einen bestehenden Versuch (z. B. Status/Ergebnisse nach dem Lauf).</summary>
    Task<TrialRecord> UpdateTrialAsync(TrialRecord trial, CancellationToken ct = default);

    Task<TrialRecord?> GetTrialAsync(string trialId, CancellationToken ct = default);

    /// <summary>Alle Versuche, optional auf eine Kampagne eingegrenzt. Chronologisch aufsteigend.</summary>
    Task<IReadOnlyList<TrialRecord>> ListTrialsAsync(string? campaignId = null, CancellationToken ct = default);

    /// <summary>
    /// Verbraucht den finalen Holdout einer Kampagne ATOMAR und EINMALIG: Prüfen und Reservieren
    /// geschehen unter einer Sperre. Der Verbrauch wird an eine konkrete Auswertung gebunden
    /// (<paramref name="evaluationReference"/>, z. B. Kandidat + Konfiguration). Ein zweiter oder
    /// paralleler Aufruf schlägt fehl — auch eine reine Einsicht verbraucht den Holdout, es gibt kein
    /// „Nachsehen ohne Verbrauch". Danach sind weitere Holdout-Auswertungen gesperrt.
    /// </summary>
    Task<CampaignRecord> ConsumeHoldoutAsync(string campaignId, string? evaluationReference = null, CancellationToken ct = default);
}
