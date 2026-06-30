using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>
/// Überwacht den MarketData-Heartbeat: trackt den letzten Tick und meldet den Feed als
/// überaltert/disconnected, wenn zu lange kein Tick kommt. Fail-closed: ohne Tick bzw.
/// bei unklarem Zustand gilt der Feed als NICHT gesund. Liefert später dem SafetyMonitor
/// den Feed-Status, damit der RiskManager bei Feed-Abbruch blockt.
/// </summary>
public interface IFeedHealthMonitor
{
    /// <summary>Aktueller (neu bewerteter) Verbindungszustand.</summary>
    MarketDataConnectionState State { get; }

    /// <summary>True nur, wenn der Feed verbunden und nicht überaltert ist.</summary>
    bool IsHealthy { get; }

    /// <summary>Markiert den Feed als verbunden/getrennt (z. B. Connect/Disconnect des Providers).</summary>
    void SetConnected(bool connected);

    /// <summary>Registriert einen empfangenen Tick (aktualisiert Heartbeat).</summary>
    void RecordTick(MarketTick tick);
}
