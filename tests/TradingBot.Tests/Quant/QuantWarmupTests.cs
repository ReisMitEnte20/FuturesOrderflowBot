using FluentAssertions;
using TradingBot.Backtesting.Ohlc;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Quant.Validation;
using Xunit;

namespace TradingBot.Tests.Quant;

/// <summary>
/// Befund 3: Nachweis, dass die konfigurierte Warmup-Einstellung TATSÄCHLICH die Ausführung sperrt —
/// nicht bloß die interne Anlaufzeit eines Indikators. Geprüft mit einer Strategie, die von der ersten
/// Kerze an signalisiert.
/// </summary>
public class QuantWarmupTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 14, 0, 0, TimeSpan.Zero);

    private static Candle C(int min, decimal price) => new()
    {
        Symbol = "TEST",
        OpenTime = T0.AddMinutes(min),
        CloseTime = T0.AddMinutes(min + 1),
        Open = price, High = price + 1m, Low = price - 1m, Close = price, Volume = 0m
    };

    private static InstrumentProfile Instr() => new()
    {
        Symbol = "TEST", BrokerSymbol = "TEST", Exchange = "X", Currency = "USD",
        TickSize = 1m, TickValue = 1m, PointValue = 1m, ContractMultiplier = 1m,
        MaxContracts = 10, DefaultStopLossTicks = 1000, DefaultTakeProfitTicks = 1000
    };

    private static FeeProfile Fee() => new() { BrokerName = "T", ExecutionProvider = "T", Instrument = "TEST" };

    /// <summary>Signalisiert von der ERSTEN Kerze an durchgehend Long (früh signalisierende Teststrategie).</summary>
    private sealed class AlwaysLongStrategy : IStrategy
    {
        public int Seen { get; private set; }
        public string Name => "AlwaysLong";
        public TradeSignal? OnCandle(Candle c)
        {
            Seen++;
            return new TradeSignal
            {
                StrategyName = Name, Symbol = c.Symbol, Direction = SignalDirection.Long,
                Timestamp = c.CloseTime, ReferencePrice = c.Close
            };
        }
        public void Reset() => Seen = 0;
    }

    [Fact]
    public void Warmup_guard_suppresses_signals_during_the_warmup_and_forwards_them_afterwards()
    {
        var inner = new AlwaysLongStrategy();
        var guarded = new WarmupGuardStrategy(inner, warmupBars: 3);

        var results = Enumerable.Range(0, 6).Select(i => guarded.OnCandle(C(i, 100 + i))).ToList();

        // Erste 3 Kerzen: kein Signal weitergegeben; danach schon.
        results[0].Should().BeNull();
        results[1].Should().BeNull();
        results[2].Should().BeNull();
        results[3].Should().NotBeNull();
        results[4].Should().NotBeNull();
        results[5].Should().NotBeNull();

        // Die innere Strategie hat trotzdem JEDE Kerze gesehen (Indikator-Warmup mit verfügbaren Daten).
        inner.Seen.Should().Be(6);
    }

    [Fact]
    public void Warmup_delays_the_actual_execution_in_the_engine()
    {
        // Steigende Reihe, keine SL/TP-Treffer.
        var bars = Enumerable.Range(0, 9).Select(i => C(i, 100 + i)).ToList();
        var cfg = new OhlcBacktestConfig { Quantity = 1, StopLossTicks = 1000, TakeProfitTicks = 1000 };
        var engine = new OhlcBacktestEngine();

        // Ohne Warmup: Signal aus Bar 0 → Ausführung am OPEN von Bar 1.
        var noWarmup = engine.Run(bars, new AlwaysLongStrategy(), Instr(), Fee(), cfg, "test", 1);
        noWarmup.Trades.Should().NotBeEmpty();
        noWarmup.Trades[0].EntryBarIndex.Should().Be(1);

        // Mit Warmup 3: Signale der Bars 0–2 werden gesperrt; erstes gültiges Signal aus Bar 3 → Entry Bar 4.
        var warmed = engine.Run(bars, new WarmupGuardStrategy(new AlwaysLongStrategy(), 3), Instr(), Fee(), cfg, "test", 1);
        warmed.Trades.Should().NotBeEmpty();
        warmed.Trades[0].EntryBarIndex.Should().Be(4);
    }
}
