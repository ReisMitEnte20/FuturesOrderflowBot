using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>
/// Single Source of Truth für offene Positionen und PnL. Verarbeitet Fills (auch
/// Teilfüllungen, Teilverkäufe, Flips). Sendet NIEMALS Orders und hat KEINE
/// Execution-Referenz. PnL nutzt FeeCalculator/PnLCalculator; Tick-/Fee-Werte kommen
/// ausschließlich aus den übergebenen Profilen.
/// </summary>
public interface IPositionManager
{
    /// <summary>Aktuelle Position für ein Symbol (inkl. Flat-Snapshot mit realisiertem PnL); null wenn unbekannt.</summary>
    Position? GetPosition(string symbol);

    /// <summary>Alle aktuell OFFENEN Positionen (Side != Flat).</summary>
    IReadOnlyCollection<Position> OpenPositions { get; }

    /// <summary>
    /// Verarbeitet eine (Teil-)Ausführung und gibt die aktualisierte Position zurück.
    /// Profile werden explizit übergeben (deterministisch, kein verstecktes IO).
    /// </summary>
    Position ApplyFill(FillEvent fill, InstrumentProfile instrument, FeeProfile feeProfile);

    /// <summary>Aktualisiert den UnrealizedPnL anhand des aktuellen Preises.</summary>
    Position MarkToMarket(string symbol, decimal lastPrice, InstrumentProfile instrument);

    /// <summary>
    /// Vergleicht die lokale Position mit einer übergebenen Broker-Position.
    /// true = stimmt überein. Holt selbst KEINE Broker-Daten (keine Execution-Referenz).
    /// </summary>
    bool Reconcile(string symbol, Position? brokerPosition);
}
