using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>
/// Eine Trading-Strategie. SICHERHEITSREGEL: Eine Strategie erzeugt ausschließlich
/// <see cref="TradeSignal"/>-Objekte und sendet NIEMALS Orders. Sie hat keinen Zugriff
/// auf Broker, OrderManager oder RiskManager – ein TradeSignal ist KEINE Order;
/// OrderRequests baut ausschließlich der OrderManager nach Risk-Freigabe.
///
/// Alle Handler haben Default-Implementierungen (kein Signal / no-op): eine Strategie
/// implementiert nur die Events, die sie laut RequiredDataType tatsächlich braucht.
/// Aktivieren/Deaktivieren geschieht im Framework (StrategyRegistry/Engine) – eine
/// deaktivierte Strategie wird gar nicht erst aufgerufen.
/// </summary>
public interface IStrategy
{
    string Name { get; }

    /// <summary>Einmalige Initialisierung mit Kontext (Instrument, Config). Default: no-op.</summary>
    void Initialize(StrategyExecutionContext context) { }

    /// <summary>Wertet einen neuen Tick aus. Default: kein Signal.</summary>
    TradeSignal? OnTick(MarketTick tick) => null;

    /// <summary>Wertet eine neue Zeit-/Tick-/Volumen-Kerze aus. Default: kein Signal.</summary>
    TradeSignal? OnCandle(Candle candle) => null;

    /// <summary>
    /// Wertet eine neue Orderflow-Bar aus. Nur mit ECHTEN Bid/Ask/Aggressor-Daten aufrufbar
    /// (die Engine blockt unklassifizierte Bars). Default: kein Signal.
    /// </summary>
    TradeSignal? OnOrderFlowBar(OrderFlowBar bar) => null;

    /// <summary>Setzt internen Zustand zurück (neue Session/Backtest-Lauf). Default: no-op.</summary>
    void Reset() { }
}
