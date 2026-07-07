using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>
/// Ergebnis eines Marktdaten-Imports: die Daten, der Qualitätsbericht und die daraus
/// abgeleiteten <see cref="OrderFlowCapabilities"/> (was die Daten EHRLICH erlauben).
/// Je nach <see cref="SourceType"/> ist genau eine der Listen befüllt.
/// </summary>
public sealed record ImportedMarketDataSet
{
    public required MarketDataSourceType SourceType { get; init; }
    public string Symbol { get; init; } = string.Empty;

    public IReadOnlyList<MarketTick> Ticks { get; init; } = Array.Empty<MarketTick>();
    public IReadOnlyList<OrderFlowBar> OrderFlowBars { get; init; } = Array.Empty<OrderFlowBar>();
    public IReadOnlyList<FootprintBar> FootprintBars { get; init; } = Array.Empty<FootprintBar>();
    public IReadOnlyList<VolumeProfile> VolumeProfiles { get; init; } = Array.Empty<VolumeProfile>();

    public required OrderFlowDataQualityReport Quality { get; init; }
    public OrderFlowCapabilities Capabilities { get; init; } = OrderFlowCapabilities.None;
}
