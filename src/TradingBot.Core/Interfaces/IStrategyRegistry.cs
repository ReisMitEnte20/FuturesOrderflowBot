using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>Unveränderliche Beschreibung einer registrierten Strategie.</summary>
public sealed record StrategyDescriptor
{
    public required IStrategy Strategy { get; init; }
    public required StrategyConfig Config { get; init; }
    public string Name => Config.Name;
}

/// <summary>Momentaufnahme des Laufzeit-Zustands einer registrierten Strategie.</summary>
public sealed record StrategyRuntimeState
{
    public required string Name { get; init; }
    public bool Enabled { get; init; }
    public int SignalsGenerated { get; init; }
    public DateTimeOffset? LastSignalAt { get; init; }
}

/// <summary>
/// Verwaltet registrierte Strategien und ihren Aktiv-Status. Doppelte Namen sind
/// verboten (eindeutige Identität pro Strategie-Instanz). Kein Order-/Broker-Zugriff.
/// </summary>
public interface IStrategyRegistry
{
    /// <summary>Registriert eine Strategie mit Config. Wirft bei doppeltem Namen.</summary>
    void Register(IStrategy strategy, StrategyConfig config);

    /// <summary>Aktiviert eine Strategie. False, wenn unbekannt.</summary>
    bool Enable(string name);

    /// <summary>Deaktiviert eine Strategie – sie wird danach nicht mehr aufgerufen. False, wenn unbekannt.</summary>
    bool Disable(string name);

    bool IsEnabled(string name);
    StrategyDescriptor? Get(string name);
    IReadOnlyList<StrategyDescriptor> All { get; }
}
