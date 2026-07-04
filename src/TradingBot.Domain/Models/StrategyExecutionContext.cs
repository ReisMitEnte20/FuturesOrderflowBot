namespace TradingBot.Domain.Models;

/// <summary>
/// Kontext, den eine Strategie bei der Initialisierung erhält (reine Daten).
/// Enthält KEINE Order-/Broker-/Risk-Referenzen – Strategien erzeugen nur Signale.
/// </summary>
public sealed record StrategyExecutionContext
{
    public required string Symbol { get; init; }

    /// <summary>Instrument-Spezifikation (TickSize/TickValue etc.) – nur lesend nutzbar.</summary>
    public InstrumentProfile? Instrument { get; init; }

    /// <summary>Konfiguration dieser Strategie-Instanz.</summary>
    public StrategyConfig? Config { get; init; }

    /// <summary>Handelstag der Session (informativ).</summary>
    public DateOnly? TradingDate { get; init; }
}
