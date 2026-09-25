using TradingBot.Domain.Enums;

namespace TradingBot.Backtesting.Ohlc;

/// <summary>Grund für den Ausstieg aus einem Trade (bar-basierter OHLC-Backtest).</summary>
public enum OhlcExitReason
{
    StopLoss,
    TakeProfit,
    OppositeSignal,
    EndOfData,
    SessionClose
}

/// <summary>
/// Ein abgeschlossener Round-Turn-Trade im bar-basierten OHLC-Backtest. Zusätzlich zu den
/// PnL-Feldern werden Ausstiegsgrund, Mehrdeutigkeit (SL und TP in derselben Kerze bei unbekannter
/// Reihenfolge) und die Bar-Indizes (für die Chart-Navigation) festgehalten.
/// </summary>
public sealed record OhlcBacktestTrade
{
    public required string Symbol { get; init; }
    public PositionSide Side { get; init; }
    public int Quantity { get; init; }

    public DateTimeOffset EntryTime { get; init; }
    public DateTimeOffset ExitTime { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal ExitPrice { get; init; }

    public int EntryBarIndex { get; init; }
    public int ExitBarIndex { get; init; }

    public decimal GrossPnL { get; init; }
    public decimal Fees { get; init; }
    public decimal NetPnL { get; init; }

    public OhlcExitReason ExitReason { get; init; }

    /// <summary>SL und TP lagen in derselben Kerze und die Reihenfolge ist unbekannt -&gt; konservativ
    /// als Stop-Loss gewertet und hier markiert.</summary>
    public bool Ambiguous { get; init; }

    public string? Note { get; init; }

    /// <summary>Für die Kennzahlen-Wiederverwendung (<see cref="BacktestStatisticsCalculator"/>).</summary>
    public BacktestTrade ToBacktestTrade() => new()
    {
        Symbol = Symbol,
        Side = Side,
        Quantity = Quantity,
        EntryTime = EntryTime,
        ExitTime = ExitTime,
        EntryPrice = EntryPrice,
        ExitPrice = ExitPrice,
        GrossPnL = GrossPnL,
        Fees = Fees,
        NetPnL = NetPnL
    };
}

/// <summary>Einstellungen eines OHLC-Backtests. Nichts hardcoded — Tick/Fee/PointValue aus den Profilen.</summary>
public sealed record OhlcBacktestConfig
{
    /// <summary>Feste Kontraktzahl je Trade (durch InstrumentProfile.MaxContracts begrenzt).</summary>
    public int Quantity { get; init; } = 1;

    /// <summary>Startkapital für die Equity-Kurve.</summary>
    public decimal InitialBalance { get; init; } = 10_000m;

    /// <summary>Stop-Loss in Ticks; null = InstrumentProfile.DefaultStopLossTicks.</summary>
    public int? StopLossTicks { get; init; }

    /// <summary>Take-Profit in Ticks; null = InstrumentProfile.DefaultTakeProfitTicks.</summary>
    public int? TakeProfitTicks { get; init; }

    /// <summary>Slippage in Ticks je Markt-Fill; null = FeeProfile.EstimatedSlippageTicks.</summary>
    public decimal? SlippageTicksOverride { get; init; }

    /// <summary>Gebühren berücksichtigen (Standard true). Aus = nur Brutto (für Vergleich).</summary>
    public bool ApplyFees { get; init; } = true;

    /// <summary>Unvollständige Randkerzen aus der Auswertung ausschließen (Standard true).</summary>
    public bool ExcludePartialEdges { get; init; } = true;
}

/// <summary>Ein Punkt der realisierten Equity-Kurve (kumulierter NetPnL) zu einem Bar-Zeitpunkt.</summary>
public sealed record OhlcEquityPoint
{
    public int BarIndex { get; init; }
    public DateTimeOffset Time { get; init; }
    public decimal RealizedNetPnL { get; init; }
    public decimal Equity { get; init; }   // InitialBalance + RealizedNetPnL
}

/// <summary>Herkunft und Eckdaten des verwendeten OHLC-Datensatzes (im Ergebnis festgehalten).</summary>
public sealed record OhlcDataInfo
{
    public required string Source { get; init; }
    public required string Symbol { get; init; }
    public int TimeframeMinutes { get; init; }
    public string Timezone { get; init; } = "UTC";
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public int BarCount { get; init; }
    public int EvaluatedBars { get; init; }
    public bool LeadingPartialExcluded { get; init; }
    public bool TrailingPartialExcluded { get; init; }
}

/// <summary>Ergebnis eines OHLC-Backtests: Kennzahlen, Trades, Equity-Kurve, Datenbezug, Konfig-Echo.</summary>
public sealed record OhlcBacktestResult
{
    public BacktestRunStatus Status { get; init; } = BacktestRunStatus.NotStarted;
    public string? Message { get; init; }

    public BacktestStatistics Statistics { get; init; } = BacktestStatistics.Empty;
    public IReadOnlyList<OhlcBacktestTrade> Trades { get; init; } = Array.Empty<OhlcBacktestTrade>();
    public IReadOnlyList<OhlcEquityPoint> Equity { get; init; } = Array.Empty<OhlcEquityPoint>();

    public int SignalsGenerated { get; init; }
    public int AmbiguousTrades { get; init; }

    public required OhlcDataInfo Data { get; init; }
    public string StrategyName { get; init; } = "";
    public OhlcBacktestConfig Config { get; init; } = new();

    /// <summary>Effektiv verwendete Werte (Echo für reproduzierbare Runs).</summary>
    public int EffectiveStopLossTicks { get; init; }
    public int EffectiveTakeProfitTicks { get; init; }
    public decimal EffectiveSlippageTicks { get; init; }
    public decimal FeePerSide { get; init; }
    public decimal InitialBalance { get; init; }
    public decimal FinalEquity { get; init; }
}
