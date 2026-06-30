using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>
/// Stellt FeeProfile bereit, eindeutig je Broker + ExecutionProvider + Instrument.
/// Keine Order darf ohne gültiges FeeProfile erzeugt werden.
/// </summary>
public interface IFeeProfileProvider
{
    /// <summary>Liefert das passende Fee-Profil oder null, wenn keine Kombination passt.</summary>
    Task<FeeProfile?> GetAsync(
        string brokerName, string executionProvider, string instrument,
        CancellationToken cancellationToken = default);

    /// <summary>Alle bekannten Fee-Profile.</summary>
    Task<IReadOnlyCollection<FeeProfile>> GetAllAsync(CancellationToken cancellationToken = default);
}
