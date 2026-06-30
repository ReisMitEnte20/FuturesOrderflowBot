using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>
/// Stellt InstrumentProfile bereit. Keine Order darf ohne gültiges InstrumentProfile
/// erzeugt werden.
/// </summary>
public interface IInstrumentProvider
{
    /// <summary>Liefert das Profil zum internen Symbol oder null, wenn unbekannt.</summary>
    Task<InstrumentProfile?> GetAsync(string symbol, CancellationToken cancellationToken = default);

    /// <summary>Alle bekannten Instrument-Profile.</summary>
    Task<IReadOnlyCollection<InstrumentProfile>> GetAllAsync(CancellationToken cancellationToken = default);
}
