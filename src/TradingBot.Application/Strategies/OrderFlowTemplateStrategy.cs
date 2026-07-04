using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Strategies;

/// <summary>
/// LEERES Template für spätere echte Orderflow-Strategien (Delta-Divergenz, Absorption,
/// Stacked Imbalances, CVD …). Enthält bewusst KEINE Handelslogik und erzeugt NIEMALS
/// ein Signal – insbesondere keines aus Bars ohne echte Bid/Ask-Klassifikation.
///
/// Einbau einer echten Strategie später:
/// 1. Von diesem Template kopieren, Name/Config festlegen (RequiredDataType = OrderFlow).
/// 2. In <see cref="OnOrderFlowBar"/> die Orderflow-Logik implementieren
///    (bar.Delta, bar.BidVolume/AskVolume, bar.CumulativeDelta) und bei Setup ein
///    TradeSignal zurückgeben – NIEMALS eine Order bauen oder senden.
/// 3. Über StrategyRegistry registrieren; RiskManager/OrderManager bleiben unverändert.
/// </summary>
public sealed class OrderFlowTemplateStrategy : IStrategy
{
    private StrategyExecutionContext? _context;

    public string Name => "OrderFlowTemplateStrategy";

    public void Initialize(StrategyExecutionContext context) => _context = context;

    public TradeSignal? OnOrderFlowBar(OrderFlowBar bar)
    {
        // Fail-closed: ohne echte Klassifikation ist Orderflow-Analyse unmöglich -> kein Signal.
        if (bar.TotalVolume > 0m && bar.BidVolume + bar.AskVolume <= 0m)
            return null;

        // TODO (spätere Phase): echte Orderflow-Logik, z. B.
        //   - Delta-Divergenz: Preis-Hoch ohne Delta-Hoch
        //   - Absorption: hohes Volumen am Level ohne Preisfortschritt
        //   - Stacked Imbalances / CVD-Bestätigung
        // Bis dahin: bewusst KEIN Signal.
        return null;
    }

    public void Reset() { /* kein interner Zustand im Template */ }
}
