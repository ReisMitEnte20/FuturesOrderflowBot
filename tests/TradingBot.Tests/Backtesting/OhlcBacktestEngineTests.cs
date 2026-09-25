using FluentAssertions;
using TradingBot.Backtesting;
using TradingBot.Backtesting.Ohlc;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using Xunit;

namespace TradingBot.Tests.Backtesting;

/// <summary>
/// Prüft die bar-basierte OHLC-Ausführung: kein Look-ahead, Ausführung nach Schlusskurssignal,
/// Long/Short-PnL inkl. Kosten, SL/TP/Gap/mehrdeutige Kerzen, leere/Teilkerzen-Daten, Reproduzierbarkeit.
/// </summary>
public class OhlcBacktestEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 14, 0, 0, TimeSpan.Zero);

    private static Candle C(int min, decimal o, decimal h, decimal l, decimal c) => new()
    {
        Symbol = "TEST",
        OpenTime = T0.AddMinutes(min),
        CloseTime = T0.AddMinutes(min + 1),
        Open = o, High = h, Low = l, Close = c, Volume = 0m
    };

    // 1 Punkt = 1 $, 1 Tick = 1 Punkt -> einfache Zahlen.
    private static InstrumentProfile Instr(int slDef = 1000, int tpDef = 1000) => new()
    {
        Symbol = "TEST", BrokerSymbol = "TEST", Exchange = "X", Currency = "USD",
        TickSize = 1m, TickValue = 1m, PointValue = 1m, ContractMultiplier = 1m,
        MaxContracts = 10, DefaultStopLossTicks = slDef, DefaultTakeProfitTicks = tpDef
    };

    private static FeeProfile Fee(decimal commission = 0m, decimal slipTicks = 0m) => new()
    {
        BrokerName = "T", ExecutionProvider = "T", Instrument = "TEST",
        CommissionPerSide = commission, EstimatedSlippageTicks = slipTicks
    };

    /// <summary>Emittiert an definierten (0-basierten) Bar-Indizes ein Long/Short-Signal.</summary>
    private sealed class ScriptedStrategy : IStrategy
    {
        private readonly Dictionary<int, SignalDirection> _byIndex;
        private int _i = -1;
        public ScriptedStrategy(Dictionary<int, SignalDirection> byIndex) => _byIndex = byIndex;
        public string Name => "Scripted";
        public TradeSignal? OnCandle(Candle c)
        {
            _i++;
            return _byIndex.TryGetValue(_i, out var d)
                ? new TradeSignal { StrategyName = Name, Symbol = c.Symbol, Direction = d, Timestamp = c.CloseTime, ReferencePrice = c.Close }
                : null;
        }
        public void Reset() => _i = -1;
    }

    private static OhlcBacktestResult Run(IReadOnlyList<Candle> bars, ScriptedStrategy s,
        OhlcBacktestConfig cfg, InstrumentProfile? instr = null, FeeProfile? fee = null, bool lead = false, bool trail = false)
        => new OhlcBacktestEngine().Run(bars, s, instr ?? Instr(), fee ?? Fee(), cfg, "test", 1, lead, trail);

    [Fact]
    public void Signal_on_close_executes_at_next_bar_open_no_lookahead()
    {
        var bars = new[]
        {
            C(0, 100, 101, 99, 100),
            C(1, 100, 101, 99, 100),   // Long-Signal am Schluss von Bar 1
            C(2, 100, 110, 100, 105),  // Entry erwartet am OPEN (=100) DIESES Bars
            C(3, 105, 110, 104, 108),
            C(4, 108, 112, 107, 110),  // Ende -> Exit am Close (=110)
        };
        var s = new ScriptedStrategy(new() { [1] = SignalDirection.Long });
        var r = Run(bars, s, new OhlcBacktestConfig { Quantity = 1, StopLossTicks = 1000, TakeProfitTicks = 1000 });

        r.Trades.Should().ContainSingle();
        var t = r.Trades[0];
        t.EntryBarIndex.Should().Be(2, "das Signal aus Bar 1 darf frühestens am OPEN von Bar 2 ausgeführt werden");
        t.EntryPrice.Should().Be(100m);
        t.EntryTime.Should().Be(bars[2].OpenTime);
        t.ExitReason.Should().Be(OhlcExitReason.EndOfData);
        t.ExitPrice.Should().Be(110m);
        t.NetPnL.Should().Be(10m);   // (110-100)*1*1, keine Kosten
    }

    [Fact]
    public void Short_pnl_includes_fees_and_slippage()
    {
        var bars = new[]
        {
            C(0, 100, 101, 99, 100),
            C(1, 100, 101, 99, 100),   // Short-Signal
            C(2, 100, 101, 99, 100),   // Entry am Open=100, Slippage 1 -> Fill 99
            C(3, 95, 96, 93, 95),      // Low 93 <= TP(94) -> TP-Fill 94
        };
        var s = new ScriptedStrategy(new() { [1] = SignalDirection.Short });
        var r = Run(bars, s,
            new OhlcBacktestConfig { Quantity = 1, StopLossTicks = 5, TakeProfitTicks = 5 },
            fee: Fee(commission: 0.5m, slipTicks: 1m));

        var t = r.Trades.Should().ContainSingle().Subject;
        t.Side.Should().Be(PositionSide.Short);
        t.EntryPrice.Should().Be(99m);              // 100 - 1 Tick Slippage (adverse)
        t.ExitReason.Should().Be(OhlcExitReason.TakeProfit);
        t.ExitPrice.Should().Be(94m);               // TP = 99 - 5, Limit-Fill ohne Slippage
        t.GrossPnL.Should().Be(5m);                 // (99 - 94) * 1 * 1
        t.Fees.Should().Be(1m);                     // 2 Seiten * 0.5
        t.NetPnL.Should().Be(4m);
    }

    [Fact]
    public void Stop_loss_take_profit_gap_and_ambiguous_bar()
    {
        // Long, Entry am Open=100, SL=95, TP=105 (5 Ticks).
        OhlcBacktestTrade OneTrade(Candle exitBar)
        {
            var bars = new[] { C(0, 100, 101, 99, 100), C(1, 100, 101, 99, 100), C(2, 100, 100, 100, 100), exitBar };
            var s = new ScriptedStrategy(new() { [1] = SignalDirection.Long });
            return Run(bars, s, new OhlcBacktestConfig { Quantity = 1, StopLossTicks = 5, TakeProfitTicks = 5 }).Trades.Single();
        }

        // TP: High 106 >= 105
        var tp = OneTrade(C(3, 100, 106, 99, 101));
        tp.ExitReason.Should().Be(OhlcExitReason.TakeProfit);
        tp.ExitPrice.Should().Be(105m);
        tp.Ambiguous.Should().BeFalse();

        // SL: Low 94 <= 95 (kein Gap, Open 100)
        var sl = OneTrade(C(3, 100, 101, 94, 96));
        sl.ExitReason.Should().Be(OhlcExitReason.StopLoss);
        sl.ExitPrice.Should().Be(95m);

        // Gap: Open 90 < SL 95 -> Fill am Open (nicht zum alten Level)
        var gap = OneTrade(C(3, 90, 91, 88, 89));
        gap.ExitReason.Should().Be(OhlcExitReason.StopLoss);
        gap.ExitPrice.Should().Be(90m);

        // Mehrdeutig: SL (Low 94) UND TP (High 106) in derselben Kerze -> konservativ SL, markiert
        var amb = OneTrade(C(3, 100, 106, 94, 100));
        amb.ExitReason.Should().Be(OhlcExitReason.StopLoss);
        amb.ExitPrice.Should().Be(95m);
        amb.Ambiguous.Should().BeTrue();
    }

    [Fact]
    public void Take_profit_limit_gap_is_price_improved_to_open_never_worse_than_limit()
    {
        // LONG: Entry Open=100, TP=105 (5 Ticks), SL weit weg. Nächster Bar öffnet mit Gap ÜBER dem Limit
        // (110). Ein Limit wird nie schlechter als seine Grenze gefüllt -> Preisverbesserung zum OPEN.
        {
            var bars = new[] { C(0, 100, 101, 99, 100), C(1, 100, 101, 99, 100), C(2, 100, 100, 100, 100), C(3, 110, 112, 109, 111) };
            var s = new ScriptedStrategy(new() { [1] = SignalDirection.Long });
            var t = Run(bars, s, new OhlcBacktestConfig { Quantity = 1, StopLossTicks = 50, TakeProfitTicks = 5 }).Trades.Single();
            t.ExitReason.Should().Be(OhlcExitReason.TakeProfit);
            t.ExitPrice.Should().Be(110m, "eine günstige Kurslücke füllt das Limit zum besseren OPEN");
            t.ExitPrice.Should().BeGreaterThanOrEqualTo(105m, "ein Limit wird nie schlechter als seine Grenze gefüllt");
            t.Ambiguous.Should().BeFalse();
        }

        // SHORT: Entry Open=100, TP=95, SL weit weg. Nächster Bar öffnet mit Gap UNTER dem Limit (90).
        {
            var bars = new[] { C(0, 100, 101, 99, 100), C(1, 100, 101, 99, 100), C(2, 100, 100, 100, 100), C(3, 90, 91, 88, 89) };
            var s = new ScriptedStrategy(new() { [1] = SignalDirection.Short });
            var t = Run(bars, s, new OhlcBacktestConfig { Quantity = 1, StopLossTicks = 50, TakeProfitTicks = 5 }).Trades.Single();
            t.ExitReason.Should().Be(OhlcExitReason.TakeProfit);
            t.ExitPrice.Should().Be(90m);
            t.ExitPrice.Should().BeLessThanOrEqualTo(95m, "ein Short-Limit wird nie über seiner Grenze gefüllt");
        }
    }

    [Fact]
    public void Opposite_signal_reverses_position_at_next_open_before_intrabar_stops()
    {
        // Long ab Bar2-Open. Bei Bar3-Schluss Gegensignal (Short) -> am Bar4-OPEN wird der Long geschlossen
        // (Grund OppositeSignal, VOR den intrabar-Schutzorders) und der Short eröffnet. SL/TP weit weg.
        var bars = new[]
        {
            C(0, 100, 101, 99, 100),
            C(1, 100, 101, 99, 100),   // Long-Signal
            C(2, 100, 101, 99, 100),   // Long-Entry am Open=100
            C(3, 100, 101, 99, 100),   // Short-Signal (Gegensignal)
            C(4, 102, 103, 101, 102),  // Long-Exit + Short-Entry am Open=102
            C(5, 102, 103, 101, 102),  // Ende -> Short-Exit am Close=102
        };
        var s = new ScriptedStrategy(new() { [1] = SignalDirection.Long, [3] = SignalDirection.Short });
        var r = Run(bars, s, new OhlcBacktestConfig { Quantity = 1, StopLossTicks = 1000, TakeProfitTicks = 1000 });

        r.Trades.Should().HaveCount(2);
        var longTrade = r.Trades[0];
        longTrade.Side.Should().Be(PositionSide.Long);
        longTrade.ExitReason.Should().Be(OhlcExitReason.OppositeSignal);
        longTrade.ExitBarIndex.Should().Be(4);
        longTrade.ExitPrice.Should().Be(102m, "das Gegensignal schließt zum OPEN von Bar 4");

        var shortTrade = r.Trades[1];
        shortTrade.Side.Should().Be(PositionSide.Short);
        shortTrade.EntryBarIndex.Should().Be(4);
        shortTrade.EntryPrice.Should().Be(102m);
        shortTrade.ExitReason.Should().Be(OhlcExitReason.EndOfData);
    }

    [Fact]
    public void Empty_or_too_short_data_yields_clean_empty_result()
    {
        var s = new ScriptedStrategy(new());
        var r = Run(new[] { C(0, 100, 101, 99, 100) }, s, new OhlcBacktestConfig());
        r.Status.Should().Be(BacktestRunStatus.Completed);
        r.Trades.Should().BeEmpty();
        r.Statistics.TotalTrades.Should().Be(0);
        r.Statistics.ProfitFactor.Should().BeNull();   // kein gültiger Nenner
        r.Statistics.WinRate.Should().Be(0m);
    }

    [Fact]
    public void Partial_edge_candles_are_excluded_from_evaluation()
    {
        // Signal am (partiellen) LETZTEN Bar darf zu keinem Trade führen; Randkerzen ausgeschlossen.
        var bars = new[]
        {
            C(0, 100, 101, 99, 100),   // (leading partial -> ausgeschlossen)
            C(1, 100, 101, 99, 100),
            C(2, 100, 110, 100, 105),
            C(3, 105, 110, 104, 108),  // Signal hier
            C(4, 108, 112, 107, 110),  // (trailing partial -> ausgeschlossen)
        };
        var s = new ScriptedStrategy(new() { [3] = SignalDirection.Long });
        var r = Run(bars, s, new OhlcBacktestConfig { ExcludePartialEdges = true }, lead: true, trail: true);

        r.Data.EvaluatedBars.Should().Be(3);              // Bars 1..3
        r.Data.LeadingPartialExcluded.Should().BeTrue();
        r.Data.TrailingPartialExcluded.Should().BeTrue();
        r.Data.From.Should().Be(bars[1].OpenTime);
        // Signal am letzten ausgewerteten Bar (3) -> Entry wäre Bar 4, der ausgeschlossen ist -> kein Trade.
        r.Trades.Should().BeEmpty();
    }

    [Fact]
    public void Results_are_reproducible_for_identical_input()
    {
        Candle[] Bars() => new[]
        {
            C(0, 100, 101, 99, 100), C(1, 100, 101, 99, 100), C(2, 100, 106, 95, 101),
            C(3, 101, 108, 100, 107), C(4, 107, 112, 106, 110),
        };
        var cfg = new OhlcBacktestConfig { Quantity = 2, StopLossTicks = 8, TakeProfitTicks = 6 };
        var r1 = Run(Bars(), new ScriptedStrategy(new() { [1] = SignalDirection.Long, [3] = SignalDirection.Short }), cfg, fee: Fee(0.5m, 1m));
        var r2 = Run(Bars(), new ScriptedStrategy(new() { [1] = SignalDirection.Long, [3] = SignalDirection.Short }), cfg, fee: Fee(0.5m, 1m));

        r1.Statistics.Should().BeEquivalentTo(r2.Statistics);
        r1.Trades.Should().BeEquivalentTo(r2.Trades);
        r1.FinalEquity.Should().Be(r2.FinalEquity);
    }
}
