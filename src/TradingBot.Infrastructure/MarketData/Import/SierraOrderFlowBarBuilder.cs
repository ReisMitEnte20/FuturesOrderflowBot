using System.Globalization;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.MarketData.Import;

/// <summary>Ein aus Ticks aggregiertes Preislevel (echte Footprint-Zeile, kein DOM/Level 2).</summary>
public sealed record SierraFootprintLevel(decimal Price, decimal Volume, decimal BidVolume, decimal AskVolume)
{
    public decimal Delta => AskVolume - BidVolume;
}

/// <summary>
/// Intrabar-Momentaufnahme (forming candle) beim Durchspielen der Ticks: zeigt, wie sich die aktuelle
/// Bar aus den historischen Ticks aufbaut (High/Low/Close/Volume/Bid/Ask/Delta/CVD wachsen intrabar).
/// </summary>
public sealed record SierraIntrabarFrame
{
    public long TickIndex { get; init; }
    public DateTimeOffset TickTimeUtc { get; init; }
    public decimal CurrentPrice { get; init; }
    public int CompletedBars { get; init; }          // Anzahl bereits finalisierter Bars vor dieser
    public DateTimeOffset FormingOpenTime { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public decimal Volume { get; init; }
    public decimal BidVolume { get; init; }
    public decimal AskVolume { get; init; }
    public decimal Delta => AskVolume - BidVolume;
    public decimal CumulativeDelta { get; init; }    // inkl. der sich bildenden Bar
    public double BarProgressPercent { get; init; }
}

/// <summary>
/// Eine streamend gebaute Time-<see cref="OrderFlowBar"/> plus Sierra-Zusatzinfos
/// (NumberOfTrades, optionale Footprint-Preislevels). MinPrice/MaxPrice == Bar.Low/High.
/// </summary>
public sealed record SierraOrderFlowBar
{
    public required OrderFlowBar Bar { get; init; }
    public decimal NumberOfTrades { get; init; }
    public IReadOnlyList<SierraFootprintLevel> PriceLevels { get; init; } = Array.Empty<SierraFootprintLevel>();

    public decimal MinPrice => Bar.Low;
    public decimal MaxPrice => Bar.High;
    public decimal Delta => Bar.Delta;
}

/// <summary>Read-only Report der Sierra-Tick→OrderFlowBar-Aggregation.</summary>
public sealed record SierraAggregationResult
{
    public long FileSizeBytes { get; init; } = -1;
    public long RowsProcessed { get; init; }
    public long ValidTicks { get; init; }
    public long ParseErrors { get; init; }
    public bool Truncated { get; init; }

    public int BarsCreated => Bars.Count;
    public IReadOnlyList<SierraOrderFlowBar> Bars { get; init; } = Array.Empty<SierraOrderFlowBar>();

    public DateTimeOffset? FirstTickTime { get; init; }
    public DateTimeOffset? LastTickTime { get; init; }
    public DateTimeOffset? FirstBarTime { get; init; }
    public DateTimeOffset? LastBarTime { get; init; }

    public decimal TotalVolume { get; init; }
    public decimal SumBidVolume { get; init; }
    public decimal SumAskVolume { get; init; }
    public decimal NetDelta => SumAskVolume - SumBidVolume;
    public decimal FinalCumulativeDelta { get; init; }

    public decimal? MinPrice { get; init; }
    public decimal? MaxPrice { get; init; }

    public SierraGranularity Granularity { get; init; } = SierraGranularity.AggregatedOrUnknown;
    public bool IsSingleTick => Granularity == SierraGranularity.SingleTick;

    public OrderFlowCapabilities Capabilities { get; init; } = OrderFlowCapabilities.None;
    public bool SupportsDomLevel2 => false;

    public IReadOnlyList<DataQualityIssue> Issues { get; init; } = Array.Empty<DataQualityIssue>();
}

/// <summary>
/// Aggregiert Sierra-1-Tick-Daten STREAMEND zu Time-<see cref="OrderFlowBar"/>s (kein
/// <c>ReadAllText</c>, kein <c>ToList()</c> über die Datei; nur die aktuelle Bar wird gepuffert →
/// konstanter RAM). Bid/Ask-Volumen, Delta, CVD, NumberOfTrades und optionale Footprint-Preislevels
/// werden AUS DEN TICKS aggregiert — nichts erfunden. DOM/Level 2 bleibt false.
///
/// Fehlt der Header oder eine Pflichtspalte (date, time, last, volume) → Fehler. Einzelne kaputte
/// Zeilen erhöhen nur ParseErrors. Capabilities werden ehrlich aus den tatsächlichen Daten gesetzt.
/// </summary>
public sealed class SierraOrderFlowBarBuilder
{
    private const int ProgressEvery = 100_000;
    private const int MaxStoredIssues = 50;

    private readonly CsvImportProfile _profile;

    public SierraOrderFlowBarBuilder(CsvImportProfile? profile = null)
        => _profile = profile ?? CsvImportProfile.Default(MarketDataSourceType.AggressorTick);

    public SierraAggregationResult BuildFile(
        string path, string symbol, TimeSpan barInterval, long? maxRows = null,
        DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null,
        bool buildFootprint = true, Action<long>? onProgress = null,
        int frameEveryTicks = 0, Action<SierraIntrabarFrame>? onFrame = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Datei nicht gefunden: '{path}'.", path);
        long size = new FileInfo(path).Length;
        using var reader = new StreamReader(path);
        return Build(reader, symbol, barInterval, maxRows, fromUtc, toUtc, buildFootprint, onProgress, frameEveryTicks, onFrame)
            with { FileSizeBytes = size };
    }

    public SierraAggregationResult Build(
        TextReader reader, string symbol, TimeSpan barInterval, long? maxRows = null,
        DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null,
        bool buildFootprint = true, Action<long>? onProgress = null,
        int frameEveryTicks = 0, Action<SierraIntrabarFrame>? onFrame = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol muss angegeben werden (Sierra-CSV hat keine Symbol-Spalte).", nameof(symbol));
        if (barInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(barInterval), "barInterval muss > 0 sein.");

        string? header = ReadNonEmptyLine(reader) ?? throw new CsvMarketDataException("Leere Datei oder fehlende Kopfzeile.");
        var columns = IndexColumns(header);
        RequireColumns(columns, "date", "time", "last", "volume");

        bool hasBidAsk = Has(columns, "bidvolume") && Has(columns, "askvolume");
        bool hasNumTrades = Has(columns, "numberoftrades");

        long rows = 0, valid = 0, parseErrors = 0, unclassified = 0;
        bool truncated = false, allSingleTrade = hasNumTrades;
        decimal totalVol = 0m, sumBid = 0m, sumAsk = 0m, cumulativeDelta = 0m;
        decimal? minPrice = null, maxPrice = null;
        DateTimeOffset? firstTick = null, lastTick = null, previous = null;
        var issues = new List<DataQualityIssue>();
        var bars = new List<SierraOrderFlowBar>();
        BarAccumulator? acc = null;

        void AddIssue(DataQualityIssue i) { if (issues.Count < MaxStoredIssues) issues.Add(i); }
        void Flush() { if (acc is not null) { bars.Add(acc.Build(symbol, ref cumulativeDelta, buildFootprint)); acc = null; } }

        string? line;
        int lineNo = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (maxRows is long max && rows >= max) { truncated = true; break; }
            rows++;

            var f = SplitTrim(line);
            var ts = CombineTimestamp(f, columns);
            var price = Dec(f, columns, "last");
            var volume = Dec(f, columns, "volume");

            if (ts is null || price is null || price <= 0m || volume is null || volume < 0m)
            {
                parseErrors++;
                AddIssue(CsvGrid.Error("ParseError", "Zeile nicht verwertbar (ts/last/volume).", lineNo));
                continue;
            }
            if (previous is not null && ts < previous)
            {
                AddIssue(CsvGrid.Warning("NonChronological", $"Zeitstempel {ts:O} vor Vorgänger {previous:O}.", lineNo, ts));
                continue; // out-of-order Tick nicht in Bars mischen
            }
            previous = ts;

            decimal numTrades = hasNumTrades ? Dec(f, columns, "numberoftrades") ?? 0m : 0m;
            if (!(hasNumTrades && numTrades == 1m)) allSingleTrade = false;

            decimal bid = hasBidAsk ? Dec(f, columns, "bidvolume") ?? 0m : 0m;
            decimal ask = hasBidAsk ? Dec(f, columns, "askvolume") ?? 0m : 0m;
            var aggressor = Classify(hasBidAsk, bid, ask);
            if (hasBidAsk && aggressor == AggressorSide.Unknown) unclassified++;

            if (rows % ProgressEvery == 0) onProgress?.Invoke(rows);

            // Zeitfilter (optional) – gefilterte Ticks zählen als verarbeitet, aber nicht aggregiert.
            if (fromUtc is not null && ts < fromUtc) continue;
            if (toUtc is not null && ts > toUtc) continue;

            valid++;
            totalVol += volume.Value;
            sumBid += bid;
            sumAsk += ask;
            minPrice = minPrice is null ? price : Math.Min(minPrice.Value, price.Value);
            maxPrice = maxPrice is null ? price : Math.Max(maxPrice.Value, price.Value);
            firstTick ??= ts;
            lastTick = ts;

            var bucket = BucketStart(ts.Value, barInterval);
            if (acc is null || bucket != acc.BucketStart)
            {
                Flush();
                acc = new BarAccumulator(bucket, bucket + barInterval);
            }
            acc.Add(price.Value, volume.Value, bid, ask, numTrades > 0m ? numTrades : 1m, aggressor);

            // Intrabar-Frame (optional, gesampelt): Momentaufnahme der sich bildenden Bar.
            if (frameEveryTicks > 0 && onFrame is not null && valid % frameEveryTicks == 0)
                onFrame(acc.Snapshot(valid, bars.Count, cumulativeDelta, ts.Value, barInterval));
        }
        Flush();

        bool singleTick = allSingleTrade && valid > 0;
        if (singleTick)
            AddIssue(CsvGrid.Info("SierraSingleTick", "1-Tick-Export (NumberOfTrades == 1) – geeignet für Orderflow-Forschung."));
        else
            AddIssue(CsvGrid.Warning("SierraAggregatedRecords",
                "Aggregiert oder Granularität unbekannt (NumberOfTrades > 1 bzw. Spalte fehlt) – KEINE Tick-Garantie."));
        if (hasBidAsk && valid > 0 && unclassified > 0)
            AddIssue(CsvGrid.Warning("PartialClassification",
                $"{unclassified} Ticks ohne eindeutige Bid/Ask-Klassifikation – Delta/CVD NICHT erlaubt."));

        bool fullyClassified = hasBidAsk && valid > 0 && unclassified == 0;
        var caps = fullyClassified
            ? new OrderFlowCapabilities
            {
                SupportsDeltaCvd = true,
                SupportsAbsorption = true,
                SupportsBarImbalance = true,
                // Footprint-Preislevels wurden ECHT aus Ticks aggregiert → Stacked-Imbalance möglich.
                SupportsStackedImbalances = buildFootprint && bars.Count > 0,
                SupportsHvnLvn = false // kein Session-Volume-Profile gebaut
            }
            : OrderFlowCapabilities.None;

        return new SierraAggregationResult
        {
            RowsProcessed = rows,
            ValidTicks = valid,
            ParseErrors = parseErrors,
            Truncated = truncated,
            Bars = bars,
            FirstTickTime = firstTick,
            LastTickTime = lastTick,
            FirstBarTime = bars.Count > 0 ? bars[0].Bar.OpenTime : null,
            LastBarTime = bars.Count > 0 ? bars[^1].Bar.OpenTime : null,
            TotalVolume = totalVol,
            SumBidVolume = sumBid,
            SumAskVolume = sumAsk,
            FinalCumulativeDelta = cumulativeDelta,
            MinPrice = minPrice,
            MaxPrice = maxPrice,
            Granularity = singleTick ? SierraGranularity.SingleTick : SierraGranularity.AggregatedOrUnknown,
            Capabilities = caps,
            Issues = issues
        };
    }

    /// <summary>Untere Intervallgrenze (UTC-getaktet ab DateTime-Epoche), z. B. volle Minute.</summary>
    private static DateTimeOffset BucketStart(DateTimeOffset ts, TimeSpan interval)
    {
        long iv = interval.Ticks;
        long start = ts.UtcTicks - (ts.UtcTicks % iv);
        return new DateTimeOffset(start, TimeSpan.Zero);
    }

    private static AggressorSide Classify(bool hasBidAsk, decimal bid, decimal ask)
    {
        if (!hasBidAsk) return AggressorSide.Unknown;
        if (ask > 0m && bid == 0m) return AggressorSide.Buy;
        if (bid > 0m && ask == 0m) return AggressorSide.Sell;
        return AggressorSide.Unknown;
    }

    // ---- Streaming-Akkumulator einer einzelnen Bar (nur die aktuelle Bar liegt im RAM) ----
    private sealed class BarAccumulator
    {
        public DateTimeOffset BucketStart { get; }
        private readonly DateTimeOffset _closeTime;
        private decimal _open, _close, _high, _low, _total, _bid, _ask, _numTrades;
        private bool _first = true;
        private readonly Dictionary<decimal, (decimal Bid, decimal Ask)> _levels = new();

        public BarAccumulator(DateTimeOffset bucketStart, DateTimeOffset closeTime)
        { BucketStart = bucketStart; _closeTime = closeTime; }

        public void Add(decimal price, decimal volume, decimal bid, decimal ask, decimal numTrades, AggressorSide side)
        {
            if (_first) { _open = _high = _low = price; _first = false; }
            if (price > _high) _high = price;
            if (price < _low) _low = price;
            _close = price;
            _total += volume;
            _bid += bid;
            _ask += ask;
            _numTrades += numTrades;

            // Footprint aus Ticks: Volumen dem Bid oder Ask am Preislevel zuordnen (echte Aggregation).
            decimal addBid = side == AggressorSide.Sell ? volume : 0m;
            decimal addAsk = side == AggressorSide.Buy ? volume : 0m;
            var cur = _levels.TryGetValue(price, out var v) ? v : (0m, 0m);
            _levels[price] = (cur.Item1 + addBid, cur.Item2 + addAsk);
        }

        public SierraIntrabarFrame Snapshot(long tickIndex, int completedBars, decimal finalizedCvd, DateTimeOffset tickTime, TimeSpan interval)
        {
            double prog = interval.Ticks <= 0 ? 0
                : Math.Clamp((double)(tickTime.UtcTicks - BucketStart.UtcTicks) / interval.Ticks * 100.0, 0, 100);
            return new SierraIntrabarFrame
            {
                TickIndex = tickIndex, TickTimeUtc = tickTime, CurrentPrice = _close, CompletedBars = completedBars,
                FormingOpenTime = BucketStart, Open = _open, High = _high, Low = _low, Close = _close,
                Volume = _total, BidVolume = _bid, AskVolume = _ask,
                CumulativeDelta = finalizedCvd + (_ask - _bid), BarProgressPercent = prog
            };
        }

        public SierraOrderFlowBar Build(string symbol, ref decimal cumulativeDelta, bool buildFootprint)
        {
            cumulativeDelta += _ask - _bid;
            var bar = new OrderFlowBar
            {
                Symbol = symbol,
                OpenTime = BucketStart,
                CloseTime = _closeTime,
                Open = _open, High = _high, Low = _low, Close = _close,
                TotalVolume = _total,
                BidVolume = _bid,
                AskVolume = _ask,
                CumulativeDelta = cumulativeDelta
            };

            IReadOnlyList<SierraFootprintLevel> levels = Array.Empty<SierraFootprintLevel>();
            if (buildFootprint)
                levels = _levels.OrderBy(kv => kv.Key)
                    .Select(kv => new SierraFootprintLevel(kv.Key, kv.Value.Bid + kv.Value.Ask, kv.Value.Bid, kv.Value.Ask))
                    .ToList();

            return new SierraOrderFlowBar { Bar = bar, NumberOfTrades = _numTrades, PriceLevels = levels };
        }
    }

    // ------------------------- CSV-Helper (streaming) -------------------------

    private static string? ReadNonEmptyLine(TextReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) is not null)
            if (!string.IsNullOrWhiteSpace(line)) return line;
        return null;
    }

    private Dictionary<string, int> IndexColumns(string headerLine)
    {
        var parts = SplitTrim(headerLine);
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0) map[parts[i]] = i;
        return map;
    }

    private bool Has(Dictionary<string, int> columns, string logical) => columns.ContainsKey(_profile.Column(logical));

    private void RequireColumns(Dictionary<string, int> columns, params string[] logicalFields)
    {
        var missing = logicalFields.Where(x => !columns.ContainsKey(_profile.Column(x)))
            .Select(x => $"'{_profile.Column(x)}' (Feld '{x}')").ToList();
        if (missing.Count > 0)
            throw new CsvMarketDataException("Pflichtspalte(n) fehlen im CSV-Header: " + string.Join(", ", missing));
    }

    private string[] SplitTrim(string line)
    {
        var raw = line.Split(_profile.Delimiter);
        for (int i = 0; i < raw.Length; i++) raw[i] = raw[i].Trim();
        return raw;
    }

    private string? Raw(string[] fields, Dictionary<string, int> columns, string logical)
    {
        if (!columns.TryGetValue(_profile.Column(logical), out int i) || i >= fields.Length) return null;
        var v = fields[i];
        return v.Length == 0 ? null : v;
    }

    private decimal? Dec(string[] fields, Dictionary<string, int> columns, string logical)
    {
        var raw = Raw(fields, columns, logical);
        if (raw is null) return null;
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private DateTimeOffset? CombineTimestamp(string[] fields, Dictionary<string, int> columns)
    {
        var date = Raw(fields, columns, "date");
        var time = Raw(fields, columns, "time");
        if (date is null || time is null) return null;
        return DateTimeOffset.TryParse($"{date} {time}", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var ts)
            ? ts.ToUniversalTime() : null;
    }
}
