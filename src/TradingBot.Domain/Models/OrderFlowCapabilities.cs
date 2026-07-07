namespace TradingBot.Domain.Models;

/// <summary>
/// Welche Orderflow-Analysen dieser Datensatz EHRLICH erlaubt. Wird beim Import aus den
/// tatsächlich vorhandenen Daten abgeleitet – niemals großzügiger als die Datenbasis.
/// Fehlt eine Fähigkeit, MÜSSEN entsprechende Checks InsufficientData melden (keine Fake-Daten).
/// </summary>
public sealed record OrderFlowCapabilities
{
    /// <summary>Delta/CVD-Analysen (braucht 100% Aggressor-klassifizierte Trades bzw. Bid/Ask-Bars).</summary>
    public bool SupportsDeltaCvd { get; init; }

    /// <summary>Absorption-Analysen auf Bar-Ebene (braucht Bid/Ask-Volumen + Delta).</summary>
    public bool SupportsAbsorption { get; init; }

    /// <summary>Bar-Level-Imbalance (Ask- vs. Bid-Summe der Bar).</summary>
    public bool SupportsBarImbalance { get; init; }

    /// <summary>Stacked Imbalances (braucht Footprint: Bid/Ask je Preislevel).</summary>
    public bool SupportsStackedImbalances { get; init; }

    /// <summary>HVN/LVN-Analysen (braucht Volume-Profile: Volumen je Preislevel).</summary>
    public bool SupportsHvnLvn { get; init; }

    /// <summary>OHLCV-only: keinerlei Orderflow-Analysen erlaubt.</summary>
    public static readonly OrderFlowCapabilities None = new();
}
