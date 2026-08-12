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
