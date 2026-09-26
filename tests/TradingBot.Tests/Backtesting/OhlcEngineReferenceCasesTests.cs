using FluentAssertions;
using TradingBot.Backtesting;
using TradingBot.Backtesting.Ohlc;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using Xunit;

namespace TradingBot.Tests.Backtesting;

/// <summary>
/// UNABHÄNGIGE Referenzfälle für die OHLC-Engine (fachliche Prüfung). Anders als
/// <see cref="OhlcBacktestEngineTests"/> (TickSize=PointValue=1) werden hier realistische MES-artige
/// Kontraktwerte benutzt (TickSize 0.25, TickValue 1.25, PointValue 5.00, Gebühr 0.39/Seite, Slippage
/// 1 Tick) und Positionsgröße > 1. Alle Sollwerte sind HAND berechnet und als Literale hinterlegt —
/// sie stammen NICHT aus der Engine-Logik. So werden PointValue×Menge, Roundtrip-Gebühren, im
/// Fill-Preis enthaltene Slippage und die Fill-Regeln je Ordertyp unabhängig gegengerechnet.
///
/// Referenzarithmetik (MES): TickSize 0.25, PointValue 5.00 => 1 Punkt = 5.00 $, 1 Tick = 1.25 $.
/// Slippage 1 Tick = 0.25 Preis. Gebühr 0.39/Seite => Roundtrip 0.78/Kontrakt.
/// </summary>
public class OhlcEngineReferenceCasesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 2, 14, 30, 0, TimeSpan.Zero);

    private static Candle C(int i, decimal o, decimal h, decimal l, decimal c) => new()
    {
        Symbol = "MES",
        OpenTime = T0.AddMinutes(5 * i),
        CloseTime = T0.AddMinutes(5 * (i + 1)),
        Open = o, High = h, Low = l, Close = c, Volume = 0m
    };

    // MES-artiges Beispielprofil (identische Zahlen wie config/instruments/mes.example.json).
    private static InstrumentProfile Mes() => new()
    {
        Symbol = "MES", BrokerSymbol = "MES", Exchange = "CME", Currency = "USD",
        TickSize = 0.25m, TickValue = 1.25m, PointValue = 5.00m, ContractMultiplier = 5.00m,
        MaxContracts = 20, DefaultStopLossTicks = 40, DefaultTakeProfitTicks = 60
    };

    // Gebühr 0.39/Seite als Summe der Einzelposten (wie amp-rithmic-mes.example.json), Slippage 1 Tick.
    private static FeeProfile MesFee() => new()
    {
        BrokerName = "AMP", ExecutionProvider = "Rithmic", Instrument = "MES",
        CommissionPerSide = 0.25m, ExchangeFeePerSide = 0.10m, ClearingFeePerSide = 0.02m,
        NfaFeePerSide = 0.02m, EstimatedSlippageTicks = 1.0m
    };

    /// <summary>Emittiert an definierten 0-basierten Bar-Indizes (OnCandle-Zähler) ein Long/Short-Signal.</summary>
    private sealed class ScriptedStrategy : IStrategy
    {
        private readonly Dictionary<int, SignalDirection> _byIndex;
        private int _i = -1;
        public ScriptedStrategy(Dictionary<int, SignalDirection> byIndex) => _byIndex = byIndex;
        public string Name => "RefScripted";
        public TradeSignal? OnCandle(Candle c)
        {
            _i++;
            return _byIndex.TryGetValue(_i, out var d)
                ? new TradeSignal { StrategyName = Name, Symbol = c.Symbol, Direction = d, Timestamp = c.CloseTime, ReferencePrice = c.Close }
                : null;
        }
        public void Reset() => _i = -1;
    }

    private static OhlcBacktestResult Run(IReadOnlyList<Candle> bars, Dictionary<int, SignalDirection> signals,
        int qty, int sl = 40, int tp = 60) =>
        new OhlcBacktestEngine().Run(bars, new ScriptedStrategy(signals), Mes(), MesFee(),
            new OhlcBacktestConfig { Quantity = qty, StopLossTicks = sl, TakeProfitTicks = tp, SlippageTicksOverride = 1m },
            "reference", 5);

    [Fact]
    public void RC1_long_qty2_take_profit_hand_computed()
    {
        // Signal an Bar 1 -> Entry am OPEN von Bar 2 (=5000) + 1 Tick Slippage = 5000.25.
        // TP = Entry + 60 Ticks = 5000.25 + 15.00 = 5015.25. Bar 3 High 5020 >= TP (kein Gap: Open 5001 < TP).
        var bars = new[]
        {
            C(0, 5000, 5001, 4999, 5000),
            C(1, 5000, 5001, 4999, 5000),   // Long-Signal
            C(2, 5000, 5001, 4999, 5000),   // Entry am Open
            C(3, 5001, 5020, 5000, 5010),   // TP-Treffer (Limit)
        };
        var t = Run(bars, new() { [1] = SignalDirection.Long }, qty: 2).Trades.Should().ContainSingle().Subject;

        t.EntryBarIndex.Should().Be(2);
        t.ExitBarIndex.Should().Be(3);
        t.Side.Should().Be(PositionSide.Long);
        t.EntryPrice.Should().Be(5000.25m);            // 5000 + 1 Tick
        t.StopLossPrice.Should().Be(4990.25m);         // 5000.25 - 40 Ticks (10.00)
        t.TakeProfitPrice.Should().Be(5015.25m);       // 5000.25 + 60 Ticks (15.00)
        t.ExitReason.Should().Be(OhlcExitReason.TakeProfit);
        t.ExitPrice.Should().Be(5015.25m);             // Limit-Fill exakt an TP, keine Slippage
        t.GrossPnL.Should().Be(150.00m);               // (5015.25-5000.25)=15.00 Punkte * 5.00 $ * 2
        t.Fees.Should().Be(1.56m);                     // 2 Seiten * 0.39 * 2 Kontrakte
        t.NetPnL.Should().Be(148.44m);                 // 150.00 - 1.56
    }

    [Fact]
    public void RC2_short_qty3_stop_loss_hand_computed()
    {
        // Short-Entry am Open 5000 - 1 Tick = 4999.75. SL = 4999.75 + 40 Ticks (10.00) = 5009.75.
        // Bar 3 High 5010 >= SL (kein Gap: Open 5001 < SL) -> Stop-Market an SL + 1 Tick Slippage = 5010.00.
        var bars = new[]
        {
            C(0, 5000, 5001, 4999, 5000),
            C(1, 5000, 5001, 4999, 5000),   // Short-Signal
            C(2, 5000, 5001, 4999, 5000),   // Entry am Open
            C(3, 5001, 5010, 5000, 5005),   // SL-Treffer
        };
        var t = Run(bars, new() { [1] = SignalDirection.Short }, qty: 3).Trades.Should().ContainSingle().Subject;

        t.Side.Should().Be(PositionSide.Short);
        t.EntryPrice.Should().Be(4999.75m);
        t.StopLossPrice.Should().Be(5009.75m);
        t.TakeProfitPrice.Should().Be(4984.75m);       // 4999.75 - 60 Ticks (15.00)
        t.ExitReason.Should().Be(OhlcExitReason.StopLoss);
        t.ExitPrice.Should().Be(5010.00m);             // SL + 1 Tick adverse Slippage
        t.GrossPnL.Should().Be(-153.75m);              // (4999.75-5010.00)=-10.25 Punkte * 5.00 * 3
        t.Fees.Should().Be(2.34m);                     // 2 * 0.39 * 3
        t.NetPnL.Should().Be(-156.09m);
    }

    [Fact]
    public void RC3_long_take_profit_limit_gap_is_price_improved()
    {
        // Bar 3 öffnet mit Gap ÜBER dem TP (5015.25): Limit -> Preisverbesserung zum OPEN 5020.00.
        var bars = new[]
        {
            C(0, 5000, 5001, 4999, 5000),
            C(1, 5000, 5001, 4999, 5000),
            C(2, 5000, 5001, 4999, 5000),
            C(3, 5020, 5025, 5018, 5022),   // Gap über TP
        };
        var t = Run(bars, new() { [1] = SignalDirection.Long }, qty: 1).Trades.Should().ContainSingle().Subject;

        t.ExitReason.Should().Be(OhlcExitReason.TakeProfit);
        t.ExitPrice.Should().Be(5020.00m);             // OPEN, besser als das Limit 5015.25
        t.ExitPrice.Should().BeGreaterThanOrEqualTo(t.TakeProfitPrice);
        t.GrossPnL.Should().Be(98.75m);                // (5020.00-5000.25)=19.75 * 5.00
        t.NetPnL.Should().Be(97.97m);                  // - 0.78
    }

    [Fact]
    public void RC4_long_stop_gap_fills_at_open_minus_slippage_not_old_level()
    {
        // Bar 3 öffnet mit Gap UNTER dem SL (4990.25): Stop -> Market zum OPEN 4980.00 MINUS 1 Tick Slippage.
        var bars = new[]
        {
            C(0, 5000, 5001, 4999, 5000),
            C(1, 5000, 5001, 4999, 5000),
            C(2, 5000, 5001, 4999, 5000),
            C(3, 4980, 4985, 4975, 4978),   // Gap unter SL
        };
        var r = Run(bars, new() { [1] = SignalDirection.Long }, qty: 1);
        var t = r.Trades.Should().ContainSingle().Subject;

        t.ExitReason.Should().Be(OhlcExitReason.StopLoss);
        t.ExitPrice.Should().Be(4979.75m);             // OPEN 4980 - 1 Tick adverse Slippage (Gap-Stop = Market)
        t.ExitPrice.Should().BeLessThan(t.StopLossPrice); // schlechter als SL, kein Fill zum alten Level
        t.GrossPnL.Should().Be(-102.50m);              // (4979.75-5000.25)=-20.50 * 5.00
        t.NetPnL.Should().Be(-103.28m);                // - 0.78
        // Entry (Market) + Gap-Stop (Market) = 2 Seiten Slippage informativ; NICHT zusätzlich vom Netto abgezogen.
        r.Statistics.TotalSlippage.Should().Be(2.50m); // 2 * (1 Tick * 1.25 $)
        r.Statistics.NetProfit.Should().Be(r.Statistics.GrossProfit - r.Statistics.TotalFees);
    }

    [Fact]
    public void RC4S_short_stop_gap_fills_at_open_plus_slippage()
    {
        // Short-Entry 4999.75, SL 5009.75. Bar 3 öffnet mit Gap ÜBER dem SL: Fill zum OPEN 5020.00 PLUS Slippage.
        var bars = new[]
        {
            C(0, 5000, 5001, 4999, 5000),
            C(1, 5000, 5001, 4999, 5000),   // Short-Signal
            C(2, 5000, 5001, 4999, 5000),   // Entry am Open
            C(3, 5020, 5025, 5018, 5022),   // Gap über SL
        };
        var r = Run(bars, new() { [1] = SignalDirection.Short }, qty: 1);
        var t = r.Trades.Should().ContainSingle().Subject;

        t.Side.Should().Be(PositionSide.Short);
        t.StopLossPrice.Should().Be(5009.75m);
        t.ExitReason.Should().Be(OhlcExitReason.StopLoss);
        t.ExitPrice.Should().Be(5020.25m);             // OPEN 5020 + 1 Tick adverse Slippage
        t.ExitPrice.Should().BeGreaterThan(t.StopLossPrice);
        t.GrossPnL.Should().Be(-102.50m);              // (4999.75-5020.25)=-20.50 * 5.00
        t.NetPnL.Should().Be(-103.28m);
        r.Statistics.TotalSlippage.Should().Be(2.50m);
        r.Statistics.NetProfit.Should().Be(r.Statistics.GrossProfit - r.Statistics.TotalFees);
    }

    [Fact]
    public void RC9_reversal_closes_old_position_once_and_new_protection_comes_from_new_entry()
    {
        // Long offen, dann Gegensignal Short. Prüft: alte Position genau EINMAL geschlossen; die Schutzorders
        // der neuen Short-Position stammen aus deren Entry (nicht aus der alten Long-Position).
        var bars = new[]
        {
            C(0, 5000, 5001, 4999, 5000),
            C(1, 5000, 5001, 4999, 5000),   // Long-Signal
            C(2, 5000, 5001, 4999, 5000),   // Long-Entry am Open 5000 (Entry 5000.25, SL 4990.25)
            C(3, 5000, 5001, 4999, 5000),   // Short-Signal (Gegensignal)
            C(4, 5000, 5001, 4999, 5000),   // Reversal am Open 5000
            C(5, 5000, 5001, 4999, 5000),   // Ende -> Short EndOfData
        };
        var r = Run(bars, new() { [1] = SignalDirection.Long, [3] = SignalDirection.Short }, qty: 1);

        r.Trades.Should().HaveCount(2, "jede Position wird genau einmal geschlossen");
        var lng = r.Trades[0];
        lng.Side.Should().Be(PositionSide.Long);
        lng.ExitReason.Should().Be(OhlcExitReason.OppositeSignal);
        lng.ExitBarIndex.Should().Be(4);
        // genau ein Long-Trade mit diesem Entry-Bar (kein zweiter Exit derselben Position)
        r.Trades.Count(x => x.Side == PositionSide.Long && x.EntryBarIndex == 2).Should().Be(1);

        var sht = r.Trades[1];
        sht.Side.Should().Be(PositionSide.Short);
        sht.EntryBarIndex.Should().Be(4);
        sht.EntryPrice.Should().Be(4999.75m);          // Reversal-Entry am Open 5000 - Slippage
        sht.StopLossPrice.Should().Be(5009.75m, "SL aus dem neuen Short-Entry (4999.75 + 40 Ticks)");
        sht.TakeProfitPrice.Should().Be(4984.75m, "TP aus dem neuen Short-Entry (4999.75 - 60 Ticks)");
        sht.StopLossPrice.Should().NotBe(lng.StopLossPrice, "die alte Long-Schutzorder gilt nicht für die neue Position");
    }

    [Fact]
    public void RC5_stop_and_target_same_candle_is_conservative_stop_and_ambiguous()
    {
        // Bar 3 berührt SL (Low 4985 <= 4990.25) UND TP (High 5020 >= 5015.25) -> konservativ SL, mehrdeutig.
        var bars = new[]
        {
            C(0, 5000, 5001, 4999, 5000),
            C(1, 5000, 5001, 4999, 5000),
            C(2, 5000, 5001, 4999, 5000),
            C(3, 5000, 5020, 4985, 5000),
        };
        var t = Run(bars, new() { [1] = SignalDirection.Long }, qty: 1).Trades.Should().ContainSingle().Subject;

        t.ExitReason.Should().Be(OhlcExitReason.StopLoss);
        t.Ambiguous.Should().BeTrue();
        t.ExitPrice.Should().Be(4990.00m);             // SL - 1 Tick Slippage
        t.NetPnL.Should().Be(-52.03m);                 // (4990.00-5000.25)=-10.25*5 = -51.25 - 0.78
    }

    [Fact]
    public void RC6_entry_and_exit_in_same_candle()
    {
        // Signal an Bar 1 -> Entry am OPEN von Bar 2; DIESELBE Kerze berührt den SL -> Exit auf Bar 2.
        var bars = new[]
        {
            C(0, 5000, 5001, 4999, 5000),
            C(1, 5000, 5001, 4999, 5000),   // Long-Signal
            C(2, 5000, 5001, 4985, 4990),   // Entry am Open 5000; Low 4985 <= SL 4990.25 -> Exit hier
            C(3, 4990, 4992, 4988, 4990),
        };
        var t = Run(bars, new() { [1] = SignalDirection.Long }, qty: 1).Trades.Should().ContainSingle().Subject;

        t.EntryBarIndex.Should().Be(2);
        t.ExitBarIndex.Should().Be(2, "Entry und Exit liegen in derselben Kerze");
        t.ExitReason.Should().Be(OhlcExitReason.StopLoss);
        t.ExitPrice.Should().Be(4990.00m);
        t.NetPnL.Should().Be(-52.03m);
    }

    [Fact]
    public void RC7_opposite_signal_at_next_open_beats_active_stop_gap()
    {
        // Long offen mit SL 4990.25. Bei Bar 3 Gegensignal (Short); Bar 4 öffnet mit Gap UNTER dem SL.
        // Regel: das Gegensignal schließt zuerst am OPEN (Grund OppositeSignal), nicht als StopLoss.
        var bars = new[]
        {
            C(0, 5000, 5001, 4999, 5000),
            C(1, 5000, 5001, 4999, 5000),   // Long-Signal
            C(2, 5000, 5001, 4999, 5000),   // Long-Entry am Open 5000 (Entry 5000.25, SL 4990.25)
            C(3, 5000, 5001, 4999, 5000),   // Short-Signal (Gegensignal)
            C(4, 4980, 4985, 4975, 4980),   // Gap unter SL: Long-Exit + Short-Entry am OPEN
            C(5, 4980, 4981, 4979, 4980),   // Ende -> Short EndOfData
        };
        var r = Run(bars, new() { [1] = SignalDirection.Long, [3] = SignalDirection.Short }, qty: 1);

        r.Trades.Should().HaveCount(2);
        var lng = r.Trades[0];
        lng.Side.Should().Be(PositionSide.Long);
        lng.ExitBarIndex.Should().Be(4);
        lng.ExitReason.Should().Be(OhlcExitReason.OppositeSignal, "Gegensignal hat Vorrang vor dem gleichzeitig greifenden Gap-Stop");
        lng.ExitPrice.Should().Be(4979.75m);           // OPEN 4980 - 1 Tick Slippage (Market)
        lng.NetPnL.Should().Be(-103.28m);              // (4979.75-5000.25)=-20.50*5 = -102.50 - 0.78

        var sht = r.Trades[1];
        sht.Side.Should().Be(PositionSide.Short);
        sht.EntryBarIndex.Should().Be(4);
        sht.EntryPrice.Should().Be(4979.75m);
        sht.ExitReason.Should().Be(OhlcExitReason.EndOfData);
    }

    [Fact]
    public void RC8_aggregate_metrics_reconcile_with_independent_recompute()
    {
        // Drei Long-Trades (qty 2): TP-Gewinn, SL-Verlust, TP-Gewinn. Signale an 1/5/9.
        var bars = new[]
        {
            C(0, 5000, 5001, 4999, 5000),
            C(1, 5000, 5001, 4999, 5000),   // Signal 1
            C(2, 5000, 5001, 4999, 5000),   // Entry 1
            C(3, 5001, 5020, 5000, 5010),   // TP 1  (+148.44)
            C(4, 5000, 5001, 4999, 5000),
            C(5, 5000, 5001, 4999, 5000),   // Signal 2
            C(6, 5000, 5001, 4999, 5000),   // Entry 2
            C(7, 5000, 5001, 4985, 4990),   // SL 2  (-104.06)
            C(8, 5000, 5001, 4999, 5000),
            C(9, 5000, 5001, 4999, 5000),   // Signal 3
            C(10, 5000, 5001, 4999, 5000),  // Entry 3
            C(11, 5001, 5020, 5000, 5010),  // TP 3  (+148.44)
        };
        var r = Run(bars, new() { [1] = SignalDirection.Long, [5] = SignalDirection.Long, [9] = SignalDirection.Long }, qty: 2);
        var s = r.Statistics;

        // Einzeltrades gegen Handwerte.
        r.Trades.Select(t => t.NetPnL).Should().Equal(148.44m, -104.06m, 148.44m);

        // --- Unabhängige Nachrechnung direkt aus der Trade-Liste (getrennt vom Calculator) ---
        var nets = r.Trades.Select(t => t.NetPnL).ToList();
        decimal sumNet = nets.Sum();
        decimal sumGross = r.Trades.Sum(t => t.GrossPnL);
        decimal sumFees = r.Trades.Sum(t => t.Fees);
        decimal winSum = nets.Where(n => n > 0).Sum();
        decimal lossSum = nets.Where(n => n < 0).Sum();
        int wins = nets.Count(n => n > 0), losses = nets.Count(n => n < 0);
        decimal expectedPf = winSum / Math.Abs(lossSum);
        // Max-Drawdown der realisierten Kurve, Startpeak 0 (gleiche Definition wie im Calculator, hier eigenständig).
        decimal run = 0m, peak = 0m, maxDd = 0m;
        foreach (var np in nets) { run += np; peak = Math.Max(peak, run); maxDd = Math.Max(maxDd, peak - run); }

        // Kern-Identitäten.
        sumNet.Should().Be(192.82m);
        s.NetProfit.Should().Be(sumNet, "Σ Trade-Netto-PnL = Gesamt-Netto-PnL");
        s.GrossProfit.Should().Be(sumGross);
        s.TotalFees.Should().Be(sumFees).And.Be(4.68m);   // 3 Trades * 0.78 Roundtrip * 2 Kontrakte
        (s.GrossProfit - s.TotalFees).Should().Be(s.NetProfit);
        (r.InitialBalance + s.NetProfit).Should().Be(r.FinalEquity);
        r.FinalEquity.Should().Be(10_192.82m);            // Standard-Startkapital 10.000

        // Kennzahlen unabhängig gegengerechnet.
        s.WinRate.Should().Be((decimal)wins / r.Trades.Count).And.Be(2m / 3m);
        s.ProfitFactor.Should().Be(expectedPf);
        s.Expectancy.Should().Be(sumNet / r.Trades.Count);
        s.MaxDrawdown.Should().Be(maxDd).And.Be(104.06m);
        s.LargestWin.Should().Be(148.44m);
        s.LargestLoss.Should().Be(-104.06m);

        // Slippage informativ: 3 Trades, jeweils Entry (Market) + Exit;
        // Trade 2 ist ein Stop (Market) => 2 Seiten, Trades 1 und 3 sind TP-Limits => nur die Entry-Seite.
        // (2 Market-Fills * 1.25 $ * 2) + (1 Market-Fill * 1.25 $ * 2) * 2  ... hier explizit:
        // Entries: 3 * (1 Tick * 1.25 $ * 2) = 7.50 ; Stop-Exit: 1 * 1.25 * 2 = 2.50 => 10.00.
        s.TotalSlippage.Should().Be(10.00m);
        // Slippage darf NICHT zusätzlich vom Netto abgezogen sein:
        s.NetProfit.Should().Be(s.GrossProfit - s.TotalFees);
    }
}
