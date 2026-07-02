using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.PaperTrading.Journal;

/// <summary>
/// In-Memory-Implementierung von <see cref="ITradeJournal"/> für Paper-Sessions.
/// Speichert Journal-Einträge thread-safe im Speicher – bewusst ohne DB/Datei, aber sauber
/// testbar. Eine persistente Variante (JSON/DB) kommt in einer späteren Phase.
/// </summary>
public sealed class InMemoryTradeJournal : ITradeJournal
{
    private readonly List<TradeJournalEntry> _entries = new();
    private readonly object _sync = new();

    public IReadOnlyList<TradeJournalEntry> Entries
    {
        get { lock (_sync) return _entries.ToList(); }
    }

    public Task RecordAsync(TradeJournalEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync) _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TradeJournalEntry>> GetEntriesAsync(
        DateOnly date, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            IReadOnlyList<TradeJournalEntry> result = _entries
                .Where(e => DateOnly.FromDateTime(e.Timestamp.UtcDateTime) == date)
                .ToList();
            return Task.FromResult(result);
        }
    }
}
