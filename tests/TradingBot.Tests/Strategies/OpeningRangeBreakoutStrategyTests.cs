using FluentAssertions;
using TradingBot.Application.Strategies;
using TradingBot.Backtesting.Ohlc;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using Xunit;

namespace TradingBot.Tests.Strategies;

/// <summary>
/// ORB-Baseline auf der echten OHLC-Engine: Long/Short, Doji, ein Trade je Session, kein Look-ahead,
/// dynamische SL/TP-Preise (R aus dem tatsächlichen Fill), Tick-Rundung, Gap-Ungültigkeit, Session-Schluss,
/// fehlende Kerzen (kein erfundener Fill) und Kosten.
/// </summary>
public sealed class OpeningRangeBreakoutStrategyTests
{
    // 2026-06-16 ist Sommerzeit (EDT, UTC-4): NY 09:30 = 13:30 UTC, NY 16:00 = 20:00 UTC.
    private static readonly DateTimeOffset Day1Open = new(2026, 6, 16, 13, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2Open = new(2026, 6, 17, 13, 30, 0, TimeSpan.Zero);
    private const int SessionEndBar = 77;   // 13:30 + 77·5min = 19:55 UTC Open → 20:00 UTC Close = NY 16:00

    private static Candle Bar(DateTimeOffset start, int k, decimal o, decimal h, decimal l, decimal c) => new()
    {
        Symbol = "MES",
        OpenTime = start.AddMinutes(5 * k),
        CloseTime = start.AddMinutes(5 * (k + 1)),
        Open = o, High = h, Low = l, Close = c, Volume = 0m
    };

    /// <summary>Eine volle RTH-Session (78 5-Min-Bars). <paramref name="custom"/> überschreibt einzelne Bars;
    /// der Rest ist flach auf <paramref name="flat"/>. <paramref name="lastBar"/> begrenzt die Session (fehlende Kerzen).</summary>
    private static List<Candle> Session(DateTimeOffset start, decimal flat,
        IDictionary<int, (decimal o, decimal h, decimal l, decimal c)> custom, int lastBar = SessionEndBar)
    {
        var bars = new List<Candle>();
        for (int k = 0; k <= lastBar; k++)
            bars.Add(custom.TryGetValue(k, out var v)
                ? Bar(start, k, v.o, v.h, v.l, v.c)
                : Bar(start, k, flat, flat, flat, flat));
        return bars;
    }

    private static InstrumentProfile Mes(decimal tick = 1m, decimal point = 1m, decimal value = 1m) => new()
    {
        Symbol = "MES", BrokerSymbol = "MES", Exchange = "CME", Currency = "USD",
        TickSize = tick, TickValue = value, PointValue = point, ContractMultiplier = point,
        MaxContracts = 10, DefaultStopLossTicks = 40, DefaultTakeProfitTicks = 60
    };

    private static FeeProfile Fee(decimal commission = 0m, decimal slipTicks = 0m) => new()
    {
        BrokerName = "T", ExecutionProvider = "T", Instrument = "MES",
        CommissionPerSide = commission, EstimatedSlippageTicks = slipTicks
    };

    private static OhlcBacktestResult Run(IReadOnlyList<Candle> bars, InstrumentProfile? instr = null, FeeProfile? fee = null)
        => new OhlcBacktestEngine().Run(bars, new OpeningRangeBreakoutStrategy(), instr ?? Mes(), fee ?? Fee(),
            new OhlcBacktestConfig { Quantity = 1, StopLossTicks = 40, TakeProfitTicks = 60, ExcludePartialEdges = false },
            "test", 5);

    [Fact]
    public void Long_entry_next_open_dynamic_sl_tp_and_session_close()
    {
        // Opening-Bar aufwärts (Long): O=100, C=102, Low=99 → Stop 99. Entry am Folgebar-Open (102). R=3, Ziel=10R=132.
        var custom = new Dictionary<int, (decimal, decimal, decimal, decimal)>
        {
            [0] = (100, 103, 99, 102),   // Opening Range
            [1] = (102, 102, 102, 102),  // Entry-Bar
        };
        var res = Run(Session(Day1Open, 102m, custom));

        res.Trades.Should().ContainSingle("höchstens ein Trade je Session");
        var t = res.Trades[0];
        t.Side.Should().Be(PositionSide.Long);
        t.EntryTime.Should().Be(Day1Open.AddMinutes(5), "Einstieg am OPEN des Folgebars (kein Look-ahead)");
        t.EntryPrice.Should().Be(102m);
        t.StopLossPrice.Should().Be(99m, "Gegenextrem der ersten Kerze");
        t.TakeProfitPrice.Should().Be(132m, "10R aus dem tatsächlichen Fill (R=3)");
        t.ExitReason.Should().Be(OhlcExitReason.SessionClose);
        t.ExitBarIndex.Should().Be(SessionEndBar);
    }

    [Fact]
    public void Short_entry_uses_opening_high_as_stop()
    {
        var custom = new Dictionary<int, (decimal, decimal, decimal, decimal)>
        {
            [0] = (100, 101, 97, 98),    // abwärts → Short, Stop = High 101
            [1] = (98, 98, 98, 98),
        };
        var res = Run(Session(Day1Open, 98m, custom));

        res.Trades.Should().ContainSingle();
        var t = res.Trades[0];
        t.Side.Should().Be(PositionSide.Short);
        t.EntryPrice.Should().Be(98m);
        t.StopLossPrice.Should().Be(101m);
        t.TakeProfitPrice.Should().Be(68m, "Short 10R: 98 − 10·3");
        t.ExitReason.Should().Be(OhlcExitReason.SessionClose);
    }

    [Fact]
    public void Doji_opening_candle_makes_no_trade()
    {
        var custom = new Dictionary<int, (decimal, decimal, decimal, decimal)> { [0] = (100, 101, 99, 100) };
        var res = Run(Session(Day1Open, 100m, custom));
        res.Trades.Should().BeEmpty("Close == Open der ersten Kerze → kein Trade");
        res.SignalsGenerated.Should().Be(0);
    }

    [Fact]
    public void One_trade_per_session_and_reset_next_session()
    {
        var d1 = Session(Day1Open, 102m, new Dictionary<int, (decimal, decimal, decimal, decimal)>
        {
            [0] = (100, 103, 99, 102), [1] = (102, 102, 102, 102),   // Long
        });
        var d2 = Session(Day2Open, 98m, new Dictionary<int, (decimal, decimal, decimal, decimal)>
        {
            [0] = (100, 101, 97, 98), [1] = (98, 98, 98, 98),        // Short
        });
        var res = Run(d1.Concat(d2).ToList());

        res.Trades.Should().HaveCount(2, "genau ein Trade je Session, Zustand wird zum nächsten 09:30 zurückgesetzt");
        res.Trades[0].Side.Should().Be(PositionSide.Long);
        res.Trades[1].Side.Should().Be(PositionSide.Short);
    }

    [Fact]
    public void R_is_measured_from_the_actual_fill_including_slippage()
    {
        // 1 Tick Slippage: Long-Fill = Open + 1 = 103 (nicht 102). Stop 99 → R=4, Ziel = 103 + 40 = 143.
        var custom = new Dictionary<int, (decimal, decimal, decimal, decimal)>
        {
            [0] = (100, 103, 99, 102), [1] = (102, 102, 102, 102),
        };
        var res = Run(Session(Day1Open, 102m, custom), fee: Fee(slipTicks: 1m));
        var t = res.Trades[0];
        t.EntryPrice.Should().Be(103m, "Fill am Open + 1 Tick Slippage");
        t.StopLossPrice.Should().Be(99m);
        t.TakeProfitPrice.Should().Be(143m, "R aus dem FILL (103−99=4), 10R = 40");
    }

    [Fact]
    public void Gap_through_the_stop_invalidates_the_entry_no_trade()
    {
        // Long-Signal (Stop 99), aber der Folgebar GAPt unter den Stop: Open 98 < 99 → ungültige Stop-Lage → kein Trade.
        var custom = new Dictionary<int, (decimal, decimal, decimal, decimal)>
        {
            [0] = (100, 103, 99, 102), [1] = (98, 98, 98, 98),
        };
        var res = Run(Session(Day1Open, 98m, custom));
        res.Trades.Should().BeEmpty();
        res.InvalidStopSkips.Should().Be(1, "Entry wegen ungültiger Stop-Lage nach Gap verworfen (kein erratener Fill)");
    }

    [Fact]
    public void Take_profit_at_10R_is_a_limit_fill_without_slippage()
    {
        var custom = new Dictionary<int, (decimal, decimal, decimal, decimal)>
        {
            [0] = (100, 103, 99, 102), [1] = (102, 102, 102, 102),
            [5] = (102, 135, 102, 120),   // High 135 ≥ TP 132 → Take-Profit
        };
        var res = Run(Session(Day1Open, 102m, custom));   // Slippage 0: Fill 102, R=3, TP=132
        // TP-Limit füllt exakt an 132 (keine adverse Slippage auf Limit).
        var t = res.Trades[0];
        t.ExitReason.Should().Be(OhlcExitReason.TakeProfit);
        t.ExitPrice.Should().Be(132m);
    }

    [Fact]
    public void Stop_loss_is_a_market_fill_with_slippage()
    {
        var custom = new Dictionary<int, (decimal, decimal, decimal, decimal)>
        {
            [0] = (100, 103, 99, 102), [1] = (102, 102, 102, 102),
            [5] = (102, 102, 98, 100),   // Low 98 ≤ SL 99 → Stop-Loss (Market + Slippage)
        };
        var res = Run(Session(Day1Open, 102m, custom), fee: Fee(slipTicks: 1m));
        var t = res.Trades[0];
        t.ExitReason.Should().Be(OhlcExitReason.StopLoss);
        t.ExitPrice.Should().Be(98m, "Stop 99 − 1 Tick adverse Slippage");
    }

    [Fact]
    public void Stop_price_is_rounded_to_tick_size()
    {
        // tickSize 0.25: Stop-Preis 99.80 → gerundet 99.75.
        var custom = new Dictionary<int, (decimal, decimal, decimal, decimal)>
        {
            [0] = (100m, 103m, 99.80m, 102m), [1] = (102m, 102m, 102m, 102m),
        };
        var res = Run(Session(Day1Open, 102m, custom), instr: Mes(tick: 0.25m, point: 5m, value: 1.25m));
        var t = res.Trades[0];
        t.StopLossPrice.Should().Be(99.75m, "auf Tick-Größe gerundet");
        // R = 102 − 99.75 = 2.25 → Ziel = 102 + 22.5 = 124.5 (tick-aligned)
        t.TakeProfitPrice.Should().Be(124.5m);
    }

    [Fact]
    public void Data_ending_before_session_close_is_an_incomplete_session()
    {
        // Daten enden vor 16:00 (letzter Bar 15:55 NY), Position offen → kein erfundener 16:00-Fill: Schluss am echten
        // letzten Bar (EndOfData) UND als unvollständige Session gekennzeichnet (Position trug Exposure über das
        // vorgesehene Session-Ende hinaus bzw. bis Datenende).
        var custom = new Dictionary<int, (decimal, decimal, decimal, decimal)>
        {
            [0] = (100, 103, 99, 102), [1] = (102, 102, 102, 102),
        };
        var res = Run(Session(Day1Open, 102m, custom, lastBar: SessionEndBar - 1));
        res.Trades.Should().ContainSingle();
        res.Trades[0].ExitReason.Should().Be(OhlcExitReason.EndOfData, "kein 16:00-Bar vorhanden → kein erfundener Session-Fill");
        res.IncompleteSessionCloses.Should().Be(1, "Datenende vor Session-Ende bei offener Position → unvollständig");
        res.Trades[0].Note.Should().Contain("unvollständig");
    }

    [Fact]
    public void Costs_are_applied_net_of_fees()
    {
        var custom = new Dictionary<int, (decimal, decimal, decimal, decimal)>
        {
            [0] = (100, 103, 99, 102), [1] = (102, 102, 102, 102),
        };
        var res = Run(Session(Day1Open, 102m, custom), fee: Fee(commission: 0.5m));
        var t = res.Trades[0];
        t.Fees.Should().Be(1.0m, "2 Seiten × 0,5 × 1 Kontrakt");
        t.NetPnL.Should().Be(t.GrossPnL - 1.0m);
    }

    // ---- Session-Randfälle ---------------------------------------------------------------------

    [Fact]
    public void Missing_entry_bar_discards_the_entry_and_does_not_fill_later()
    {
        // Opening-Bar vorhanden, aber die erwartete 09:35-Kerze (Index 1) FEHLT → Entry verwerfen, NICHT später füllen.
        var full = Session(Day1Open, 102m, new Dictionary<int, (decimal, decimal, decimal, decimal)>
        {
            [0] = (100, 103, 99, 102),
        });
        var bars = full.Where((_, idx) => idx != 1).ToList();   // 09:35-Bar entfernt; Folgedaten laufen weiter

        var res = Run(bars);
        res.Trades.Should().BeEmpty("fehlende Ausführungskerze → kein späterer/Folgetags-Fill");
        res.MissedEntrySkips.Should().Be(1);
    }

    [Fact]
    public void Missing_session_close_with_next_day_data_is_flagged_incomplete_not_carried_overnight()
    {
        // Tag 1 ohne 16:00-Schluss-Bar (Index 77 entfernt), Tag 2 vorhanden. Die Tag-1-Position darf NICHT still
        // über Nacht laufen: Exit am ersten Bar nach dem Session-Ende + Lauf als unvollständig gekennzeichnet.
        var d1 = Session(Day1Open, 102m, new Dictionary<int, (decimal, decimal, decimal, decimal)>
        {
            [0] = (100, 103, 99, 102), [1] = (102, 102, 102, 102),
        }, lastBar: SessionEndBar - 1);   // kein 16:00-Bar
        var d2 = Session(Day2Open, 102m, new Dictionary<int, (decimal, decimal, decimal, decimal)>
        {
            [0] = (100, 103, 99, 102), [1] = (102, 102, 102, 102),
        });

        var res = Run(d1.Concat(d2).ToList());

        res.IncompleteSessionCloses.Should().Be(1, "Tag-1-Session ohne Schluss-Bar bei offener Position");
        res.Message.Should().NotBeNullOrEmpty();
        var stale = res.Trades[0];
        stale.ExitReason.Should().Be(OhlcExitReason.SessionClose);
        stale.Note.Should().Contain("fehlte");
        stale.ExitTime.Should().Be(Day2Open, "Exit am ersten Bar nach dem Session-Ende (nicht erfunden, nicht rückwirkend)");
        res.Trades.Should().HaveCount(2, "Tag 2 handelt eigenständig (kein Übernacht-Übertrag in die neue Session)");
    }

    [Fact]
    public void Full_holiday_session_is_excluded_before_execution()
    {
        // Karfreitag 2026-04-03 (voller Feiertag): aufwärts gerichtete Opening-Kerze, aber KEIN Trade.
        var goodFriday = new DateTimeOffset(2026, 4, 3, 13, 30, 0, TimeSpan.Zero);   // NY 09:30 EDT
        var strat = new OpeningRangeBreakoutStrategy();
        var bars = Session(goodFriday, 102m, new Dictionary<int, (decimal, decimal, decimal, decimal)>
        {
            [0] = (100, 103, 99, 102), [1] = (102, 102, 102, 102),
        });
        var res = new OhlcBacktestEngine().Run(bars, strat, Mes(), Fee(),
            new OhlcBacktestConfig { Quantity = 1, StopLossTicks = 40, TakeProfitTicks = 60, ExcludePartialEdges = false }, "test", 5);

        res.Trades.Should().BeEmpty("voller Feiertag → Session ausgeschlossen");
        strat.ExcludedSessions.Should().Contain(new DateOnly(2026, 4, 3));
    }

    [Fact]
    public void Early_close_day_uses_the_13_00_session_end()
    {
        // 2026-11-27 (Tag nach Thanksgiving, Early Close 13:00 ET, EST=UTC-5): NY 09:30 = 14:30 UTC, Ende 13:00 = 18:00 UTC.
        var start = new DateTimeOffset(2026, 11, 27, 14, 30, 0, TimeSpan.Zero);
        var bars = Session(start, 102m, new Dictionary<int, (decimal, decimal, decimal, decimal)>
        {
            [0] = (100, 103, 99, 102), [1] = (102, 102, 102, 102),
        }, lastBar: 45);   // bis ~18:45 UTC
        var res = Run(bars);

        res.Trades.Should().ContainSingle();
        var t = res.Trades[0];
        t.ExitReason.Should().Be(OhlcExitReason.SessionClose);
        // Schluss am Bar, der um 18:00 UTC (= NY 13:00) schließt: Index 41 (Open 17:55, Close 18:00).
        t.ExitTime.Should().Be(new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero), "Early-Close-Ende 13:00 ET, nicht 16:00");
        t.ExitBarIndex.Should().Be(41);
    }

    [Fact]
    public void Last_bar_stop_loss_is_not_double_closed_by_session_close()
    {
        // Auf dem Session-Schluss-Bar wird zuerst SL/TP geprüft; ein dort getroffener Stop schließt die Position,
        // der anschließende SessionClose darf sie NICHT ein zweites Mal schließen.
        var custom = new Dictionary<int, (decimal, decimal, decimal, decimal)>
        {
            [0] = (100, 103, 99, 102), [1] = (102, 102, 102, 102),
            [SessionEndBar] = (102, 102, 98, 100),   // Low 98 ≤ SL 99 am Schluss-Bar → Stop-Loss
        };
        var res = Run(Session(Day1Open, 102m, custom), fee: Fee(slipTicks: 1m));
        res.Trades.Should().ContainSingle("genau ein Exit, kein Doppelschluss");
        res.Trades[0].ExitReason.Should().Be(OhlcExitReason.StopLoss);
        res.Trades[0].ExitBarIndex.Should().Be(SessionEndBar);
    }

    [Fact]
    public void Session_close_applies_market_slippage_and_fees()
    {
        var custom = new Dictionary<int, (decimal, decimal, decimal, decimal)>
        {
            [0] = (100, 103, 99, 102), [1] = (102, 102, 102, 102),
        };
        var res = Run(Session(Day1Open, 102m, custom), fee: Fee(commission: 0.5m, slipTicks: 1m));
        var t = res.Trades[0];
        t.ExitReason.Should().Be(OhlcExitReason.SessionClose);
        t.ExitPrice.Should().Be(101m, "Long-Session-Exit am Close 102 − 1 Tick Market-Slippage");
        t.Fees.Should().Be(1.0m, "Gebühren auch beim Session-Exit");
    }
}
