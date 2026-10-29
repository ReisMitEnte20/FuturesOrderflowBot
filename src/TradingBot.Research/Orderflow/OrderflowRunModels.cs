using TradingBot.Domain.Models;

namespace TradingBot.Research.Orderflow;

/// <summary>
/// Eingaben fuer EINEN Orderflow-Mehrstrategielauf ueber den bestehenden Tick-Backtester
/// (<see cref="Runner.BacktestStrategyRunner"/> + BacktestEngine). KEINE zweite Engine, keine Orders.
/// Alle Markt-/Kostenparameter kommen von aussen (aus config/), nichts hardcoded.
/// </summary>
public sealed record OrderflowRunRequest
{
    public required IReadOnlyList<MarketTick> Ticks { get; init; }
    public required string Symbol { get; init; }
    public required InstrumentProfile Instrument { get; init; }
    public required FeeProfile Fee { get; init; }
    public required BrokerProfile Broker { get; init; }
    public required RiskConfig Risk { get; init; }
    public required TradingAccount Account { get; init; }

    /// <summary>Ticks pro OrderFlowBar (Mehrstrategielauf: 500).</summary>
    public int TicksPerBar { get; init; } = 500;

    /// <summary>Slippage-Override in Ticks (null = FeeProfile.EstimatedSlippageTicks).</summary>
    public decimal? SlippageTicksOverride { get; init; }

    public required IReadOnlyList<StrategyCandidate> Candidates { get; init; }

    // Provenienz/Qualitaet (werden mit dem Ergebnis gespeichert):
    public string DataSource { get; init; } = string.Empty;
    public string DataFilePath { get; init; } = string.Empty;
    public string DataSha256 { get; init; } = string.Empty;
    public bool CapabilitiesSufficient { get; init; } = true;
    public string AggressorNote { get; init; } = string.Empty;
    public int RowsRead { get; init; }
    public int RowsAccepted { get; init; }
    public decimal DeltaThreshold { get; init; }

    /// <summary>
    /// Maximale Anzahl Tape-Eintraege im Dashboard-Artefakt (0 = unbegrenzt, Rueckwaertskompatibel).
    /// Schuetzt den Browser davor, zig Millionen Tick-Tape-Eintraege zu laden; Kerzen/Footprint/Delta nutzen
    /// weiterhin ALLE Ticks.
    /// </summary>
    public int MaxTapeEntries { get; init; }

    /// <summary>Ehrlicher, quellenspezifischer Hinweis zur Preisgranularitaet (leer = Runner-Default).</summary>
    public string PriceGranularityNote { get; init; } = string.Empty;

    /// <summary>Ehrlicher, quellenspezifischer Hinweis zur Footprint-Quelle (leer = Runner-Default).</summary>
    public string FootprintSourceNote { get; init; } = string.Empty;

    /// <summary>Tatsaechlich verwendete Einstellungen/Config-Quellen (Freitext key/value), werden mit gespeichert.</summary>
    public IReadOnlyDictionary<string, string> SettingsUsed { get; init; } = new Dictionary<string, string>();
}

/// <summary>Ergebnis eines Orderflow-Mehrstrategielaufs: das Dashboard-Artefakt + die Roh-Laufergebnisse (fuer Tests).</summary>
public sealed record OrderflowRunResult
{
    public required string RunId { get; init; }
    public required OrderflowWorkspaceArtifact Workspace { get; init; }
    public required IReadOnlyList<StrategyRunResult> RunResults { get; init; }
    public int FullBars { get; init; }
    public int RemainderTicks { get; init; }
}

// ---- Dashboard-Artefakt (serialisiert camelCase -> entspricht dashboard/src/lib/workspace.ts WorkspaceData) ----

public sealed record OrderflowWorkspaceArtifact
{
    public required OrderflowMeta Meta { get; init; }
    public required IReadOnlyDictionary<string, string> Availability { get; init; }
    public required OrderflowQuality Quality { get; init; }
    public required IReadOnlyList<WsCandle> Candles { get; init; }
    public required IReadOnlyList<IReadOnlyList<WsFpLevel>> Footprint { get; init; }
    public required IReadOnlyList<object[]> Tape { get; init; }
    public required IReadOnlyList<WsStrategy> Strategies { get; init; }
    public required OrderflowProvenance Provenance { get; init; }
}

public sealed record OrderflowMeta
{
    public required string Instrument { get; init; }
    public required string DataSource { get; init; }
    public long MarketStartMs { get; init; }
    public long MarketEndMs { get; init; }
    public int TicksPerBar { get; init; }
    public decimal PointValue { get; init; }
    public required string TickSizeNote { get; init; }
    public decimal FeePerSide { get; init; }
    public decimal SlippageTicks { get; init; }
    public required string GeneratedUtc { get; init; }
}

public sealed record OrderflowQuality
{
    public required string PriceGranularity { get; init; }
    public int Drops { get; init; }
    public required string Aggressor { get; init; }
    public required string FootprintSource { get; init; }
    public required string DomNote { get; init; }
}

public sealed record WsCandle
{
    public long T { get; init; }
    public decimal O { get; init; }
    public decimal H { get; init; }
    public decimal L { get; init; }
    public decimal C { get; init; }
    public decimal V { get; init; }
    public decimal Delta { get; init; }
    /// <summary>Belegte Bar-SCHLUSSZEIT (ms) aus dem Aggregator (fuer leakage-freien Snapshot @t der letzten Bar).</summary>
    public long Tc { get; init; }
}

public sealed record WsFpLevel
{
    public decimal P { get; init; }
    public decimal B { get; init; }
    public decimal A { get; init; }
}

public sealed record WsStrategy
{
    public required string Name { get; init; }
    public bool UsesOrderflow { get; init; }
    public decimal NetPnL { get; init; }        // realisierte Netto-PnL (geschlossene Trades) - == RealizedNetPnL
    public decimal Fees { get; init; }           // Gebuehren der geschlossenen Trades (Round-Turn) - == RealizedFees
    public decimal FinalEquity { get; init; }    // == EndEquityRealized (Startkapital + realisierte Netto-PnL)
    public required IReadOnlyList<WsTrade> Trades { get; init; }

    // ---- Vollstaendiges Endergebnis: realisiert / unrealisiert / Gebuehren / End-Equity getrennt ----
    public decimal RealizedNetPnL { get; init; }          // geschlossene Trades, nach Fees
    public decimal RealizedFees { get; init; }            // Round-Turn-Fees der geschlossenen Trades
    public decimal OpenPositionEntryFees { get; init; }   // bereits gezahlte EINTRITTS-Fees der offenen Position (KEINE Exit-Fee)
    public decimal TotalFeesPaid { get; init; }           // RealizedFees + OpenPositionEntryFees
    public decimal UnrealizedGrossAtLastTick { get; init; } // Mark-to-Market der offenen Position zum LETZTEN vorhandenen Tick (brutto)
    public decimal LastTickPrice { get; init; }
    public decimal EndEquityRealized { get; init; }       // Startkapital + RealizedNetPnL
    public decimal EndEquityMarkToMarket { get; init; }   // EndEquityRealized + UnrealizedGross - OpenPositionEntryFees (kein kuenstlicher Exit)

    // Sichtbarkeit des Laufendes: offene Restposition + ausstehende Orders bleiben im Ergebnis.
    public string Status { get; init; } = string.Empty;
    public string OpenPositionSide { get; init; } = "Flat";
    public int OpenPositionQty { get; init; }
    public decimal OpenPositionAvgPrice { get; init; }
    public decimal OpenPositionUnrealizedGross { get; init; }
    public int SignalsGenerated { get; init; }
    public int OrdersSubmitted { get; init; }
    public int OrdersRejectedByRisk { get; init; }
    public int OrdersRejectedByBroker { get; init; }
    public int OrdersUnfilledAtEnd { get; init; }
}

public sealed record WsTrade
{
    public int Index { get; init; }
    public required string Side { get; init; }
    public int Qty { get; init; }
    public int EntryBarIndex { get; init; }
    public int ExitBarIndex { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal ExitPrice { get; init; }
    public required string ExitReason { get; init; }
    public decimal StopLossPrice { get; init; }
    public decimal TakeProfitPrice { get; init; }
    public bool Ambiguous { get; init; }
    public decimal NetPnL { get; init; }
    public long EntryTimeMs { get; init; }
    public long ExitTimeMs { get; init; }
    public required string EntryTime { get; init; }
    public required string ExitTime { get; init; }
}

/// <summary>Maschinenlesbare Herkunft/Grenzen des Laufs (Fingerabdruck, Kosten, Abweichungen zum Harness).</summary>
public sealed record OrderflowProvenance
{
    public required string DataFilePath { get; init; }
    public required string DataSha256 { get; init; }
    public int Ticks { get; init; }
    public int FullBars { get; init; }
    public int RemainderTicks { get; init; }
    public int TicksPerBar { get; init; }
    public decimal DeltaThreshold { get; init; }
    public decimal StartingBalance { get; init; }
    public required OrderflowCosts Costs { get; init; }
    public required string Capabilities { get; init; }
    public required string ExecutionModel { get; init; }
    public required IReadOnlyList<string> DivergencesFromHarness { get; init; }
    public required IReadOnlyList<string> Caveats { get; init; }

    /// <summary>Tatsaechlich verwendete Einstellungen/Config-Quellen (nur dieser lokale Lauf).</summary>
    public IReadOnlyDictionary<string, string> SettingsUsed { get; init; } = new Dictionary<string, string>();
}

public sealed record OrderflowCosts
{
    public decimal FeePerSideTotal { get; init; }
    public decimal CommissionPerSide { get; init; }
    public decimal ExchangeFeePerSide { get; init; }
    public decimal ClearingFeePerSide { get; init; }
    public decimal RoutingFeePerSide { get; init; }
    public decimal NfaFeePerSide { get; init; }
    public decimal OtherFeePerSide { get; init; }
    public decimal SlippageTicks { get; init; }
    public decimal TickSize { get; init; }
    public decimal PointValue { get; init; }
}
