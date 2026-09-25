using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Backtesting.Ohlc;

/// <summary>
/// Deterministischer, BAR-basierter OHLC-Backtest (Simulation-only, keine Broker-/Live-Anbindung).
/// Wiederverwendet Domainmodelle (<see cref="Candle"/>, <see cref="IStrategy"/>, <see cref="InstrumentProfile"/>,
/// <see cref="FeeProfile"/>) und die Kennzahlenberechnung (<see cref="BacktestStatisticsCalculator"/>).
///
/// Ausführungsregeln (kein Look-ahead):
/// - Die Strategie sieht je Bar nur Daten bis zu diesem Bar (<see cref="IStrategy.OnCandle"/> am Bar-Schluss).
/// - Ein Signal aus dem Schlusskurs von Bar i wird frühestens am OPEN von Bar i+1 ausgeführt.
/// - Bereits aktive SL/TP werden je Folge-Bar geprüft. Die Gap-Behandlung ist NACH Ordertyp differenziert
///   (siehe <see cref="CheckStopTarget"/>): ein Stop wird bei einer Lücke jenseits des Levels zur Market-Order
///   und zum (bereits ungünstigeren) OPEN gefüllt; ein Take-Profit ist eine Limit-Order und wird bei einer
///   günstigen Lücke zum OPEN als Preisverbesserung gefüllt (nie schlechter als die Limit-Grenze), sonst exakt
///   an der Limit-Grenze. Kein unrealistischer Fill zum alten Level.
/// - Reihenfolge am nächsten OPEN: Eine am Vorabend (Bar-Schluss) erzeugte Gegensignal-/Exit-Anweisung wird
///   ZUERST am OPEN ausgeführt (Schritt A); erst danach werden die Schutzorders (SL/TP) der dann offenen
///   Position gegen die Bar-Range geprüft (Schritt B). Ein Gegensignal schließt also am OPEN und dreht die
///   Position; ein an derselben Lücke greifender Stop hätte dieselbe Position ebenfalls zum OPEN geschlossen –
///   das Ergebnis ist deterministisch und preislich identisch (OPEN ± Slippage).
/// - Werden SL und TP in derselben Kerze berührt und die Reihenfolge ist unbekannt, wird konservativ
///   der Stop-Loss angenommen und der Trade als mehrdeutig markiert.
/// - Auch bei Entry und Exit in derselben Kerze wird keine unbekannte Kursreihenfolge als Tatsache
///   angenommen (gleiche konservative Regel).
/// - Slippage ist ausschließlich in den Fill-Preisen enthalten (Entry/Exit von Market-Orders); sie wird
///   NICHT zusätzlich als Kostenposition vom NetPnL abgezogen. Der aufsummierte Slippage-Betrag ist rein
///   informativ (<see cref="BacktestStatistics.TotalSlippage"/>).
/// KEINE künstliche Tick-Reihenfolge aus OHLC. Ergebnis ist unabhängig von jeder Darstellungs-/Replay-Geschwindigkeit.
/// </summary>
public sealed class OhlcBacktestEngine
{
    public OhlcBacktestResult Run(
        IReadOnlyList<Candle> candles,
        IStrategy strategy,
        InstrumentProfile instrument,
        FeeProfile fee,
        OhlcBacktestConfig config,
        string source,
        int timeframeMinutes,
        bool leadingPartial = false,
        bool trailingPartial = false)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(fee);
        config ??= new OhlcBacktestConfig();

        string symbol = instrument.Symbol;
        int slTicks = config.StopLossTicks ?? instrument.DefaultStopLossTicks;
        int tpTicks = config.TakeProfitTicks ?? instrument.DefaultTakeProfitTicks;
        decimal slipTicks = config.SlippageTicksOverride ?? fee.EstimatedSlippageTicks;
        decimal tickSize = instrument.TickSize;
        decimal pointValue = instrument.PointValue;
        decimal tickValue = instrument.TickValue;
        int qty = config.Quantity <= 0 ? 1 : config.Quantity;
        if (instrument.MaxContracts > 0) qty = Math.Min(qty, instrument.MaxContracts);
        decimal feePerSide = PerSideFee(fee);
        decimal slip = slipTicks * tickSize;

        // Auswertungsfenster: unvollständige Randkerzen optional ausschließen.
        int start = 0, end = candles.Count - 1;
        bool leadExcl = false, trailExcl = false;
        if (config.ExcludePartialEdges)
        {
            if (leadingPartial && end - start >= 1) { start++; leadExcl = true; }
            if (trailingPartial && end - start >= 1) { end--; trailExcl = true; }
        }

        var dataInfo = new OhlcDataInfo
        {
            Source = source,
            Symbol = symbol,
            TimeframeMinutes = timeframeMinutes,
            Timezone = "UTC",
            From = start <= end ? candles[start].OpenTime : (DateTimeOffset?)null,
            To = start <= end ? candles[end].CloseTime : (DateTimeOffset?)null,
            BarCount = candles.Count,
            EvaluatedBars = Math.Max(0, end - start + 1),
            LeadingPartialExcluded = leadExcl,
            TrailingPartialExcluded = trailExcl
        };

        if (slTicks <= 0 || tpTicks <= 0)
        {
            return new OhlcBacktestResult
            {
                Status = BacktestRunStatus.Failed,
                Message = "StopLossTicks und TakeProfitTicks müssen > 0 sein (aus Config oder InstrumentProfile).",
                Data = dataInfo,
                StrategyName = strategy.Name,
                Config = config
            };
        }
        if (dataInfo.EvaluatedBars < 2)
        {
            return new OhlcBacktestResult
            {
                Status = BacktestRunStatus.Completed,
                Message = "Zu wenige auswertbare Kerzen (mind. 2 nach Ausschluss der Teilkerzen).",
                Data = dataInfo,
                StrategyName = strategy.Name,
                Config = config,
                EffectiveStopLossTicks = slTicks,
                EffectiveTakeProfitTicks = tpTicks,
                EffectiveSlippageTicks = slipTicks,
                FeePerSide = feePerSide,
                InitialBalance = config.InitialBalance,
                FinalEquity = config.InitialBalance
            };
        }

        strategy.Reset();

        var trades = new List<OhlcBacktestTrade>();
        var equity = new List<OhlcEquityPoint>();
        decimal realized = 0m;
        decimal totalSlippageDollars = 0m;
        int signals = 0;

        // offene Position
        PositionSide side = PositionSide.Flat;
        decimal entryPrice = 0m, sl = 0m, tp = 0m;
        DateTimeOffset entryTime = default;
        int entryBar = -1;

        SignalDirection? pending = null;   // Signal aus dem Schluss des Vorgänger-Bars

        for (int i = start; i <= end; i++)
        {
            var bar = candles[i];

            // (A) Pending-Signal am OPEN dieses Bars ausführen (kein Look-ahead). Diese am Vorabend
            //     erzeugte Anweisung hat Vorrang vor den intrabar geprüften Schutzorders (Schritt B):
            //     ein Gegensignal schließt die bestehende Position zum OPEN und dreht sie.
            if (pending is SignalDirection dir)
            {
                var wantSide = dir == SignalDirection.Long ? PositionSide.Long : PositionSide.Short;
                if (side == PositionSide.Flat)
                {
                    OpenPosition(bar, wantSide, i);
                }
                else if (side != wantSide)
                {
                    // Gegensignal: bestehende Position zum Open schließen (Market), dann flippen.
                    CloseAtMarket(bar.Open, i, OhlcExitReason.OppositeSignal, bar.OpenTime);
                    OpenPosition(bar, wantSide, i);
                }
                // gleiche Richtung -> ignorieren (bereits investiert)
                pending = null;
            }

            // (B) SL/TP der (nach A) offenen Position gegen die Bar-Range prüfen (Gap-Regel je Ordertyp).
            if (side != PositionSide.Flat)
            {
                var ex = CheckStopTarget(bar, side, sl, tp, tickSize, slip);
                if (ex.Hit)
                {
                    CloseExit(ex.Price, i, ex.Reason, ex.Time(bar), ex.Ambiguous, ex.Market);
                }
            }

            // (C) Strategie sieht den vollständigen Bar; Signal wirkt erst am nächsten Bar-Open.
            var candle = ToStrategyCandle(bar);
            var sig = strategy.OnCandle(candle);
            if (sig is not null && sig.Direction is SignalDirection.Long or SignalDirection.Short)
            {
                signals++;
                pending = sig.Direction;
            }

            // Mark-to-Market der zum Bar-Schluss offenen Position (nur Anzeige, nicht in Equity).
            decimal openPnl = side == PositionSide.Flat
                ? 0m
                : (bar.Close - entryPrice) * (side == PositionSide.Long ? 1m : -1m) * pointValue * qty;

            equity.Add(new OhlcEquityPoint
            {
                BarIndex = i,
                Time = bar.CloseTime,
                RealizedNetPnL = realized,
                Equity = config.InitialBalance + realized,
                OpenPnL = openPnl
            });
        }

        // Offene Position am Ende zum letzten Schlusskurs schließen (Market).
        if (side != PositionSide.Flat)
        {
            var last = candles[end];
            decimal exitPx = side == PositionSide.Long ? last.Close - slip : last.Close + slip;
            CloseExit(exitPx, end, OhlcExitReason.EndOfData, last.CloseTime, ambiguous: false, market: true);
            if (equity.Count > 0)
                equity[^1] = equity[^1] with { RealizedNetPnL = realized, Equity = config.InitialBalance + realized, OpenPnL = 0m };
        }

        var stats = BacktestStatisticsCalculator.Compute(
            trades.Select(t => t.ToBacktestTrade()).ToList(), totalSlippageDollars);

        return new OhlcBacktestResult
        {
            Status = BacktestRunStatus.Completed,
            Statistics = stats,
            Trades = trades,
            Equity = equity,
            SignalsGenerated = signals,
            AmbiguousTrades = trades.Count(t => t.Ambiguous),
            Data = dataInfo,
            StrategyName = strategy.Name,
            Config = config,
            EffectiveStopLossTicks = slTicks,
            EffectiveTakeProfitTicks = tpTicks,
            EffectiveSlippageTicks = slipTicks,
            FeePerSide = feePerSide,
            InitialBalance = config.InitialBalance,
            FinalEquity = config.InitialBalance + realized
        };

        // ---- lokale Helfer ----
        void OpenPosition(Candle bar, PositionSide s, int barIdx)
        {
            side = s;
            entryTime = bar.OpenTime;
            entryBar = barIdx;
            // Market-Entry am Open mit adverser Slippage.
            entryPrice = s == PositionSide.Long ? bar.Open + slip : bar.Open - slip;
            totalSlippageDollars += slipTicks * tickValue * qty;
            decimal slDist = slTicks * tickSize, tpDist = tpTicks * tickSize;
            if (s == PositionSide.Long) { sl = entryPrice - slDist; tp = entryPrice + tpDist; }
            else { sl = entryPrice + slDist; tp = entryPrice - tpDist; }
        }

        void CloseAtMarket(decimal open, int barIdx, OhlcExitReason reason, DateTimeOffset time)
        {
            decimal exitPx = side == PositionSide.Long ? open - slip : open + slip;
            CloseExit(exitPx, barIdx, reason, time, ambiguous: false, market: true);
        }

        void CloseExit(decimal exitPx, int barIdx, OhlcExitReason reason, DateTimeOffset time, bool ambiguous, bool market)
        {
            decimal dirSign = side == PositionSide.Long ? 1m : -1m;
            decimal gross = (exitPx - entryPrice) * dirSign * pointValue * qty;
            decimal fees = config.ApplyFees ? 2m * feePerSide * qty : 0m;
            decimal net = gross - fees;
            if (market) totalSlippageDollars += slipTicks * tickValue * qty;

            trades.Add(new OhlcBacktestTrade
            {
                Symbol = symbol,
                Side = side,
                Quantity = qty,
                EntryTime = entryTime,
                ExitTime = time,
                EntryPrice = entryPrice,
                ExitPrice = exitPx,
                EntryBarIndex = entryBar,
                ExitBarIndex = barIdx,
                GrossPnL = gross,
                Fees = fees,
                NetPnL = net,
                ExitReason = reason,
                StopLossPrice = sl,
                TakeProfitPrice = tp,
                Ambiguous = ambiguous
            });
            realized += net;
            side = PositionSide.Flat;
        }
    }

    private static decimal PerSideFee(FeeProfile f) =>
        f.CommissionPerSide + f.ExchangeFeePerSide + f.ClearingFeePerSide
        + f.RoutingFeePerSide + f.NfaFeePerSide + f.OtherFeePerSide;

    private static Candle ToStrategyCandle(Candle c) => c;

    /// <summary>
    /// Prüft die Schutzorders (SL/TP) einer offenen Position gegen die Bar-Range. Die Fill-Regeln sind
    /// nach Ordertyp differenziert:
    /// <list type="bullet">
    /// <item><b>Stop-Loss (Stop-Order → wird bei Auslösung zur Market-Order):</b> Öffnet der Bar bereits
    /// jenseits des Stops (Gap), wird zum OPEN gefüllt – das ist der bereits ungünstigere Marktpreis, kein
    /// Fill zum alten Level. Ohne Gap wird an <c>sl</c> mit adverser Slippage gefüllt (Market-Fill).</item>
    /// <item><b>Take-Profit (Limit-Order):</b> Ein Limit wird nie schlechter als seine Grenze gefüllt.
    /// Öffnet der Bar günstiger als die TP-Grenze (Gap über TP long / unter TP short), erfolgt eine
    /// PREISVERBESSERUNG zum OPEN. Wird die Grenze intrabar erreicht (ohne Gap), wird exakt an <c>tp</c>
    /// gefüllt – ohne Slippage (Limit).</item>
    /// </list>
    /// Werden SL und TP in derselben Kerze berührt und die Reihenfolge ist unbekannt, wird konservativ der
    /// Stop-Loss angenommen und der Trade als mehrdeutig markiert. Rückgabe inkl. Flag, ob es ein Market-Fill
    /// war (nur dann wird informative Slippage erfasst; ein Gap-Fill zum OPEN und ein Limit-Fill erfassen
    /// keine zusätzliche Slippage, da der Preis bereits den realistischen Fill widerspiegelt).
    /// </summary>
    private static ExitCheck CheckStopTarget(Candle bar, PositionSide side, decimal sl, decimal tp, decimal tickSize, decimal slip)
    {
        if (side == PositionSide.Long)
        {
            // Stop-Gap unter SL: Stop wird zur Market-Order, Fill zum (bereits schlechteren) OPEN.
            if (bar.Open <= sl) return ExitCheck.At(bar.Open, OhlcExitReason.StopLoss, market: false, atOpen: true);
            // Limit-Gap über TP: Preisverbesserung zum OPEN (>= Limit tp).
            if (bar.Open >= tp) return ExitCheck.At(bar.Open, OhlcExitReason.TakeProfit, market: false, atOpen: true);
            bool slHit = bar.Low <= sl, tpHit = bar.High >= tp;
            if (slHit && tpHit) return ExitCheck.At(sl - slip, OhlcExitReason.StopLoss, market: true, ambiguous: true);
            if (slHit) return ExitCheck.At(sl - slip, OhlcExitReason.StopLoss, market: true);                            // Stop: Market + Slippage
            if (tpHit) return ExitCheck.At(tp, OhlcExitReason.TakeProfit, market: false);                                // Limit-Fill exakt an tp
            return ExitCheck.None;
        }
        else
        {
            // Stop-Gap über SL (Short): Fill zum OPEN.
            if (bar.Open >= sl) return ExitCheck.At(bar.Open, OhlcExitReason.StopLoss, market: false, atOpen: true);
            // Limit-Gap unter TP (Short): Preisverbesserung zum OPEN (<= Limit tp).
            if (bar.Open <= tp) return ExitCheck.At(bar.Open, OhlcExitReason.TakeProfit, market: false, atOpen: true);
            bool slHit = bar.High >= sl, tpHit = bar.Low <= tp;
            if (slHit && tpHit) return ExitCheck.At(sl + slip, OhlcExitReason.StopLoss, market: true, ambiguous: true);
            if (slHit) return ExitCheck.At(sl + slip, OhlcExitReason.StopLoss, market: true);                            // Stop: Market + Slippage
            if (tpHit) return ExitCheck.At(tp, OhlcExitReason.TakeProfit, market: false);                                // Limit-Fill exakt an tp
            return ExitCheck.None;
        }
    }

    private readonly struct ExitCheck
    {
        public bool Hit { get; init; }
        public decimal Price { get; init; }
        public OhlcExitReason Reason { get; init; }
        public bool Ambiguous { get; init; }
        public bool Market { get; init; }      // Market-Fill (Slippage erfassen)?
        public bool AtOpen { get; init; }
        public DateTimeOffset Time(Candle bar) => AtOpen ? bar.OpenTime : bar.CloseTime;
        public static readonly ExitCheck None = new() { Hit = false };
        public static ExitCheck At(decimal price, OhlcExitReason reason, bool market, bool ambiguous = false, bool atOpen = false)
            => new() { Hit = true, Price = price, Reason = reason, Market = market, Ambiguous = ambiguous, AtOpen = atOpen };
    }
}
