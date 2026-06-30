using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>
/// Stellt BrokerProfile bereit. Keine Order darf ohne gültiges BrokerProfile erzeugt werden.
/// </summary>
public interface IBrokerProfileProvider
{
    /// <summary>Liefert das Profil zum Broker-Namen oder null, wenn unbekannt.</summary>
    Task<BrokerProfile?> GetAsync(string brokerName, CancellationToken cancellationToken = default);

    /// <summary>Alle bekannten Broker-Profile.</summary>
    Task<IReadOnlyCollection<BrokerProfile>> GetAllAsync(CancellationToken cancellationToken = default);
}
