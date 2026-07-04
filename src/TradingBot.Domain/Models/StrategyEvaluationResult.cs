namespace TradingBot.Domain.Models;

/// <summary>
/// Ergebnis EINER Strategie-Auswertung für EIN Event. Entweder ein Signal oder
/// bewusst kein Signal (mit optionalem Grund für Diagnose). Niemals eine Order.
/// </summary>
public sealed record StrategyEvaluationResult
{
    public required string StrategyName { get; init; }
    public TradeSignal? Signal { get; init; }
    public string? Reason { get; init; }

    public bool HasSignal => Signal is not null;

    public static StrategyEvaluationResult NoSignal(string strategyName, string? reason = null)
        => new() { StrategyName = strategyName, Reason = reason };

    public static StrategyEvaluationResult WithSignal(string strategyName, TradeSignal signal, string? reason = null)
        => new() { StrategyName = strategyName, Signal = signal, Reason = reason };
}
