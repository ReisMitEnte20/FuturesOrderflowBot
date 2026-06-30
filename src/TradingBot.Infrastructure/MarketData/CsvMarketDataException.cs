namespace TradingBot.Infrastructure.MarketData;

/// <summary>
/// Fehler beim Einlesen von MarketData-CSV (Struktur, ungültige Werte, falsche Reihenfolge).
/// Keine stillen Fehler – ungültige Daten werden klar mit Zeilennummer gemeldet.
/// </summary>
public sealed class CsvMarketDataException : Exception
{
    public int? LineNumber { get; }

    public CsvMarketDataException(string message, int? lineNumber = null, Exception? inner = null)
        : base(lineNumber is null ? message : $"Zeile {lineNumber}: {message}", inner)
        => LineNumber = lineNumber;
}
