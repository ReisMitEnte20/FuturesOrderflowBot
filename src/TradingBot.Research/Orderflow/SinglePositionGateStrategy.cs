using TradingBot.Application.Strategies.OrderFlow;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Research.Orderflow;

/// <summary>
/// LOKALE Lauf-Anpassung (keine Aenderung der Strategieregeln, kein globaler OrderManager-Eingriff):
/// begrenzt eine innere OrderFlow-Strategie auf HOECHSTENS EINEN Kontrakt und unterdrueckt das Aufstocken
/// (Add) bei wiederholten gleichgerichteten Signalen — genau wie der urspruengliche Harness ("desired position").
///
/// Modelliert dabei die DOKUMENTIERTE, konservative Engine-Semantik (OrderIntentClassifier):
///  - gleiche Richtung, bereits offen -> KEIN Signal (halten, kein Add)
///  - Flat -> schliessen (nur wenn offen)
///  - Gegenrichtung -> Signal durchreichen; die Engine SCHLIESST zunaechst nur (kein Auto-Flip),
///    das Eroeffnen der Gegenposition erfolgt per Folgesignal. Das Modell bildet das ab (Short-auf-Long => Flat).
/// Die inneren Signalregeln bleiben unveraendert; der Gate entscheidet nur, OB ein Signal weitergereicht wird.
/// </summary>
public sealed class SinglePositionGateStrategy : IOrderFlowStrategy
{
    private readonly IStrategy _inner;
    private PositionSide _modelled = PositionSide.Flat;

    public SinglePositionGateStrategy(IStrategy inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public string Name => _inner.Name;
    public StrategyDataRequirements DataRequirements => new() { NeedsOrderFlowBars = true };

    public void Initialize(StrategyExecutionContext context) { _inner.Initialize(context); _modelled = PositionSide.Flat; }
    public void Reset() { _inner.Reset(); _modelled = PositionSide.Flat; }

    public TradeSignal? OnOrderFlowBar(OrderFlowBar bar)
    {
        var sig = _inner.OnOrderFlowBar(bar);
        if (sig is null) return null; // z. B. Doji -> halten, keine Positionsaenderung

        switch (sig.Direction)
        {
            case SignalDirection.Long:
                if (_modelled == PositionSide.Long) return null;                 // schon long -> halten, KEIN Add
                _modelled = _modelled == PositionSide.Short ? PositionSide.Flat   // Short -> Engine schliesst nur -> Flat
                                                            : PositionSide.Long;  // Flat  -> Long eroeffnet
                return sig;

            case SignalDirection.Short:
                if (_modelled == PositionSide.Short) return null;                // schon short -> halten, KEIN Add
                _modelled = _modelled == PositionSide.Long ? PositionSide.Flat    // Long -> Engine schliesst nur -> Flat
                                                           : PositionSide.Short;  // Flat -> Short eroeffnet
                return sig;

            default: // Flat
                if (_modelled == PositionSide.Flat) return null;                 // nichts zu schliessen
                _modelled = PositionSide.Flat;
                return sig;
        }
    }
}
