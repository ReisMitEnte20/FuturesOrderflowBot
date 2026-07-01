using FluentAssertions;
using TradingBot.Backtesting;
using TradingBot.Domain.Enums;
using Xunit;

namespace TradingBot.Tests.Backtesting;

public class BacktestStatisticsCalculatorTests
{
    private static readonly DateTimeOffset Day = new(2026, 6, 23, 14, 0, 0, TimeSpan.Zero);

    private static BacktestTrade Trade(decimal net, decimal fees = 0m, DateTimeOffset? exit = null) => new()
    {
        Symbol = "NQ", Side = PositionSide.Long, Quantity = 1,
        EntryTime = Day, ExitTime = exit ?? Day, EntryPrice = 20000m, ExitPrice = 20000m,
        GrossPnL = net + fees, Fees = fees, NetPnL = net
    };

    [Fact]
    public void Empty_trades_yield_empty_stats_with_slippage()
    {
        var s = BacktestStatisticsCalculator.Compute(Array.Empty<BacktestTrade>(), totalSlippage: 5m);
        s.TotalTrades.Should().Be(0);
        s.TotalSlippage.Should().Be(5m);
        s.ProfitFactor.Should().BeNull();
    }

    [Fact]
    public void Computes_core_metrics()
    {
        var trades = new[] { Trade(100m), Trade(-50m), Trade(200m), Trade(-30m), Trade(-20m) };

        var s = BacktestStatisticsCalculator.Compute(trades, totalSlippage: 0m);

        s.TotalTrades.Should().Be(5);
        s.WinningTrades.Should().Be(2);
        s.LosingTrades.Should().Be(3);
        s.BreakEvenTrades.Should().Be(0);
        s.NetProfit.Should().Be(200m);
        s.GrossProfit.Should().Be(200m); // fees 0
        s.WinRate.Should().Be(0.4m);
        s.ProfitFactor.Should().Be(3m);             // 300 / 100
        s.AverageWinner.Should().Be(150m);
        s.LargestWin.Should().Be(200m);
        s.LargestLoss.Should().Be(-50m);
        s.Expectancy.Should().Be(40m);              // 200 / 5
        s.MaxWinningStreak.Should().Be(1);
        s.MaxLosingStreak.Should().Be(2);           // -30, -20
        s.MaxDrawdown.Should().Be(50m);             // Peak 250 -> 200
        s.TradesPerDay.Should().Be(5m);             // alle am selben Tag
    }

    [Fact]
    public void NetProfit_equals_GrossProfit_minus_TotalFees()
    {
        var trades = new[] { Trade(100m, fees: 4.30m), Trade(-50m, fees: 4.30m) };
        var s = BacktestStatisticsCalculator.Compute(trades, 0m);

        s.TotalFees.Should().Be(8.60m);
        s.NetProfit.Should().Be(50m);
        s.GrossProfit.Should().Be(58.60m);
        (s.GrossProfit - s.TotalFees).Should().Be(s.NetProfit);
    }

    [Fact]
    public void No_losers_gives_null_profit_factor()
    {
        var s = BacktestStatisticsCalculator.Compute(new[] { Trade(100m), Trade(50m) }, 0m);
        s.ProfitFactor.Should().BeNull();
        s.WinRate.Should().Be(1m);
    }

    [Fact]
    public void Break_even_trade_resets_streaks_and_is_counted()
    {
        var trades = new[] { Trade(100m), Trade(0m), Trade(-10m), Trade(-10m) };
        var s = BacktestStatisticsCalculator.Compute(trades, 0m);

        s.BreakEvenTrades.Should().Be(1);
        s.MaxWinningStreak.Should().Be(1);
        s.MaxLosingStreak.Should().Be(2);
    }

    [Fact]
    public void Drawdown_counts_initial_losing_sequence()
    {
        // Verluste zuerst: Peak bleibt 0, Drawdown wächst.
        var trades = new[] { Trade(-100m), Trade(-50m), Trade(80m) };
        var s = BacktestStatisticsCalculator.Compute(trades, 0m);
        s.MaxDrawdown.Should().Be(150m); // 0 -> -150
    }

    [Fact]
    public void TradesPerDay_spans_distinct_days()
    {
        var trades = new[]
        {
            Trade(10m, exit: Day),
            Trade(10m, exit: Day.AddDays(1)),
            Trade(10m, exit: Day.AddDays(1))
        };
        var s = BacktestStatisticsCalculator.Compute(trades, 0m);
        s.TradesPerDay.Should().Be(1.5m); // 3 Trades / 2 Tage
    }
}
