using System.Globalization;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Strategies.OrderFlow;

/// <summary>
/// Parameter des Orderflow-Templates. Alle Werte kommen aus StrategyConfig.Parameters
/// (Key/Value) – die Defaults hier sind sichere Demo-Defaults und IMMER überschreibbar.
/// Nichts, was in die Config gehört, ist im Strategie-Code hardcoded.
/// </summary>
public sealed record OrderFlowTemplateParameters
{
    // Basis-Filter
    public decimal MinDelta { get; init; } = 0m;              // Mindest-|Delta| der aktuellen Bar
    public decimal MinVolume { get; init; } = 0m;             // Mindest-Volumen der aktuellen Bar

    // Confirmations
    public decimal ImbalanceRatio { get; init; } = 2.0m;      // Bar-Level Ask/Bid- bzw. Bid/Ask-Verhältnis
    public decimal AbsorptionThreshold { get; init; } = 2.0m; // Volumen-Faktor vs. Durchschnitt
    public int AbsorptionMaxRangeTicks { get; init; } = 8;    // "viel Volumen, wenig Bewegung"
    public decimal VolumeSpikeFactor { get; init; } = 2.0m;

    // Fenster / Verhalten
    public int LookbackBars { get; init; } = 10;
    public int RequiredConfirmations { get; init; } = 2;
    public int CooldownBars { get; init; } = 0;               // 0 = aus

    // Optionale Filter
    public bool UseVwapFilter { get; init; }
    public int MaxDistanceFromVwapTicks { get; init; } = 40;
    public bool UseSessionHighLowFilter { get; init; }
    public int SessionHighLowProximityTicks { get; init; } = 20;
    public bool UseCvdConfirmation { get; init; } = true;

    /// <summary>Liest die Parameter aus StrategyConfig.Parameters (invariant, case-insensitive Keys).</summary>
    public static OrderFlowTemplateParameters From(StrategyConfig? config)
    {
        var defaults = new OrderFlowTemplateParameters();
        if (config?.Parameters is not { Count: > 0 } p) return defaults;

        var d = new Dictionary<string, string>(p, StringComparer.OrdinalIgnoreCase);
        return new OrderFlowTemplateParameters
        {
            MinDelta = Dec(d, "MinDelta", defaults.MinDelta),
            MinVolume = Dec(d, "MinVolume", defaults.MinVolume),
            ImbalanceRatio = Dec(d, "ImbalanceRatio", defaults.ImbalanceRatio),
            AbsorptionThreshold = Dec(d, "AbsorptionThreshold", defaults.AbsorptionThreshold),
            AbsorptionMaxRangeTicks = Int(d, "AbsorptionMaxRangeTicks", defaults.AbsorptionMaxRangeTicks),
            VolumeSpikeFactor = Dec(d, "VolumeSpikeFactor", defaults.VolumeSpikeFactor),
            LookbackBars = Int(d, "LookbackBars", defaults.LookbackBars),
            RequiredConfirmations = Int(d, "RequiredConfirmations", defaults.RequiredConfirmations),
            CooldownBars = Int(d, "CooldownBars", defaults.CooldownBars),
            UseVwapFilter = Bool(d, "UseVwapFilter", defaults.UseVwapFilter),
            MaxDistanceFromVwapTicks = Int(d, "MaxDistanceFromVwapTicks", defaults.MaxDistanceFromVwapTicks),
            UseSessionHighLowFilter = Bool(d, "UseSessionHighLowFilter", defaults.UseSessionHighLowFilter),
            SessionHighLowProximityTicks = Int(d, "SessionHighLowProximityTicks", defaults.SessionHighLowProximityTicks),
            UseCvdConfirmation = Bool(d, "UseCvdConfirmation", defaults.UseCvdConfirmation)
        };
    }

    private static decimal Dec(IReadOnlyDictionary<string, string> p, string key, decimal fallback)
        => p.TryGetValue(key, out var v) && decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d : fallback;

    private static int Int(IReadOnlyDictionary<string, string> p, string key, int fallback)
        => p.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i : fallback;

    private static bool Bool(IReadOnlyDictionary<string, string> p, string key, bool fallback)
        => p.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;
}
