namespace TradingBot.Domain.Models;

/// <summary>
/// Aufgeschlüsselte Kosten eines Trades. Jede Gebührenart wird getrennt gehalten,
/// damit Reporting und NetPnL exakt nachvollziehbar sind.
/// <para>
/// SEMANTIK: Die zugrunde liegenden FeeProfile-Werte sind jeweils PRO KONTRAKT PRO SEITE.
/// Ein Round Turn besteht aus Entry-Seite + Exit-Seite. Slippage wird ebenfalls pro
/// Kontrakt pro Seite geschätzt. Beträge in dieser Aufstellung sind bereits mit
/// Kontraktanzahl und Seitenanzahl multipliziert.
/// </para>
/// Alle Werte sind <see cref="decimal"/>, um Rundungsfehler zu vermeiden.
/// </summary>
public sealed record FeeBreakdown
{
    public decimal Commission { get; init; }
    public decimal ExchangeFee { get; init; }
    public decimal ClearingFee { get; init; }
    public decimal RoutingFee { get; init; }
    public decimal NfaFee { get; init; }
    public decimal OtherFee { get; init; }
    public decimal SlippageCost { get; init; }

    /// <summary>
    /// Summe ALLER Gebühren: Commission + ExchangeFee + ClearingFee + RoutingFee
    /// + NfaFee + OtherFee + SlippageCost. Dieser Wert wird von NetPnL abgezogen.
    /// </summary>
    public decimal TotalFees =>
        Commission + ExchangeFee + ClearingFee + RoutingFee + NfaFee + OtherFee + SlippageCost;

    /// <summary>Gebühren ohne Slippage (reine Broker-/Exchange-Kosten).</summary>
    public decimal TotalCommissionsAndFees =>
        Commission + ExchangeFee + ClearingFee + RoutingFee + NfaFee + OtherFee;

    public static readonly FeeBreakdown Zero = new();

    /// <summary>Summiert zwei Aufstellungen komponentenweise (z. B. Akkumulation über Fills).</summary>
    public static FeeBreakdown operator +(FeeBreakdown a, FeeBreakdown b) => new()
    {
        Commission = a.Commission + b.Commission,
        ExchangeFee = a.ExchangeFee + b.ExchangeFee,
        ClearingFee = a.ClearingFee + b.ClearingFee,
        RoutingFee = a.RoutingFee + b.RoutingFee,
        NfaFee = a.NfaFee + b.NfaFee,
        OtherFee = a.OtherFee + b.OtherFee,
        SlippageCost = a.SlippageCost + b.SlippageCost
    };
}
