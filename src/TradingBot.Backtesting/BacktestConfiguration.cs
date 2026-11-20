namespace TradingBot.Backtesting;

/// <summary>
/// Woher die Fill-Preise stammen. Nur <see cref="Tick"/> liefert genaue Fills.
/// <see cref="Candle"/> ist bewusst als UNGENAU markiert und wird derzeit nicht simuliert.
/// </summary>
public enum FillDataMode
{
    Tick = 0,
    /// <summary>Ungenauer Modus – ohne Tickdaten sind Fills innerhalb einer Candle nicht seriös bestimmbar.</summary>
    Candle = 1
}

/// <summary>Einstellungen eines Backtest-Laufs. Nichts hardcoded – Werte kommen von hier oder aus den Profilen.</summary>
public sealed record BacktestConfiguration
{
    public FillDataMode FillDataMode { get; init; } = FillDataMode.Tick;

    /// <summary>Überschreibt die Slippage in Ticks; null = nutze FeeProfile.EstimatedSlippageTicks.</summary>
    public decimal? SlippageTicksOverride { get; init; }

    /// <summary>
    /// Annahme-Szenario Ausführungsverzögerung: eine Order darf frühestens am (1 + n)-ten Trade-Tick NACH ihrer Einreichung
    /// gefüllt werden (Standard 0 = am Folge-Tick, unverändertes Verhalten). Fehlt dieser Tick, bleibt die Order ungefüllt —
    /// es wird kein Fill erfunden. Kein gemessener Latenzwert.
    /// </summary>
    public int FillDelayTicks { get; init; }

    /// <summary>Startkapital für die Equity-Kurve (optional, nur informativ).</summary>
    public decimal InitialBalance { get; init; }

    /// <summary>Teilfüllungen (derzeit vorbereitet, Standard aus = volle Fills).</summary>
    public bool AllowPartialFills { get; init; }
}
