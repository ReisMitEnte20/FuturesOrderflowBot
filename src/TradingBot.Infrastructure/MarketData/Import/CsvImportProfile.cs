using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.MarketData.Import;

/// <summary>
/// Mapping-Konfiguration für einen CSV-Import: logisches Feld → tatsächlicher Spaltenname.
/// Damit sind ECHTE ATAS-Header später reine Konfiguration (JSON-ladbar über IConfigService) –
/// es werden keine ATAS-Spaltennamen im Code hardcoded. Die Defaults entsprechen den
/// Formaten unter samples/marketdata/.
///
/// Logische Feldnamen (kanonisch, case-insensitive):
///  Ticks:      timestamp, symbol, price, volume, tradedirection, bidvolume, askvolume
///  Bars:       bartimestamp, symbol, open, high, low, close, volume, bidvolume, askvolume,
///              delta, cumulativedelta
///  Footprint:  bartimestamp, symbol, pricelevel, bidvolumeatprice, askvolumeatprice,
///              totalvolumeatprice, imbalanceratio, isstackedimbalance (+ optional open/high/low/close)
///  Profile:    sessiondate, symbol, pricelevel, volumeatprice, bidvolumeatprice,
///              askvolumeatprice, hvn, lvn
/// </summary>
public sealed record CsvImportProfile
{
    public required MarketDataSourceType SourceType { get; init; }

    /// <summary>Logisches Feld → CSV-Spaltenname (beides case-insensitive).</summary>
    public IReadOnlyDictionary<string, string> ColumnMap { get; init; }
        = new Dictionary<string, string>();

    public char Delimiter { get; init; } = ',';

    /// <summary>Freitext (z. B. "ATAS 5.x Tick-Export").</summary>
    public string? Note { get; init; }

    /// <summary>Liefert den gemappten Spaltennamen oder das logische Feld selbst als Fallback.</summary>
    public string Column(string logicalField)
    {
        foreach (var (key, value) in ColumnMap)
            if (string.Equals(key, logicalField, StringComparison.OrdinalIgnoreCase))
                return value;
        return logicalField;
    }

    /// <summary>Default-Profil: Spaltennamen = logische Feldnamen (Format der Sample-CSVs).</summary>
    public static CsvImportProfile Default(MarketDataSourceType sourceType) => new() { SourceType = sourceType };
}
