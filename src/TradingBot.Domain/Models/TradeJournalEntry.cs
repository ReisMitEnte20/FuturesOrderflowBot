using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>Journal-Eintrag, der einen abgeschlossenen Trade mit Kontext festhält.</summary>
public sealed record TradeJournalEntry
{
    public Guid EntryId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; }
    public TradingMode Mode { get; init; }

    public required Trade Trade { get; init; }

    /// <summary>Freitext-Notizen (z. B. Marktkontext, Setup-Qualität).</summary>
    public string Notes { get; init; } = string.Empty;
}
