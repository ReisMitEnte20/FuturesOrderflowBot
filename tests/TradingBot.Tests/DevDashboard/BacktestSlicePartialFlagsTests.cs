using FluentAssertions;
using TradingBot.Backtesting.Ohlc;
using TradingBot.Core.Interfaces;
using TradingBot.DevDashboard.Services;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.MarketData.Import;
using Xunit;

namespace TradingBot.Tests.DevDashboard;

/// <summary>
/// Befund 5: Die Teilkerzen-Flags (Leading/Trailing) gelten nur für die ECHTEN Ränder des
/// Gesamtdatensatzes. Bei einem innenliegenden Ausschnitt (Walk-forward-Fenster) dürfen vollständige
/// Randkerzen NICHT aufgrund der globalen Flags entfernt werden.
/// </summary>
public class BacktestSlicePartialFlagsTests
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

    private sealed class NoSignalStrategy : IStrategy
    {
        public string Name => "NoSignal";
        public TradeSignal? OnCandle(Candle c) => null;
        public void Reset() { }
    }

    private static BacktestApiService.RunContext Ctx(IReadOnlyList<Candle> candles, bool lead, bool trail) =>
        new(candles, Instr(), Fee(), lead, trail, "test", 1, Array.Empty<OhlcImportIssue>(), false, false);

    private static readonly BacktestApiService Service = new(AppContext.BaseDirectory);
    private static readonly OhlcBacktestConfig Config = new() { Quantity = 1, ExcludePartialEdges = true };

    [Fact]
    public void Fully_inner_slice_keeps_its_last_candle_although_global_trailing_is_partial()
    {
        var global = Enumerable.Range(0, 6).Select(i => C(i, 100 + i)).ToList();
        var ctx = Ctx(global, lead: false, trail: true);

        // Voller Lauf: die globale Schlusskerze ist unvollständig und wird ausgeschlossen.
        var full = Service.RunEngine(ctx, new NoSignalStrategy(), Config);
        full.Data.TrailingPartialExcluded.Should().BeTrue();
        full.Data.EvaluatedBars.Should().Be(5);

        // Innenliegender Ausschnitt (Bars 1..3): dessen letzte Kerze ist vollständig und bleibt erhalten.
        var innerSlice = global.Skip(1).Take(3).ToList();   // Indizes 1,2,3 — endet vor der globalen Schlusskerze
        var inner = Service.RunEngine(ctx, new NoSignalStrategy(), Config, candlesOverride: innerSlice);
        inner.Data.TrailingPartialExcluded.Should().BeFalse();
        inner.Data.EvaluatedBars.Should().Be(3);
    }

    [Fact]
    public void Slice_touching_the_global_end_still_honors_the_trailing_partial_flag()
    {
        var global = Enumerable.Range(0, 6).Select(i => C(i, 100 + i)).ToList();
        var ctx = Ctx(global, lead: false, trail: true);

        var tailSlice = global.Skip(3).Take(3).ToList();   // Indizes 3,4,5 — enthält die globale Schlusskerze
        var tail = Service.RunEngine(ctx, new NoSignalStrategy(), Config, candlesOverride: tailSlice);
        tail.Data.TrailingPartialExcluded.Should().BeTrue();
        tail.Data.EvaluatedBars.Should().Be(2);
    }

    [Fact]
    public void Inner_slice_keeps_its_first_candle_although_global_leading_is_partial()
    {
        var global = Enumerable.Range(0, 6).Select(i => C(i, 100 + i)).ToList();
        var ctx = Ctx(global, lead: true, trail: false);

        var innerSlice = global.Skip(2).Take(3).ToList();  // beginnt nach der globalen Anfangskerze
        var inner = Service.RunEngine(ctx, new NoSignalStrategy(), Config, candlesOverride: innerSlice);
        inner.Data.LeadingPartialExcluded.Should().BeFalse();
        inner.Data.EvaluatedBars.Should().Be(3);
    }
}
