using System.Globalization;
using TradingBot.Domain.Models;

namespace TradingBot.Backtesting.Ohlc;

/// <summary>Ein beanstandeter Datensatz beim OHLC-Import (nachvollziehbar, ohne stilles Auffüllen).</summary>
public sealed record OhlcImportIssue(int Line, string Code, string Message);

/// <summary>Ergebnis des OHLC-Imports: geprüfte Kerzen + Qualitätsreport.</summary>
public sealed record OhlcImportResult
{
    public IReadOnlyList<Candle> Candles { get; init; } = Array.Empty<Candle>();
    public string Symbol { get; init; } = "";
    public int TimeframeMinutes { get; init; }
    public string Timezone { get; init; } = "UTC";
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }

    public int RowsRead { get; init; }
    public int ValidRows { get; init; }
    public int Rejected { get; init; }
    public bool HasVolume { get; init; }

    /// <summary>Bei reinen OHLC-CSV nicht erkennbar (false). Nur die Sierra-Aggregation setzt Teilkerzen.</summary>
    public bool LeadingPartial { get; init; }
    public bool TrailingPartial { get; init; }

    public IReadOnlyList<OhlcImportIssue> Issues { get; init; } = Array.Empty<OhlcImportIssue>();
}

/// <summary>
/// Importiert historische OHLCV-Bars aus CSV (Header-basiert). Prüft Zeitordnung, Duplikate,
/// Zahlenformat und OHLC-Konsistenz und meldet ungültige Datensätze. Erzeugt KEINE Volumina/Kurse
/// und füllt KEINE Lücken. Zeitzone: Zeitstempel werden als UTC interpretiert (bzw. mit Offset gelesen).
/// Sierra-TICK-Dateien sind hiermit NICHT zu lesen (dort sind High/Low Ask/Bid) — dafür die
/// bestehende Sierra→OrderFlowBar-Aggregation (Last-Preise) verwenden.
/// </summary>
public sealed class OhlcCsvImporter
{
    private const int MaxIssues = 200;

    public OhlcImportResult ImportFile(string path, string symbol, int? timeframeMinutesOverride = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"OHLC-Datei nicht gefunden: '{path}'.", path);
        using var reader = new StreamReader(path);
        return Import(reader, symbol, timeframeMinutesOverride);
    }

    public OhlcImportResult Import(TextReader reader, string symbol, int? timeframeMinutesOverride = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("Symbol muss angegeben werden.", nameof(symbol));

        string? headerLine = ReadNonEmpty(reader) ?? throw new InvalidDataException("Leere Datei oder fehlender Header.");
        var cols = MapColumns(headerLine.Split(','));
        RequireColumns(cols);

        var issues = new List<OhlcImportIssue>();
        void AddIssue(int line, string code, string msg) { if (issues.Count < MaxIssues) issues.Add(new OhlcImportIssue(line, code, msg)); }

        var candles = new List<Candle>();
        int rows = 0, valid = 0, rejected = 0, lineNo = 1;
        bool hasVol = cols.ContainsKey("volume");
        DateTimeOffset? prev = null;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            rows++;
            var f = line.Split(',');

            var ts = ParseTimestamp(f, cols);
            var o = ParseDec(f, cols, "open");
            var h = ParseDec(f, cols, "high");
            var l = ParseDec(f, cols, "low");
            var c = ParseDec(f, cols, "close");
            decimal vol = hasVol ? (ParseDec(f, cols, "volume") ?? 0m) : 0m;

            if (ts is null || o is null || h is null || l is null || c is null)
            { rejected++; AddIssue(lineNo, "ParseError", "Zeitstempel oder O/H/L/C nicht lesbar."); continue; }
            if (o <= 0m || h <= 0m || l <= 0m || c <= 0m)
            { rejected++; AddIssue(lineNo, "NonPositive", "O/H/L/C müssen > 0 sein."); continue; }

            decimal open = o.Value, high = h.Value, low = l.Value, close = c.Value;
            if (high < low || high < open || high < close || low > open || low > close)
            { rejected++; AddIssue(lineNo, "OhlcInconsistent", $"OHLC inkonsistent (O={open} H={high} L={low} C={close})."); continue; }

            if (prev is not null && ts < prev)
            { rejected++; AddIssue(lineNo, "NonChronological", $"Zeitstempel {ts:O} vor Vorgänger {prev:O}."); continue; }
            if (prev is not null && ts == prev)
            { rejected++; AddIssue(lineNo, "DuplicateTimestamp", $"Doppelter Zeitstempel {ts:O}."); continue; }
            prev = ts;

            valid++;
            candles.Add(new Candle
            {
                Symbol = symbol,
                OpenTime = ts.Value,
                CloseTime = ts.Value,   // wird nach dem Lauf um den Timeframe ergänzt
                Open = open, High = high, Low = low, Close = close, Volume = vol
            });
        }

        int tf = timeframeMinutesOverride ?? InferTimeframeMinutes(candles);
        // CloseTime = OpenTime + Timeframe (konsistent zu den Zeit-Bars des Sierra-Builders).
        for (int i = 0; i < candles.Count; i++)
            candles[i] = candles[i] with { CloseTime = candles[i].OpenTime.AddMinutes(tf) };

        return new OhlcImportResult
        {
            Candles = candles,
            Symbol = symbol,
            TimeframeMinutes = tf,
            Timezone = "UTC",
            From = candles.Count > 0 ? candles[0].OpenTime : null,
            To = candles.Count > 0 ? candles[^1].CloseTime : null,
            RowsRead = rows,
            ValidRows = valid,
            Rejected = rejected,
            HasVolume = hasVol,
            LeadingPartial = false,
            TrailingPartial = false,
            Issues = issues
        };
    }

    private static int InferTimeframeMinutes(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 2) return 1;
        var deltas = new List<double>();
        for (int i = 1; i < candles.Count && i < 500; i++)
        {
            var d = (candles[i].OpenTime - candles[i - 1].OpenTime).TotalMinutes;
            if (d > 0) deltas.Add(d);
        }
        if (deltas.Count == 0) return 1;
        deltas.Sort();
        double median = deltas[deltas.Count / 2];
        int tf = (int)Math.Round(median);
        return tf <= 0 ? 1 : tf;
    }

    // ---- Helfer ----
    private static string? ReadNonEmpty(TextReader r)
    {
        string? l;
        while ((l = r.ReadLine()) is not null) if (!string.IsNullOrWhiteSpace(l)) return l;
        return null;
    }

    private static Dictionary<string, int> MapColumns(string[] header)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Length; i++)
        {
            string h = header[i].Trim().ToLowerInvariant();
            string? key = h switch
            {
                "timestamp" or "datetime" or "date_time" or "time_stamp" => "timestamp",
                "date" => "date",
                "time" => "time",
                "open" or "o" => "open",
                "high" or "h" => "high",
                "low" or "l" => "low",
                "close" or "c" or "last" => "close",
                "volume" or "vol" or "v" => "volume",
                _ => null
            };
            if (key is not null && !map.ContainsKey(key)) map[key] = i;
        }
        return map;
    }

    private static void RequireColumns(Dictionary<string, int> cols)
    {
        bool hasTime = cols.ContainsKey("timestamp") || (cols.ContainsKey("date") && cols.ContainsKey("time")) || cols.ContainsKey("date");
        var missing = new List<string>();
        if (!hasTime) missing.Add("timestamp | date[,time]");
        foreach (var k in new[] { "open", "high", "low", "close" }) if (!cols.ContainsKey(k)) missing.Add(k);
        if (missing.Count > 0)
            throw new InvalidDataException("Pflichtspalten fehlen im CSV-Header: " + string.Join(", ", missing));
    }

    private static DateTimeOffset? ParseTimestamp(string[] f, Dictionary<string, int> cols)
    {
        string? raw = null;
        if (cols.TryGetValue("timestamp", out int ti) && ti < f.Length) raw = f[ti].Trim();
        else if (cols.TryGetValue("date", out int di) && di < f.Length)
        {
            raw = f[di].Trim();
            if (cols.TryGetValue("time", out int tmi) && tmi < f.Length) raw += " " + f[tmi].Trim();
        }
        if (string.IsNullOrEmpty(raw)) return null;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var ts)
            ? ts.ToUniversalTime() : (DateTimeOffset?)null;
    }

    private static decimal? ParseDec(string[] f, Dictionary<string, int> cols, string key)
    {
        if (!cols.TryGetValue(key, out int i) || i >= f.Length) return null;
        var v = f[i].Trim();
        return v.Length == 0 ? null
            : decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
