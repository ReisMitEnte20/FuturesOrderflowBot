using FluentAssertions;
using TradingBot.Application.Strategies.OrderFlow;
using TradingBot.Domain.Models;
using TradingBot.Domain.Enums;
using TradingBot.Research;
using TradingBot.Research.Orderflow;
using TradingBot.Tests.Backtesting;
using Xunit;

namespace TradingBot.Tests.Orderflow;

/// <summary>
/// Belegt den Orderflow-Mehrstrategielauf ueber den bestehenden Tick-Backtester mit den GEGATETEN Kandidaten
/// (max. 1 Kontrakt, kein Aufstocken): getrennte Ledgers, Reproduzierbarkeit, 500-Tick-Parametrisierung,
/// kein rueckwirkender Fill, vollstaendiges Endergebnis (Equity-Abstimmung) und Sichtbarkeit offener Positionen.
/// </summary>
public class OrderflowMultiStrategyRunnerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 23, 13, 30, 0, TimeSpan.Zero);
    private const decimal FeePerSide = 0.85m + 1.18m + 0.10m + 0.02m; // = 2.15 (aus BacktestTestData.Fee())

    // 500 klassifizierte Ticks je Bar: "buy"=steigend/Ask-dominiert (Delta +500, Long), "sell"=fallend/Bid (Short),
    // "neutral"=flach/ausgeglichen (Delta 0, Flat/Doji).
    private static List<MarketTick> BuildBars(string[] types, int perBar = 500, decimal basePrice = 20000m)
    {
        var ticks = new List<MarketTick>(types.Length * perBar);
        int idx = 0; decimal price = basePrice;
        foreach (var type in types)
        {
            for (int i = 0; i < perBar; i++)
            {
                decimal p = type switch { "buy" => price + i * 0.25m, "sell" => price - i * 0.25m, _ => price };
                var aggr = type switch { "buy" => AggressorSide.Buy, "sell" => AggressorSide.Sell, _ => (i % 2 == 0 ? AggressorSide.Buy : AggressorSide.Sell) };
                ticks.Add(new MarketTick { Symbol = "NQ", Timestamp = T0.AddMilliseconds(idx++), Price = p, Volume = 1m, Aggressor = aggr });
            }
            price = type == "buy" ? price + perBar * 0.25m : type == "sell" ? price - perBar * 0.25m : price;
        }
        return ticks;
    }

    private static readonly string[] Pattern = { "buy", "sell", "buy", "neutral", "sell", "buy" }; // 6 volle Bars

    private static OrderflowRunRequest Request(IReadOnlyList<MarketTick> ticks, IReadOnlyList<StrategyCandidate> cands, int tpb = 500) => new()
    {
        Ticks = ticks, Symbol = "NQ",
        Instrument = BacktestTestData.Instrument(), Fee = BacktestTestData.Fee(), Broker = BacktestTestData.Broker(),
        Risk = BacktestTestData.Risk(), Account = BacktestTestData.Account(),
        TicksPerBar = tpb, Candidates = cands, DataSha256 = "testsha", DataSource = "synthetic", CapabilitiesSufficient = true,
        DeltaThreshold = DeltaFlowStrategy.DefaultDeltaThreshold
    };

    private static IReadOnlyList<StrategyCandidate> Gated() => OrderflowProofCandidates.BuildGated("NQ");

    [Fact]
    public async Task TicksPerBar_500_controls_full_bar_aggregation()
    {
        var r = await new OrderflowMultiStrategyRunner().RunAsync(Request(BuildBars(Pattern), new[] { Gated()[0] }));
        r.FullBars.Should().Be(6);
        r.RemainderTicks.Should().Be(0);
        r.Workspace.Candles.Should().HaveCount(6);
        r.Workspace.Footprint.Should().HaveCount(6);
        r.Workspace.Meta.TicksPerBar.Should().Be(500);
    }

    [Fact]
    public async Task Three_strategies_have_separate_ledgers_solo_equals_combined()
    {
        var ticks = BuildBars(Pattern);
        var combined = await new OrderflowMultiStrategyRunner().RunAsync(Request(ticks, Gated()));
        var solo = await new OrderflowMultiStrategyRunner().RunAsync(Request(ticks, new[] { Gated()[0] }));

        combined.Workspace.Strategies.Select(s => s.Name).Should().Equal("Momentum(Price)", "DeltaFlow(OF)", "Fade(Price)");
        var mCombined = combined.Workspace.Strategies.Single(s => s.Name == "Momentum(Price)");
        var mSolo = solo.Workspace.Strategies.Single();
        mSolo.RealizedNetPnL.Should().Be(mCombined.RealizedNetPnL);
        mSolo.Trades.Count.Should().Be(mCombined.Trades.Count);
    }

    [Fact]
    public async Task Repeated_run_is_identical()
    {
        var ticks = BuildBars(Pattern);
        var a = await new OrderflowMultiStrategyRunner().RunAsync(Request(ticks, Gated()));
        var b = await new OrderflowMultiStrategyRunner().RunAsync(Request(ticks, Gated()));
        for (int i = 0; i < a.Workspace.Strategies.Count; i++)
        {
            var sa = a.Workspace.Strategies[i]; var sb = b.Workspace.Strategies[i];
            sb.Name.Should().Be(sa.Name);
            sb.RealizedNetPnL.Should().Be(sa.RealizedNetPnL);
            sb.EndEquityMarkToMarket.Should().Be(sa.EndEquityMarkToMarket);
            sb.Trades.Select(t => (t.Side, t.EntryPrice, t.ExitPrice, t.NetPnL, t.EntryBarIndex, t.ExitBarIndex))
              .Should().Equal(sa.Trades.Select(t => (t.Side, t.EntryPrice, t.ExitPrice, t.NetPnL, t.EntryBarIndex, t.ExitBarIndex)));
        }
    }

    [Fact]
    public async Task Gate_keeps_at_most_one_contract_and_never_adds()
    {
        var r = await new OrderflowMultiStrategyRunner().RunAsync(Request(BuildBars(Pattern), Gated()));
        foreach (var s in r.Workspace.Strategies)
        {
            s.OpenPositionQty.Should().BeLessThanOrEqualTo(1, "hoechstens ein Kontrakt");
            s.OrdersRejectedByRisk.Should().Be(0, "kein Add soll ueberhaupt als Order entstehen (keine Ablehnungs-Pollution)");
            s.OrdersSubmitted.Should().Be(s.SignalsGenerated, "jedes durchgereichte Signal wird eine Order (keine unterdrueckten Adds als Order)");
        }
    }

    [Fact]
    public async Task DeltaFlow_produces_trades_and_completes()
    {
        var r = await new OrderflowMultiStrategyRunner().RunAsync(Request(BuildBars(Pattern), new[] { Gated()[1] }));
        var df = r.Workspace.Strategies.Single();
        df.Name.Should().Be("DeltaFlow(OF)");
        df.Status.Should().Be("Completed");
        df.Trades.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task No_retroactive_fill_entries_never_on_the_signal_bar()
    {
        var r = await new OrderflowMultiStrategyRunner().RunAsync(Request(BuildBars(Pattern), Gated()));
        foreach (var s in r.Workspace.Strategies)
            foreach (var t in s.Trades)
            {
                t.EntryBarIndex.Should().BeGreaterThanOrEqualTo(1);
                t.ExitBarIndex.Should().BeGreaterThanOrEqualTo(t.EntryBarIndex);
                t.EntryTimeMs.Should().BeGreaterThanOrEqualTo(r.Workspace.Meta.MarketStartMs);
            }
    }

    [Fact]
    public async Task End_result_equity_reconciles_realized_unrealized_and_entry_fees()
    {
        var r = await new OrderflowMultiStrategyRunner().RunAsync(Request(BuildBars(Pattern), Gated()));
        decimal startBal = BacktestTestData.Account().StartingBalance;

        foreach (var s in r.Workspace.Strategies)
        {
            s.NetPnL.Should().Be(s.RealizedNetPnL);
            s.Fees.Should().Be(s.RealizedFees);
            s.EndEquityRealized.Should().Be(startBal + s.RealizedNetPnL);
            s.TotalFeesPaid.Should().Be(s.RealizedFees + s.OpenPositionEntryFees);
            // Kern-Abstimmung: Mark-to-Market = realisiert + unrealisiert(letzter Tick) - gezahlte Eintritts-Fees.
            s.EndEquityMarkToMarket.Should().Be(s.EndEquityRealized + s.UnrealizedGrossAtLastTick - s.OpenPositionEntryFees);

            if (s.OpenPositionSide == "Flat")
            {
                s.UnrealizedGrossAtLastTick.Should().Be(0m, "ohne offene Position keine unrealisierte PnL");
                s.OpenPositionEntryFees.Should().Be(0m, "keine Eintritts-Fee ohne offene Position");
                s.EndEquityMarkToMarket.Should().Be(s.EndEquityRealized);
            }
            else
            {
                // NUR Eintritts-Fee der offenen Position (eine Seite), KEINE hypothetische Exit-Fee.
                s.OpenPositionEntryFees.Should().Be(FeePerSide * s.OpenPositionQty);
            }
        }
    }
}
