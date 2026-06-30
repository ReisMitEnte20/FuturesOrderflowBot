namespace TradingBot.Domain.Models;

/// <summary>
/// Vollständiger Kontext für eine RiskManager-Prüfung. Der Aufrufer (OrderManager)
/// löst Profile + Tageszustand auf und übergibt sie hier. Profile sind bewusst
/// nullable, damit der RiskManager fehlende Profile als harte Ablehnung erzwingt
/// (fail-closed) – auch wenn der Aufrufer das Auflösen vergisst.
/// </summary>
public sealed record RiskEvaluationRequest
{
    public required TradeSignal Signal { get; init; }

    /// <summary>Vom Aufrufer gewünschte Kontraktanzahl (muss &gt; 0 sein).</summary>
    public required int RequestedContracts { get; init; }

    public required DailyRiskState DailyState { get; init; }

    public InstrumentProfile? Instrument { get; init; }
    public FeeProfile? Fee { get; init; }
    public BrokerProfile? Broker { get; init; }
    public RiskConfig? Risk { get; init; }

    /// <summary>Anzahl aktuell offener Positionen (über alle Symbole).</summary>
    public int OpenPositionsCount { get; init; }

    /// <summary>Aktuell offene Kontrakte im betroffenen Symbol (für Contract-Room).</summary>
    public int CurrentOpenContracts { get; init; }

    /// <summary>
    /// Ergebnis des Positionsabgleichs lokal↔Broker: true = stimmt überein,
    /// false = Abweichung (→ Ablehnung), null = nicht geprüft (übersprungen).
    /// </summary>
    public bool? PositionReconciled { get; init; }
}
