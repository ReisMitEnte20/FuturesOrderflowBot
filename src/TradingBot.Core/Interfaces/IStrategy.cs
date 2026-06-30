using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>
/// Eine Orderflow-Strategie. SICHERHEITSREGEL: Eine Strategie erzeugt ausschließlich
/// <see cref="TradeSignal"/>-Objekte und sendet NIEMALS Orders. Sie hat keinen Zugriff
/// auf Broker, OrderManager oder RiskManager. Strategie-Logik folgt in späteren Phasen.
/// </summary>
public interface IStrategy
{
    string Name { get; }

    /// <summary>Wertet eine neue Orderflow-Bar aus. Gibt ein Signal zurück oder null.</summary>
    TradeSignal? OnBar(OrderFlowBar bar);

    /// <summary>Wertet einen neuen Tick aus. Gibt ein Signal zurück oder null.</summary>
    TradeSignal? OnTick(MarketTick tick);
}
