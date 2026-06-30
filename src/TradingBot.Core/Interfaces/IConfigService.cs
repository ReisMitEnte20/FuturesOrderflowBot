namespace TradingBot.Core.Interfaces;

/// <summary>
/// Lädt und speichert typisierte Konfiguration (JSON). Keine Geschäftslogik –
/// reine Serialisierung. Quelle aller Broker-/Instrument-/Fee-/Risk-Profile.
/// </summary>
public interface IConfigService
{
    /// <summary>Lädt ein Konfigurationsobjekt aus der angegebenen Quelle.</summary>
    Task<T> LoadAsync<T>(string path, CancellationToken cancellationToken = default);

    /// <summary>Speichert ein Konfigurationsobjekt an die angegebene Quelle.</summary>
    Task SaveAsync<T>(string path, T value, CancellationToken cancellationToken = default);

    /// <summary>Prüft, ob eine Konfigurationsquelle existiert.</summary>
    bool Exists(string path);
}
