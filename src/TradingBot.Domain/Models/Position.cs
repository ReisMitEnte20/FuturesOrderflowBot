using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>
/// Aktuelle Position in einem Symbol. GrossPnL und NetPnL werden getrennt geführt.
/// Unrealized/Realized werden vom PositionManager gepflegt (keine Logik im Modell).
/// </summary>
public sealed record Position
{
    public required string AccountId { get; init; }
    public required string Symbol { get; init; }

    public PositionSide Side { get; init; } = PositionSide.Flat;

    /// <summary>Anzahl Kontrakte (immer &gt;= 0; Richtung steckt in <see cref="Side"/>).</summary>
    public int Quantity { get; init; }

    public decimal AverageEntryPrice { get; init; }

    public decimal UnrealizedGrossPnL { get; init; }
    public decimal RealizedGrossPnL { get; init; }
    public decimal RealizedNetPnL { get; init; }

    /// <summary>Bisher angefallene Gebühren dieser Position.</summary>
    public FeeBreakdown Fees { get; init; } = FeeBreakdown.Zero;

    public bool IsFlat => Side == PositionSide.Flat || Quantity == 0;
}
