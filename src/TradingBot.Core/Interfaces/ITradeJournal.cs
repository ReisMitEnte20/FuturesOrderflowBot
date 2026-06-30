using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>Persistiert abgeschlossene Trades samt Kontext für Auswertung/Audit.</summary>
public interface ITradeJournal
{
    Task RecordAsync(TradeJournalEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Alle Journal-Einträge eines Tages.</summary>
    Task<IReadOnlyList<TradeJournalEntry>> GetEntriesAsync(DateOnly date, CancellationToken cancellationToken = default);
}
