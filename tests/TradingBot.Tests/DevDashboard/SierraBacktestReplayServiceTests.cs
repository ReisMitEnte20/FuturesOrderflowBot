using FluentAssertions;
using TradingBot.DevDashboard.Services;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.MarketData.Import;
using Xunit;

namespace TradingBot.Tests.DevDashboard;

/// <summary>
/// Phase: Sierra local backtest → Replay. Prüft die read-only Pipeline (Bars → Demo-Regel → Trades →
/// ReplaySession) mit kleinen synthetischen Sierra-Ticks. LOCAL / SIMULATION ONLY, kein Fake-Orderflow.
/// </summary>
public class SierraBacktestReplayServiceTests
{
    private const string Header =
        "Date, Time, Open, High, Low, Last, Volume, NumberOfTrades, BidVolume, AskVolume";

    private static SierraMarketDataResult Load(string csv) =>
        new SierraMarketDataAdapter().Load(new StringReader(csv), "MES", TimeSpan.FromMinutes(1));

    // Bar 23:00: stark positives Delta (+70) & Close>Open -> Long-Entry; Bar 23:01: High >= TP -> Exit.
    private const string BullishHighDelta = Header + "\n" +
        "2025/12/28, 23:00:05, 100.00,100.00,100.00,100.00, 30, 1, 0, 30\n" +
        "2025/12/28, 23:00:40, 100.50,100.50,100.50,100.50, 40, 1, 0, 40\n" +
        "2025/12/28, 23:01:20, 106.00,106.00,106.00,106.00, 10, 1, 0, 10\n";

    [Fact]
    public void Demo_rule_creates_trade_from_bars()
    {
        var r = SierraBacktestReplayService.BuildFrom(Load(BullishHighDelta), "MES");

        r.Session.Bars.Should().HaveCount(2);
        r.Session.Trades.Should().ContainSingle();
        var t = r.Session.Trades[0];
        t.Side.Should().Be(PositionSide.Long);
        t.EntryIndex.Should().BeLessThan(t.ExitIndex);
        t.NetPnL.Should().Be(21m);            // (105.50-100.50)*5 - 4
        r.Wins.Should().Be(1);
        r.DeltaCvdAvailable.Should().BeTrue();
    }

    [Fact]
    public void Replay_session_equity_is_consistent()
    {
        var s = SierraBacktestReplayService.BuildFrom(Load(BullishHighDelta), "MES").Session;

        s.RealizedEquityByBar.Should().HaveCount(s.BarCount);
        s.RealizedEquityByBar[^1].Should().Be(s.TotalNetPnL);
        s.Symbol.Should().Contain("Sierra");
    }

    [Fact]
    public void Missing_bid_ask_produces_no_fake_orderflow_signals()
    {
        var csv = "Date, Time, Last, Volume\n" +
                  "2025/12/28, 23:00:05, 100.00, 30\n" +
                  "2025/12/28, 23:01:05, 100.50, 40\n";
        var r = SierraBacktestReplayService.BuildFrom(Load(csv), "MES");

        r.DeltaCvdAvailable.Should().BeFalse();
        r.Session.Trades.Should().BeEmpty();   // ohne echtes Delta keine Signale
    }

    [Fact]
    public void TryBuild_reads_local_file_streaming()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"sierra_probe_{Guid.NewGuid():N}.txt");
        File.WriteAllText(tmp, BullishHighDelta);
        try
        {
            var svc = new SierraBacktestReplayService(tmp);
            svc.LocalFileAvailable.Should().BeTrue();
            var r = svc.TryBuild();
            r.Should().NotBeNull(svc.LastError);
            r!.Session.Trades.Should().ContainSingle();
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Missing_local_file_sets_error()
    {
        var svc = new SierraBacktestReplayService(@"A:\nope\does-not-exist.txt");
        svc.LocalFileAvailable.Should().BeFalse();
        svc.TryBuild().Should().BeNull();
        svc.LastError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Dashboard_has_no_execution_reference()
    {
        var referenced = typeof(SierraBacktestReplayService).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!).ToList();
        referenced.Should().NotContain(n => n.Contains("TradingBot.Execution"));
    }
}
