using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>
/// Ergebnis der RiskManager-Prüfung. Der RiskManager ENTSCHEIDET nur – er führt nie aus.
/// Bei Ablehnung ist <see cref="ApprovedContracts"/> stets 0 und <see cref="RejectionReason"/>
/// ungleich <see cref="RiskRejectionReason.None"/>.
/// </summary>
public sealed record RiskDecision
{
    public bool Approved { get; init; }
    public RiskRejectionReason RejectionReason { get; init; } = RiskRejectionReason.None;
    public string Message { get; init; } = string.Empty;

    /// <summary>Vom Aufrufer angefragte Kontraktanzahl.</summary>
    public int RequestedContracts { get; init; }

    /// <summary>Tatsächlich freigegebene Kontraktanzahl (≤ Requested, 0 bei Ablehnung).</summary>
    public int ApprovedContracts { get; init; }

    /// <summary>Verbleibender erlaubter Tagesverlust (≥ 0).</summary>
    public decimal RemainingDailyLoss { get; init; }

    /// <summary>Verbleibende erlaubte Trades heute (int.MaxValue = unbegrenzt).</summary>
    public int RemainingTrades { get; init; }

    /// <summary>Aktueller NetPnL des Tages.</summary>
    public decimal CurrentDailyPnL { get; init; }

    public int CurrentConsecutiveLosses { get; init; }

    /// <summary>Auslastung des Risikobudgets in Prozent (0..100), bindende Grenze.</summary>
    public decimal RiskUtilizationPercent { get; init; }
}
