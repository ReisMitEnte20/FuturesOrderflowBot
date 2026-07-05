namespace TradingBot.Application.Strategies.OrderFlow;

/// <summary>Ergebnisstatus eines einzelnen Orderflow-Checks.</summary>
public enum ConditionStatus
{
    /// <summary>Bedingung erfüllt.</summary>
    Met = 0,
    /// <summary>Bedingung geprüft, aber nicht erfüllt.</summary>
    NotMet = 1,
    /// <summary>Datenbasis reicht nicht aus – es wird NIEMALS geraten (fail-closed).</summary>
    InsufficientData = 2
}

/// <summary>Ergebnis eines einzelnen Checks inkl. Name und Diagnose-Detail.</summary>
public sealed record ConditionResult(string Condition, ConditionStatus Status, string? Detail = null)
{
    public bool IsMet => Status == ConditionStatus.Met;

    public static ConditionResult Met(string condition, string? detail = null)
        => new(condition, ConditionStatus.Met, detail);
    public static ConditionResult NotMet(string condition, string? detail = null)
        => new(condition, ConditionStatus.NotMet, detail);
    public static ConditionResult Insufficient(string condition, string detail)
        => new(condition, ConditionStatus.InsufficientData, detail);
}
