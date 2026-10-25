using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.MarketData.Import;

/// <summary>
/// Ergebnis des Sierra-Adapters: die in das bestehende Domain-Modell überführten Daten
/// (<see cref="Dataset"/> = <see cref="ImportedMarketDataSet"/> mit OrderFlowBars + Quality +
/// Capabilities) plus der volle Streaming-Report inkl. Footprint-Preislevels (<see cref="Aggregation"/>).
/// </summary>
public sealed record SierraMarketDataResult
{
    public required ImportedMarketDataSet Dataset { get; init; }
    public required SierraAggregationResult Aggregation { get; init; }
}

/// <summary>
/// Überführt streamend aggregierte Sierra-OrderFlowBars in das bestehende Import-Domain-Modell
/// (<see cref="ImportedMarketDataSet"/>), damit sie als LOKALE, read-only Datenquelle für
/// Backtest/Research vorbereitet sind. Reine Adapter-/Mapping-Schicht:
///
/// - Keine Broker-API, keine Live-Execution, keine echten Orders, keine Netzwerkcalls.
/// - Keine Dashboard- oder <c>TradingBot.Execution</c>-Abhängigkeit (nur Domain + Sierra-Builder).
/// - Streaming/Chunking bleibt erhalten (delegiert an <see cref="SierraOrderFlowBarBuilder"/>).
/// - <see cref="OrderFlowCapabilities"/> und Datenqualität werden EHRLICH aus den Ticks abgeleitet;
///   ohne echte Bid/Ask-Volumen entsteht kein Fake-Orderflow (Capabilities = None).
/// </summary>
public sealed class SierraMarketDataAdapter
{
    private readonly SierraOrderFlowBarBuilder _builder;

    public SierraMarketDataAdapter(CsvImportProfile? profile = null)
        => _builder = new SierraOrderFlowBarBuilder(profile);

    public SierraMarketDataResult LoadFromFile(
        string path, string symbol, TimeSpan barSize, long? maxRows = null,
        DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null,
        bool buildFootprint = true, Action<long>? onProgress = null)
    {
        var agg = _builder.BuildFile(path, symbol, barSize, maxRows, fromUtc, toUtc, buildFootprint, onProgress);
        return new SierraMarketDataResult { Dataset = ToDataset(symbol, agg), Aggregation = agg };
    }

    public SierraMarketDataResult Load(
        TextReader reader, string symbol, TimeSpan barSize, long? maxRows = null,
        DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null,
        bool buildFootprint = true, Action<long>? onProgress = null)
    {
        var agg = _builder.Build(reader, symbol, barSize, maxRows, fromUtc, toUtc, buildFootprint, onProgress);
        return new SierraMarketDataResult { Dataset = ToDataset(symbol, agg), Aggregation = agg };
    }

    /// <summary>Wie <see cref="LoadFromFile"/>, aber Range-Bars (Bar-Wechsel bei Preisbewegung statt Zeit).</summary>
    public SierraMarketDataResult LoadRangeFromFile(
        string path, string symbol, decimal rangeSize, long? maxRows = null,
        DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null,
        bool buildFootprint = true, Action<long>? onProgress = null)
    {
        var agg = _builder.BuildRangeFile(path, symbol, rangeSize, maxRows, fromUtc, toUtc, buildFootprint, onProgress);
        return new SierraMarketDataResult { Dataset = ToDataset(symbol, agg), Aggregation = agg };
    }

    /// <summary>Wie <see cref="Load"/>, aber Range-Bars (Bar-Wechsel bei Preisbewegung statt Zeit).</summary>
    public SierraMarketDataResult LoadRange(
        TextReader reader, string symbol, decimal rangeSize, long? maxRows = null,
        DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null,
        bool buildFootprint = true, Action<long>? onProgress = null)
    {
        var agg = _builder.BuildRange(reader, symbol, rangeSize, maxRows, fromUtc, toUtc, buildFootprint, onProgress);
        return new SierraMarketDataResult { Dataset = ToDataset(symbol, agg), Aggregation = agg };
    }

    /// <summary>
    /// Streamt die rohen, klassifizierten Sierra-Ticks (für Replay/Backtest/Research) aus einer Datei.
    /// Liefert <see cref="MarketTick"/>, die direkt an <see cref="Infrastructure.MarketData.ReplayMarketDataProvider"/>
    /// übergeben werden können — so fliessen echte Sierra-OrderFlow-Daten in die Backtest-/Research-Pipeline,
    /// ohne neue Architektur-Abhängigkeiten. Keine Broker-API, keine Live-Execution, kein Fake-Orderflow.
    /// </summary>
    public IReadOnlyList<MarketTick> StreamTicksFromFile(
        string path, string symbol, long? maxRows = null,
        DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null,
        Action<long>? onProgress = null)
        => SierraOrderFlowBarBuilder.StreamTicksFile(path, symbol, maxRows, fromUtc, toUtc, onProgress);

    /// <summary>Wie <see cref="StreamTicksFromFile"/>, aber über TextReader (Tests).</summary>
    public IReadOnlyList<MarketTick> StreamTicks(
        TextReader reader, string symbol, long? maxRows = null,
        DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null,
        Action<long>? onProgress = null)
        => SierraOrderFlowBarBuilder.StreamTicks(reader, symbol, maxRows, fromUtc, toUtc, onProgress);

    /// <summary>
    /// Importiert NUR das UTC-Fenster [fromUtc, toUtc] einer (grossen) Sierra-Datei als <see cref="ImportedMarketDataSet"/>
    /// (Ticks + Qualitaet + EHRLICHE Capabilities) — streamend und fenster-begrenzt BEIM LESEN, d. h. die Gesamtdatei
    /// wird nicht materialisiert. Dies ist der Konsolen-/Research-Eingang fuer Sierra (parallel zu
    /// <see cref="AtasTickCsvImporter"/>), ohne neue Engine. Capabilities werden ehrlich aus der Klassifikation
    /// abgeleitet (Delta/CVD nur bei voller Aggressor-Klassifikation). KEINE Quote-/DOM-Faehigkeit: BidSize/AskSize
    /// sind AUSGEFUEHRTE Volumina, keine ruhenden Orderbuchmengen.
    /// </summary>
    public ImportedMarketDataSet ImportWindowedSet(
        string path, string symbol, DateTimeOffset fromUtc, DateTimeOffset toUtc, Action<long>? onProgress = null)
    {
        var r = SierraOrderFlowBarBuilder.StreamTicksWindowedFile(path, symbol, fromUtc, toUtc, onProgress);

        var issues = new List<DataQualityIssue>();
        if (!r.HasBidAsk)
            issues.Add(CsvGrid.Warning("NoBidAskVolume",
                "Spalten BidVolume/AskVolume fehlen – keine Aggressor-Klassifikation, keine Delta/CVD-Capability."));
        if (r.HasBidAsk && r.Unclassified > 0)
            issues.Add(CsvGrid.Warning("PartialClassification",
                $"{r.Unclassified} von {r.Ticks.Count} Ticks im Fenster ohne eindeutige Bid/Ask-Klassifikation – Delta/CVD NICHT erlaubt."));
        if (r.Rejected > 0)
            issues.Add(CsvGrid.Warning("RejectedRows",
                $"{r.Rejected} Zeile(n) im Fenster abgelehnt (ungueltiger Zeitstempel/Preis/Volumen)."));
        issues.Add(CsvGrid.Info("SierraWindowedStream",
            $"Gefensterter Streaming-Import [{fromUtc:O} .. {toUtc:O}] (UTC): {r.Ticks.Count} Ticks akzeptiert; BidSize/AskSize = ausgefuehrte Volumina (keine ruhenden Quotes/DOM)."));

        bool fullyClassified = r.HasBidAsk && r.Ticks.Count > 0 && r.Unclassified == 0;
        var sourceType = fullyClassified ? MarketDataSourceType.AggressorTick : MarketDataSourceType.MinimalTick;

        return new ImportedMarketDataSet
        {
            SourceType = sourceType,
            Symbol = symbol,
            Ticks = r.Ticks,
            Quality = new OrderFlowDataQualityReport
            {
                SourceType = sourceType,
                RowsRead = ClampInt(r.RowsReadInWindow + r.Rejected),
                RowsAccepted = r.Ticks.Count,
                Issues = issues
            },
            Capabilities = fullyClassified
                ? new OrderFlowCapabilities { SupportsDeltaCvd = true, SupportsAbsorption = true, SupportsBarImbalance = true }
                : OrderFlowCapabilities.None
        };
    }

    /// <summary>Mappt den Aggregations-Report auf das kanonische <see cref="ImportedMarketDataSet"/>.</summary>
    private static ImportedMarketDataSet ToDataset(string symbol, SierraAggregationResult agg)
    {
        var bars = new List<OrderFlowBar>(agg.Bars.Count);
        foreach (var b in agg.Bars) bars.Add(b.Bar);   // OHLC, Volume, Bid/Ask, Delta, CumulativeDelta

        return new ImportedMarketDataSet
        {
            SourceType = MarketDataSourceType.OrderFlowBars,
            Symbol = symbol,
            OrderFlowBars = bars,
            Quality = new OrderFlowDataQualityReport
            {
                SourceType = MarketDataSourceType.OrderFlowBars,
                RowsRead = ClampInt(agg.RowsProcessed),
                RowsAccepted = ClampInt(agg.ValidTicks),
                Issues = agg.Issues
            },
            Capabilities = agg.Capabilities
        };
    }

    private static int ClampInt(long v) => v > int.MaxValue ? int.MaxValue : (int)v;
}
