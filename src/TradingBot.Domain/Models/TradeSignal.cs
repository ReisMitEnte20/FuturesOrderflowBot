using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>
/// Reines Strategie-Signal. WICHTIG: Ein Signal ist KEINE Order und enthält
/// keine Ausführungsdetails. Erst RiskManager + OrderManager entscheiden, ob und
/// wie daraus eine Order wird. Mengen-/Stop-Angaben sind nur Vorschläge.
/// </summary>
public sealed record TradeSignal
{
    public Guid SignalId { get; init; } = Guid.NewGuid();
    public required string StrategyName { get; init; }
    public required string Symbol { get; init; }
    public SignalDirection Direction { get; init; }
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Referenzpreis zum Signalzeitpunkt (nur informativ).</summary>
    public decimal ReferencePrice { get; init; }

    /// <summary>Optionale Konfidenz 0..1 (Strategie-intern).</summary>
    public decimal Confidence { get; init; }

    /// <summary>Vorgeschlagene Kontraktanzahl. Finale Menge entscheidet der RiskManager.</summary>
    public int? SuggestedQuantity { get; init; }

    public int? SuggestedStopLossTicks { get; init; }
    public int? SuggestedTakeProfitTicks { get; init; }

    /// <summary>
    /// Optionaler ABSOLUTER Stop-Loss-Preis (statt Ticks) — für Strategien, die den Stop an ein Kursniveau binden
    /// (z. B. das Gegenextrem der Opening-Range). Nur ein Vorschlag; der OHLC-Backtest nutzt ihn für dynamische
    /// Stop-/Zielpreise und leitet R aus dem TATSÄCHLICHEN Entry-Fill ab. Fehlt er, gelten die Tick-Vorgaben.
    /// </summary>
    public decimal? SuggestedStopLossPrice { get; init; }

    /// <summary>Optionales Ziel als R-Vielfaches (Ziel = Entry ± R·|Entry − StopPreis|, R aus dem echten Fill).
    /// Nur zusammen mit <see cref="SuggestedStopLossPrice"/> wirksam.</summary>
    public decimal? SuggestedTakeProfitRMultiple { get; init; }

    /// <summary>Zeitgesteuerter Exit: offene Position am SCHLUSS DIESES Bars glattstellen (z. B. Session-Ende).
    /// Kein Fill am nächsten Bar/Handelstag. Richtung wird dann ignoriert.</summary>
    public bool FlattenAtClose { get; init; }

    /// <summary>Erwartete OPEN-Zeit des Ausführungs-Bars. Ist sie gesetzt und der nächste Bar hat eine ANDERE
    /// Open-Zeit (z. B. fehlende Folgekerze/Lücke), wird der Entry VERWORFEN statt später/am nächsten Tag gefüllt.</summary>
    public DateTimeOffset? EntryBarOpenTime { get; init; }

    /// <summary>Spätester Zeitpunkt, zu dem die eröffnete Position glattgestellt sein MUSS (z. B. Session-Ende, UTC).
    /// Läuft ein Bar nach diesem Zeitpunkt, obwohl die Position noch offen ist (fehlender Session-Schluss-Bar), wird
    /// der Lauf als unvollständig gekennzeichnet und die Position am ersten Bar danach geschlossen (kein stiller
    /// Übernacht-Lauf, kein erfundener Schlusskurs).</summary>
    public DateTimeOffset? FlattenDeadline { get; init; }

    /// <summary>Kurzbegründung des Signals (z. B. "Liquidity Sweep + Absorption + Delta-Reversal").</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Bedingungen, die dieses Signal ausgelöst haben (für Auswertung/Journal).</summary>
    public IReadOnlyList<string> TriggeredConditions { get; init; } = Array.Empty<string>();

    /// <summary>Bedingungen, die geprüft wurden, aber NICHT erfüllt waren (Diagnose).</summary>
    public IReadOnlyList<string> FailedConditions { get; init; } = Array.Empty<string>();

    /// <summary>Optionale Debug-Notizen (z. B. Checks ohne ausreichende Datenbasis).</summary>
    public string? DebugNotes { get; init; }
}
