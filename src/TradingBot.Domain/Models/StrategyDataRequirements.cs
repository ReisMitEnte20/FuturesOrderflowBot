namespace TradingBot.Domain.Models;

/// <summary>
/// Deklariert, welche Daten eine Strategie benötigt. Fehlen deklarierte Pflichtdaten,
/// darf die Strategie KEIN Signal erzeugen (fail-closed, keine erfundenen Werte).
/// </summary>
public sealed record StrategyDataRequirements
{
    public bool NeedsTicks { get; init; }
    public bool NeedsCandles { get; init; }
    public bool NeedsOrderFlowBars { get; init; }

    /// <summary>Echte Bid/Ask-Volumen-Klassifikation (Aggressor) erforderlich.</summary>
    public bool NeedsBidAskVolume { get; init; }

    public bool NeedsDelta { get; init; }
    public bool NeedsCumulativeDelta { get; init; }

    /// <summary>VWAP wird genutzt (bar-basiert berechenbar; optional).</summary>
    public bool NeedsVwap { get; init; }

    /// <summary>Footprint-Daten (Bid/Ask je Preislevel) – derzeit NICHT verfügbar.</summary>
    public bool NeedsFootprint { get; init; }

    /// <summary>Volume-Profile-Daten (Volumen je Preislevel) – derzeit NICHT verfügbar.</summary>
    public bool NeedsVolumeProfile { get; init; }

    public static readonly StrategyDataRequirements None = new();
}
