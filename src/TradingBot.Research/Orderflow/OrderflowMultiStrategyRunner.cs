using System.Globalization;
using TradingBot.Application.MarketData;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Research.Runner;

namespace TradingBot.Research.Orderflow;

/// <summary>
/// Faehrt die drei vorhandenen Teststrategien je als EIGENEN Lauf ueber den bestehenden Tick-Backtester
/// (<see cref="BacktestStrategyRunner"/> + BacktestEngine) und baut daraus EIN Dashboard-Artefakt
/// (Kerzen, Footprint, Tape, je-Strategie-Trades) mit Fingerabdruck/Qualitaet/Provenienz.
/// Getrennte Ledgers (ein Lauf je Strategie), deterministisch, KEINE zweite Engine, KEINE Orders.
/// </summary>
public sealed class OrderflowMultiStrategyRunner
{
    private readonly IStrategyBacktestRunner _runner;

    public OrderflowMultiStrategyRunner(IStrategyBacktestRunner? runner = null)
        => _runner = runner ?? new BacktestStrategyRunner();

    public async Task<OrderflowRunResult> RunAsync(OrderflowRunRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.TicksPerBar <= 0) throw new ArgumentOutOfRangeException(nameof(req), "TicksPerBar muss > 0 sein.");
        var ticks = req.Ticks;
        if (ticks.Count == 0) throw new ArgumentException("Keine Ticks.", nameof(req));

        int tpb = req.TicksPerBar;
        int fullBars = ticks.Count / tpb;
        int remainder = ticks.Count % tpb;
        if (fullBars == 0)
            throw new InvalidOperationException($"Zu wenige Ticks ({ticks.Count}) fuer eine volle {tpb}-Tick-Bar.");

        // Volle Bars (Teilbar ausgeschlossen) = gemeinsame Datenbasis fuer Kerzen/Footprint/Trade-Zuordnung.
        var fullBarList = OrderFlowBarAggregator.AggregateByTicks(ticks, tpb).Take(fullBars).ToList();

        // --- je Strategie ein eigener Lauf ueber die bestehende Engine (getrennte Ledgers) ---
        var runResults = new List<StrategyRunResult>(req.Candidates.Count);
        foreach (var cand in req.Candidates)
        {
            ct.ThrowIfCancellationRequested();
            var inputs = new StrategyRunInputs
            {
                Candidate = cand,
                Config = cand.BaseConfig,
                Ticks = ticks,
                Symbol = req.Symbol,
                Instrument = req.Instrument,
                Fee = req.Fee,
                Broker = req.Broker,
                Risk = req.Risk,
                Account = req.Account,
                SlippageTicksOverride = req.SlippageTicksOverride,
                OrderFlowTicksPerBar = tpb,
                DataQualityOk = req.CapabilitiesSufficient,
                CapabilitiesSufficient = req.CapabilitiesSufficient
            };
            runResults.Add(await _runner.RunAsync(inputs, ct).ConfigureAwait(false));
        }

        // --- Kerzen + Footprint je voller Bar ---
        var candles = new List<WsCandle>(fullBars);
        var footprint = new List<IReadOnlyList<WsFpLevel>>(fullBars);
        for (int i = 0; i < fullBars; i++)
        {
            var bar = fullBarList[i];
            candles.Add(new WsCandle
            {
                T = Ms(bar.OpenTime), O = bar.Open, H = bar.High, L = bar.Low, C = bar.Close,
                V = bar.TotalVolume, Delta = bar.Delta
            });
            footprint.Add(BuildFootprint(ticks, i * tpb, tpb));
        }

        // --- Tape (alle Ticks) ---
        var tape = new List<object[]>(ticks.Count);
        foreach (var t in ticks)
            tape.Add(new object[] { Ms(t.Timestamp), t.Price, t.Volume, Dir(t.Aggressor) });

        // --- Trade -> Bar-Index (gleiche Basis wie Kerzen) ---
        var barOpenMs = fullBarList.Select(b => Ms(b.OpenTime)).ToArray();
        int BarIndexForMs(long ms)
        {
            int idx = 0;
            for (int i = 0; i < barOpenMs.Length; i++) { if (barOpenMs[i] <= ms) idx = i; else break; }
            return idx;
        }

        var startBal = req.Account.StartingBalance;
        decimal feePerSide = req.Fee.CommissionPerSide + req.Fee.ExchangeFeePerSide + req.Fee.ClearingFeePerSide
                             + req.Fee.RoutingFeePerSide + req.Fee.NfaFeePerSide + req.Fee.OtherFeePerSide;
        decimal pv = req.Instrument.PointValue;
        decimal lastTickPrice = ticks[^1].Price; // "letzter vorhandener Tick" fuer Mark-to-Market
        var strategies = new List<WsStrategy>(runResults.Count);
        foreach (var rr in runResults)
        {
            var trades = new List<WsTrade>(rr.Trades.Count);
            int k = 0;
            decimal net = 0m, fees = 0m;
            foreach (var tr in rr.Trades)
            {
                net += tr.NetPnL;
                fees += tr.Fees;
                long eMs = Ms(tr.EntryTime), xMs = Ms(tr.ExitTime);
                trades.Add(new WsTrade
                {
                    Index = k++,
                    Side = tr.Side.ToString(),
                    Qty = tr.Quantity,
                    EntryBarIndex = BarIndexForMs(eMs),
                    ExitBarIndex = BarIndexForMs(xMs),
                    EntryPrice = tr.EntryPrice,
                    ExitPrice = tr.ExitPrice,
                    ExitReason = string.Empty, // Engine liefert keine Exit-Begruendung; nichts erfinden
                    StopLossPrice = 0m,
                    TakeProfitPrice = 0m,
                    Ambiguous = false,
                    NetPnL = tr.NetPnL,
                    EntryTimeMs = eMs,
                    ExitTimeMs = xMs,
                    EntryTime = tr.EntryTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    ExitTime = tr.ExitTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                });
            }
            var diag = rr.Diagnostics;
            var oside = diag?.FinalPositionSide ?? PositionSide.Flat;
            int oqty = diag?.FinalPositionQuantity ?? 0;
            decimal oavg = diag?.FinalPositionAvgPrice ?? 0m;
            // Mark-to-Market der offenen Position zum letzten vorhandenen Tick (BRUTTO; kein kuenstlicher Exit-Fill):
            decimal unreal = oside == PositionSide.Long ? (lastTickPrice - oavg) * oqty * pv
                           : oside == PositionSide.Short ? (oavg - lastTickPrice) * oqty * pv : 0m;
            // Nur bereits gezahlte EINTRITTS-Gebuehren der offenen Position (KEINE hypothetische Exit-Fee):
            decimal openEntryFees = oqty > 0 ? feePerSide * oqty : 0m;
            decimal endRealized = startBal + net;
            decimal endMtm = endRealized + unreal - openEntryFees;
            strategies.Add(new WsStrategy
            {
                Name = rr.StrategyName,
                UsesOrderflow = rr.StrategyName.Contains("OF", StringComparison.Ordinal),
                NetPnL = net,
                Fees = fees,
                FinalEquity = endRealized,
                Trades = trades,
                RealizedNetPnL = net,
                RealizedFees = fees,
                OpenPositionEntryFees = openEntryFees,
                TotalFeesPaid = fees + openEntryFees,
                UnrealizedGrossAtLastTick = unreal,
                LastTickPrice = lastTickPrice,
                EndEquityRealized = endRealized,
                EndEquityMarkToMarket = endMtm,
                Status = diag?.Status ?? string.Empty,
                OpenPositionSide = (diag?.FinalPositionSide ?? PositionSide.Flat).ToString(),
                OpenPositionQty = diag?.FinalPositionQuantity ?? 0,
                OpenPositionAvgPrice = diag?.FinalPositionAvgPrice ?? 0m,
                OpenPositionUnrealizedGross = diag?.FinalPositionUnrealizedGross ?? 0m,
                SignalsGenerated = diag?.SignalsGenerated ?? 0,
                OrdersSubmitted = diag?.OrdersSubmitted ?? 0,
                OrdersRejectedByRisk = diag?.OrdersRejectedByRisk ?? 0,
                OrdersRejectedByBroker = diag?.OrdersRejectedByBroker ?? 0,
                OrdersUnfilledAtEnd = diag?.OrdersUnfilledAtEnd ?? 0
            });
        }

        decimal slipTicks = req.SlippageTicksOverride ?? req.Fee.EstimatedSlippageTicks;

        var meta = new OrderflowMeta
        {
            Instrument = req.Symbol,
            DataSource = req.DataSource,
            MarketStartMs = Ms(fullBarList[0].OpenTime),
            MarketEndMs = Ms(fullBarList[^1].CloseTime),
            TicksPerBar = tpb,
            PointValue = req.Instrument.PointValue,
            TickSizeNote = $"Config-TickSize {req.Instrument.TickSize}; Aufnahme 1-Punkt-granular (ungeklaert, nicht korrigiert)",
            FeePerSide = feePerSide,
            SlippageTicks = slipTicks,
            GeneratedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        var availability = new Dictionary<string, string>
        {
            ["candles"] = "verfuegbar (volle Bars)",
            ["footprint"] = "abgeleitet (aus Ticks je Bar)",
            ["tape"] = "verfuegbar (aufgenommene Trades)",
            ["delta"] = "abgeleitet (Aggressor aus Aufnahme)",
            ["cvd"] = "abgeleitet",
            ["dom"] = "nicht verfuegbar (unvalidiert)",
            ["orderbookHeatmap"] = "nicht verfuegbar (unvalidiert)"
        };

        var quality = new OrderflowQuality
        {
            PriceGranularity = "1-Punkt (Aufnahme; MNQ real 0,25 ungeklaert)",
            Drops = Math.Max(0, req.RowsRead - req.RowsAccepted),
            Aggressor = string.IsNullOrEmpty(req.AggressorNote) ? "aus Aufnahme (AggressorSide)" : req.AggressorNote,
            FootprintSource = "aus Ticks je 500-Tick-Bar (kein nativer ATAS-Footprint)",
            DomNote = "DOM/Orderbuch-Heatmap unvalidiert -> nicht verfuegbar"
        };

        var provenance = new OrderflowProvenance
        {
            DataFilePath = req.DataFilePath,
            DataSha256 = req.DataSha256,
            Ticks = ticks.Count,
            FullBars = fullBars,
            RemainderTicks = remainder,
            TicksPerBar = tpb,
            DeltaThreshold = req.DeltaThreshold,
            StartingBalance = startBal,
            Costs = new OrderflowCosts
            {
                FeePerSideTotal = feePerSide,
                CommissionPerSide = req.Fee.CommissionPerSide,
                ExchangeFeePerSide = req.Fee.ExchangeFeePerSide,
                ClearingFeePerSide = req.Fee.ClearingFeePerSide,
                RoutingFeePerSide = req.Fee.RoutingFeePerSide,
                NfaFeePerSide = req.Fee.NfaFeePerSide,
                OtherFeePerSide = req.Fee.OtherFeePerSide,
                SlippageTicks = slipTicks,
                TickSize = req.Instrument.TickSize,
                PointValue = req.Instrument.PointValue
            },
            Capabilities = req.CapabilitiesSufficient ? "SupportsDeltaCvd=true (voll klassifiziert)" : "unzureichend",
            ExecutionModel = "Bestehende BacktestEngine (tick-getrieben): Signal auf geschlossener "
                + $"{tpb}-Tick-Bar i; Market-Order fuellt am naechsten Tick (= Open Bar i+1), Fills VOR der Strategie "
                + "(kein Lookahead). Slippage/Fees aus FillModel/FeeProfile; Risk-Gatekeeper aktiv.",
            DivergencesFromHarness = new[]
            {
                "Kosten: Lauf nutzt das vollstaendige FeeProfile (Summe aller Gebuehren-Komponenten je Seite) statt des pauschalen 0,37/Seite im Harness.",
                "EndOfData: Die BacktestEngine stellt eine am Datenende offene Position NICHT zwangsweise glatt (Harness flachte per EndOfData am Close der letzten vollen Bar) -> eine offene Restposition erscheint nicht als abgeschlossener Trade.",
                "Das letzte volle Bar-Signal wird (anders als im Harness) ausgefuehrt und fuellt am ersten Folge-Tick; die Rest-Ticks erzeugen selbst KEINE Signal-Bar.",
                "Fill-/Slippage-/Flip-Details stammen aus FillModel/OrderManager, nicht aus der vereinfachten Harness-Transition -> PnL bewusst NICHT identisch erzwungen."
            },
            Caveats = new[]
            {
                "Technischer Funktionsnachweis - KEINE Profitabilitaetsaussage.",
                "Preisgranularitaet 1-Punkt (MNQ real 0,25) ungeklaert und NICHT korrigiert.",
                "DOM/Orderbuch-Heatmap unvalidiert - als nicht verfuegbar gekennzeichnet.",
                "Kleiner Einzelabschnitt, kein Out-of-Sample/Holdout.",
                "SinglePositionGate (max. 1 Kontrakt, kein Add) entscheidet gegen den TATSAECHLICHEN Positionszustand der Engine (read-only IPositionManager), nicht gegen ein Signalmodell: abgelehnte/ungefuellte Orders erzeugen keine Phantomposition. Trades/Position/PnL stammen ohnehin aus der Engine; harte Obergrenze via RiskConfig MaxContracts=1 (Backstop)."
            },
            SettingsUsed = req.SettingsUsed
        };

        var artifact = new OrderflowWorkspaceArtifact
        {
            Meta = meta,
            Availability = availability,
            Quality = quality,
            Candles = candles,
            Footprint = footprint,
            Tape = tape,
            Strategies = strategies,
            Provenance = provenance
        };

        var shaPart = string.IsNullOrEmpty(req.DataSha256) ? "nosha" : req.DataSha256[..Math.Min(8, req.DataSha256.Length)];
        var runId = $"orderflow-{DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{shaPart}";

        return new OrderflowRunResult
        {
            RunId = runId,
            Workspace = artifact,
            RunResults = runResults,
            FullBars = fullBars,
            RemainderTicks = remainder
        };
    }

    /// <summary>Footprint je Bar aus den Rohticks (Bid = aggressive Verkaeufer, Ask = aggressive Kaeufer).</summary>
    private static IReadOnlyList<WsFpLevel> BuildFootprint(IReadOnlyList<MarketTick> ticks, int start, int count)
    {
        var map = new SortedDictionary<decimal, (decimal bid, decimal ask)>();
        int end = Math.Min(ticks.Count, start + count);
        for (int i = start; i < end; i++)
        {
            var t = ticks[i];
            map.TryGetValue(t.Price, out var cur);
            if (t.Aggressor == AggressorSide.Buy) cur.ask += t.Volume;
            else if (t.Aggressor == AggressorSide.Sell) cur.bid += t.Volume;
            map[t.Price] = cur;
        }
        var levels = new List<WsFpLevel>(map.Count);
        foreach (var kv in map) levels.Add(new WsFpLevel { P = kv.Key, B = kv.Value.bid, A = kv.Value.ask });
        return levels;
    }

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();
    private static int Dir(AggressorSide a) => a == AggressorSide.Buy ? 1 : a == AggressorSide.Sell ? -1 : 0;
}
