namespace TradingBot.PaperTrading;

/// <summary>
/// Einstellungen einer Paper-Session. Nichts hardcoded – Slippage/Fees kommen aus den Profilen,
/// sofern hier nicht explizit überschrieben.
/// </summary>
public sealed record PaperTradingConfiguration
{
    /// <summary>Überschreibt die Slippage in Ticks; null = nutze FeeProfile.EstimatedSlippageTicks.</summary>
    public decimal? SlippageTicksOverride { get; init; }

    /// <summary>
    /// Timeout, nach dem der Feed als überaltert gilt. Hinweis: Mit Replay-Daten und
    /// Tick-gesteuerter Uhr greift Wanduhr-Staleness praktisch nicht – relevant erst mit
    /// einem echten (späteren) Live-Feed.
    /// </summary>
    public TimeSpan FeedStaleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Teilfüllungen (vorbereitet, Standard aus = volle Fills).</summary>
    public bool AllowPartialFills { get; init; }
}
