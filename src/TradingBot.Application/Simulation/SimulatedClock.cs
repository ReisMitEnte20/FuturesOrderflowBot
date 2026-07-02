using TradingBot.Core.Interfaces;

namespace TradingBot.Application.Simulation;

/// <summary>
/// Deterministische Uhr für Simulationen (Backtest UND Paper-Replay).
/// Wird von der Engine auf den aktuellen Tick-Zeitstempel gesetzt.
/// </summary>
public sealed class SimulatedClock : IClock
{
    public SimulatedClock(DateTimeOffset start) => UtcNow = start;
    public DateTimeOffset UtcNow { get; private set; }
    public void Set(DateTimeOffset now) => UtcNow = now;
}
