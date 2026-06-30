using System.Globalization;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.MarketData;

/// <summary>
/// Liest MarketData-Ticks aus CSV. Header-basiertes Spalten-Mapping (case-insensitive).
/// Pflichtspalten: timestamp, symbol, price, volume. Optional (Orderflow):
/// bid, ask, tradedirection, bidvolume, askvolume.
///
/// Validierung (fail-closed, keine stillen Fehler): Preis &gt; 0, Volumen ≥ 0, gültiger
/// Zeitstempel/Symbol, optional chronologische Reihenfolge. Aggressor wird NUR aus echten
/// Daten gesetzt (TradeDirection bzw. eindeutiges Bid-/Ask-Volumen) – niemals erfunden.
/// </summary>
public static class CsvTickReader
{
    public static IReadOnlyList<MarketTick> ReadFile(string path, bool validateChronological = true)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Pfad darf nicht leer sein.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException($"CSV-Datei nicht gefunden: '{path}'.", path);

        using var reader = new StreamReader(path);
        return Read(reader, validateChronological);
    }

    public static IReadOnlyList<MarketTick> Read(TextReader reader, bool validateChronological = true)
    {
        ArgumentNullException.ThrowIfNull(reader);

        IReadOnlyDictionary<string, int>? columns = null;
        var ticks = new List<MarketTick>();
        DateTimeOffset? previous = null;

        string? line;
        int lineNo = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue; // Leer-/Kommentarzeilen

            if (columns is null)
            {
                columns = MapColumns(line);
                RequireColumn(columns, "timestamp");
                RequireColumn(columns, "symbol");
                RequireColumn(columns, "price");
                RequireColumn(columns, "volume");
                continue;
            }

            var tick = ParseRow(line.Split(','), columns, lineNo);

            if (validateChronological && previous is not null && tick.Timestamp < previous.Value)
                throw new CsvMarketDataException(
                    $"Ticks nicht chronologisch ({tick.Timestamp:O} vor {previous.Value:O}).", lineNo);

            previous = tick.Timestamp;
            ticks.Add(tick);
        }

        if (columns is null)
            throw new CsvMarketDataException("Leere Datei oder fehlende Kopfzeile.");
        if (ticks.Count == 0)
            throw new CsvMarketDataException("Keine Datenzeilen gefunden.");

        return ticks;
    }

    private static MarketTick ParseRow(string[] fields, IReadOnlyDictionary<string, int> col, int lineNo)
    {
        string symbol = Field(fields, col, "symbol", lineNo);
        if (string.IsNullOrWhiteSpace(symbol))
            throw new CsvMarketDataException("Symbol darf nicht leer sein.", lineNo);

        var timestamp = ParseTimestamp(Field(fields, col, "timestamp", lineNo), lineNo);
        decimal price = ParseDecimal(Field(fields, col, "price", lineNo), "Preis", lineNo);
        decimal volume = ParseDecimal(Field(fields, col, "volume", lineNo), "Volumen", lineNo);

        if (price <= 0m)
            throw new CsvMarketDataException($"Ungültiger Preis '{price}' (muss > 0 sein).", lineNo);
        if (volume < 0m)
            throw new CsvMarketDataException($"Ungültiges Volumen '{volume}' (darf nicht negativ sein).", lineNo);

        decimal bid = OptionalDecimal(fields, col, "bid", lineNo);
        decimal ask = OptionalDecimal(fields, col, "ask", lineNo);
        decimal bidVolume = OptionalDecimal(fields, col, "bidvolume", lineNo);
        decimal askVolume = OptionalDecimal(fields, col, "askvolume", lineNo);

        var aggressor = ClassifyAggressor(fields, col, bidVolume, askVolume, lineNo);

        return new MarketTick
        {
            Symbol = symbol,
            Timestamp = timestamp,
            Price = price,
            Volume = volume,
            Bid = bid,
            Ask = ask,
            Aggressor = aggressor
        };
    }

    private static AggressorSide ClassifyAggressor(
        string[] fields, IReadOnlyDictionary<string, int> col, decimal bidVolume, decimal askVolume, int lineNo)
    {
        if (col.TryGetValue("tradedirection", out int di) && di < fields.Length)
        {
            string raw = fields[di].Trim().ToLowerInvariant();
            return raw switch
            {
                "buy" or "b" or "ask" or "1" or "+1" => AggressorSide.Buy,
                "sell" or "s" or "bid" or "-1" => AggressorSide.Sell,
                "" or "unknown" or "none" or "n" or "0" => AggressorSide.Unknown,
                _ => throw new CsvMarketDataException($"Unbekannte TradeDirection '{raw}'.", lineNo)
            };
        }

        // Kein TradeDirection: nur ableiten, wenn genau eine Seite Volumen trägt (sonst Unknown – nicht raten).
        if (askVolume > 0m && bidVolume == 0m) return AggressorSide.Buy;
        if (bidVolume > 0m && askVolume == 0m) return AggressorSide.Sell;
        return AggressorSide.Unknown;
    }

    // ---- Hilfsfunktionen -----------------------------------------------------

    private static IReadOnlyDictionary<string, int> MapColumns(string headerLine)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parts = headerLine.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            var name = parts[i].Trim().ToLowerInvariant();
            if (name.Length > 0) map[name] = i;
        }
        return map;
    }

    private static void RequireColumn(IReadOnlyDictionary<string, int> columns, string name)
    {
        if (!columns.ContainsKey(name))
            throw new CsvMarketDataException($"Pflichtspalte '{name}' fehlt im CSV-Header.");
    }

    private static string Field(string[] fields, IReadOnlyDictionary<string, int> col, string name, int lineNo)
    {
        int i = col[name];
        if (i >= fields.Length)
            throw new CsvMarketDataException($"Spalte '{name}' fehlt in dieser Zeile.", lineNo);
        return fields[i].Trim();
    }

    private static DateTimeOffset ParseTimestamp(string raw, int lineNo)
    {
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var ts))
            return ts;
        throw new CsvMarketDataException($"Ungültiger Zeitstempel '{raw}'.", lineNo);
    }

    private static decimal ParseDecimal(string raw, string label, int lineNo)
    {
        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
            return d;
        throw new CsvMarketDataException($"Ungültiger {label}-Wert '{raw}'.", lineNo);
    }

    private static decimal OptionalDecimal(string[] fields, IReadOnlyDictionary<string, int> col, string name, int lineNo)
    {
        if (!col.TryGetValue(name, out int i) || i >= fields.Length) return 0m;
        var raw = fields[i].Trim();
        if (raw.Length == 0) return 0m;
        return ParseDecimal(raw, name, lineNo);
    }
}
