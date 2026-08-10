using FluentAssertions;
using TradingBot.DevDashboard.Services;
using TradingBot.Domain.Enums;
using Xunit;

namespace TradingBot.Tests.DevDashboard;

/// <summary>
/// Phase 12F: Backtest Replay Visualizer. Sichert die deterministische Replay-Demo (Bars + Trades +
/// Equity) und die Safety-Grenzen (kein Execution-/Broker-Bezug). RESEARCH / SIMULATION ONLY.
/// </summary>
public class ReplayDemoServiceTests
{
    private static ReplaySession Session() => new ReplayDemoService().GetSession();

    [Fact]
    public void Session_has_bars_and_trades_and_equity()
    {
        var s = Session();

        s.Bars.Should().NotBeEmpty();
        s.Trades.Should().NotBeEmpty();
        s.RealizedEquityByBar.Should().HaveCount(s.BarCount);
        s.Bars.Select(b => b.Index).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Trades_have_valid_entry_before_exit_and_markers()
    {
        var s = Session();

        foreach (var t in s.Trades)
        {
            t.EntryIndex.Should().BeLessThan(t.ExitIndex);
            t.ExitIndex.Should().BeLessThan(s.BarCount);
            t.Side.Should().BeOneOf(PositionSide.Long, PositionSide.Short);
            t.EntryPrice.Should().BeGreaterThan(0m);
        }
        // Beide Richtungen kommen vor (Long- und Short-Marker sichtbar).
        s.Trades.Should().Contain(t => t.Side == PositionSide.Long);
        s.Trades.Should().Contain(t => t.Side == PositionSide.Short);
    }

    [Fact]
    public void Open_and_closed_state_transitions_are_correct()
    {
        var t = Session().Trades[0];

        t.IsOpenAt(t.EntryIndex).Should().BeTrue();
        t.IsOpenAt(t.ExitIndex - 1).Should().BeTrue();
        t.IsOpenAt(t.ExitIndex).Should().BeFalse();   // am Exit nicht mehr offen
        t.IsClosedAt(t.ExitIndex).Should().BeTrue();
        t.IsClosedAt(t.EntryIndex).Should().BeFalse();
    }

    [Fact]
    public void Realized_equity_is_cumulative_and_matches_total()
    {
        var s = Session();

        s.RealizedEquityByBar[0].Should().Be(0m);                 // vor dem ersten Exit
        s.RealizedEquityByBar[^1].Should().Be(s.TotalNetPnL);     // am Ende alle geschlossen
        s.TotalNetPnL.Should().Be(s.Trades.Sum(t => t.NetPnL));
    }

    [Fact]
    public void Demo_is_deterministic_across_instances()
    {
        var a = new ReplayDemoService().GetSession();
        var b = new ReplayDemoService().GetSession();

        a.Bars.Select(x => x.Close).Should().Equal(b.Bars.Select(x => x.Close));
        a.Trades.Select(x => x.NetPnL).Should().Equal(b.Trades.Select(x => x.NetPnL));
        a.TotalNetPnL.Should().Be(b.TotalNetPnL);
    }

    [Fact]
    public void GetSession_is_cached_within_instance()
    {
        var svc = new ReplayDemoService();
        ReferenceEquals(svc.GetSession(), svc.GetSession()).Should().BeTrue();
    }

    [Fact]
    public void Dashboard_has_no_execution_or_broker_reference()
    {
        var referenced = typeof(ReplayDemoService).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!).ToList();

        referenced.Should().NotContain(n => n.Contains("TradingBot.Execution"));
        referenced.Should().NotContain(n =>
            n.Contains("Rithmic", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("CQG", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Tradovate", StringComparison.OrdinalIgnoreCase));
    }
}
