using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>
/// Führt registrierte, AKTIVE Strategien auf Marktdaten-Events aus und sammelt deren Signale.
/// Trifft KEINE Risk-Entscheidung, sendet KEINE Orders, hat KEINE Broker-/Execution-Referenz –
/// die Signale werden später vom OrderManager (nach RiskManager-Prüfung) verarbeitet.
/// Deterministisch: Strategien werden in Registrierungs-Reihenfolge ausgewertet.
/// </summary>
public interface IStrategyEngine
{
    /// <summary>Initialisiert alle registrierten Strategien mit Kontext (Instrument etc.).</summary>
    void Initialize(StrategyExecutionContext context);

    /// <summary>Tick an alle aktiven Tick-Strategien des Symbols verteilen.</summary>
    IReadOnlyList<StrategyEvaluationResult> OnTick(MarketTick tick);

    /// <summary>Kerze an alle aktiven Candle-Strategien des Symbols verteilen.</summary>
    IReadOnlyList<StrategyEvaluationResult> OnCandle(Candle candle);

    /// <summary>
    /// Orderflow-Bar an aktive OrderFlow-Strategien verteilen. Bars ohne echte
    /// Bid/Ask-Klassifikation werden fail-closed NICHT verteilt (keine Fake-Signale).
    /// </summary>
    IReadOnlyList<StrategyEvaluationResult> OnOrderFlowBar(OrderFlowBar bar);

    /// <summary>Alle bisher gesammelten Signale (chronologisch).</summary>
    IReadOnlyList<TradeSignal> CollectedSignals { get; }

    /// <summary>Laufzeit-Zustand aller registrierten Strategien.</summary>
    IReadOnlyList<StrategyRuntimeState> States { get; }

    /// <summary>Setzt Engine-Zähler, gesammelte Signale und Strategie-Zustände zurück.</summary>
    void Reset();
}
