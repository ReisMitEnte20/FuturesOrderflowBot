using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Strategies.OrderFlow;

// ------------------------------------------------------------------------------------------------
//  Mehrstrategie-FUNKTIONSNACHWEIS (technischer Test) — 1:1 aus dem bisherigen Harness portiert.
//  Reine Signalregeln auf OrderFlowBars; KEINE Profitabilitaetsaussage, KEINE Orders.
//  Die Strategien sind IOrderFlowStrategy, damit BacktestStrategyRunner sie automatisch mit dem
//  OrderFlowBarAggregatorStrategy-Wrapper versieht (Ticks -> OrderFlowBars -> OnOrderFlowBar).
//  Signal gilt fuer die GESCHLOSSENE Bar; Ausfuehrung/Position/Kosten/EndOfData entscheidet allein
//  die bestehende BacktestEngine (keine strategie-eigene Ausfuehrung).
// ------------------------------------------------------------------------------------------------

/// <summary>Preis-Momentum: Close &gt; Open =&gt; Long, Close &lt; Open =&gt; Short, Doji =&gt; KEIN Signal (Position halten).</summary>
public sealed class MomentumPriceStrategy : IOrderFlowStrategy
{
    public string Name => "Momentum(Price)";
    public StrategyDataRequirements DataRequirements => new() { NeedsOrderFlowBars = true };

    public TradeSignal? OnOrderFlowBar(OrderFlowBar b) =>
        b.Close == b.Open
            ? null
            : MultiStrategyProofSignal.For(Name, b, b.Close > b.Open ? SignalDirection.Long : SignalDirection.Short);
}

/// <summary>Preis-Fade (Gegenrichtung zu Momentum): Close &gt; Open =&gt; Short, Close &lt; Open =&gt; Long, Doji =&gt; KEIN Signal.</summary>
public sealed class FadePriceStrategy : IOrderFlowStrategy
{
    public string Name => "Fade(Price)";
    public StrategyDataRequirements DataRequirements => new() { NeedsOrderFlowBars = true };

    public TradeSignal? OnOrderFlowBar(OrderFlowBar b) =>
        b.Close == b.Open
            ? null
            : MultiStrategyProofSignal.For(Name, b, b.Close > b.Open ? SignalDirection.Short : SignalDirection.Long);
}

/// <summary>
/// Orderflow-Delta: Delta &gt; +Schwelle =&gt; Long, Delta &lt; -Schwelle =&gt; Short, sonst EXPLIZIT Flat
/// (Position schliessen). Nutzt echtes Bid/Ask-Delta der Bar. Schwelle aus Config-Parameter
/// "DeltaThreshold" (Default 30), damit der Wert mit dem Lauf gespeichert werden kann.
/// </summary>
public sealed class DeltaFlowStrategy : IOrderFlowStrategy
{
    public const decimal DefaultDeltaThreshold = 30m;
    public const string ThresholdParameterKey = "DeltaThreshold";

    private readonly decimal _threshold;

    public DeltaFlowStrategy(decimal deltaThreshold = DefaultDeltaThreshold) => _threshold = deltaThreshold;

    public decimal DeltaThreshold => _threshold;
    public string Name => "DeltaFlow(OF)";
    public StrategyDataRequirements DataRequirements => new() { NeedsOrderFlowBars = true };

    /// <summary>Liest die Schwelle aus der StrategyConfig (Parameter "DeltaThreshold"), sonst Default.</summary>
    public static decimal ThresholdFrom(StrategyConfig config)
    {
        if (config?.Parameters != null
            && config.Parameters.TryGetValue(ThresholdParameterKey, out var raw)
            && decimal.TryParse(raw, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            return v;
        return DefaultDeltaThreshold;
    }

    public TradeSignal? OnOrderFlowBar(OrderFlowBar b) =>
        b.Delta > _threshold ? MultiStrategyProofSignal.For(Name, b, SignalDirection.Long)
        : b.Delta < -_threshold ? MultiStrategyProofSignal.For(Name, b, SignalDirection.Short)
        : MultiStrategyProofSignal.For(Name, b, SignalDirection.Flat);
}

/// <summary>Baut das reine TradeSignal fuer eine geschlossene Bar (wie im Harness: Referenzpreis = Close).</summary>
internal static class MultiStrategyProofSignal
{
    public static TradeSignal For(string name, OrderFlowBar b, SignalDirection dir) => new()
    {
        StrategyName = name,
        Symbol = b.Symbol,
        Direction = dir,
        Timestamp = b.CloseTime,
        ReferencePrice = b.Close,
        SuggestedQuantity = 1
    };
}
