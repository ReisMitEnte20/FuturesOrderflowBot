using TradingBot.Domain.Models;

namespace TradingBot.Research;

/// <summary>
/// Vergleich mehrerer Strategie-Kandidaten auf denselben Daten/Profilen. Alle Kosten (Fee/Slippage)
/// und Tick-/Instrument-Werte fließen ein; nichts hardcoded. DataQuality/Capabilities werden als
/// harte Robustheits-Inputs weitergereicht (keine Fake-Orderflow-Daten).
/// </summary>
public sealed record ResearchRequest
{
    public required IReadOnlyList<StrategyCandidate> Candidates { get; init; }

    public required IReadOnlyList<MarketTick> Ticks { get; init; }
    public required string Symbol { get; init; }
    public required InstrumentProfile Instrument { get; init; }
    public required FeeProfile Fee { get; init; }
    public required BrokerProfile Broker { get; init; }
    public required RiskConfig Risk { get; init; }
    public required TradingAccount Account { get; init; }

    public decimal? SlippageTicksOverride { get; init; }

    public bool DataQualityOk { get; init; } = true;
    public bool CapabilitiesSufficient { get; init; } = true;

    public ResearchConfiguration Configuration { get; init; } = new();
}
