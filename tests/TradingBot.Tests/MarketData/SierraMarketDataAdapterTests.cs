using System.Reflection;
using FluentAssertions;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.MarketData.Import;
using Xunit;

namespace TradingBot.Tests.MarketData;

/// <summary>
/// Sierra → Backtest/Research-Adapter: überführt streamend aggregierte OrderFlowBars in das
/// bestehende <see cref="ImportedMarketDataSet"/>-Modell. Nur kleine synthetische Eingaben.
/// Sichert Delta/CVD, Capabilities, kein Fake-Orderflow und keine Execution-/Broker-Referenz.
/// </summary>
public class SierraMarketDataAdapterTests
{
    private const string Header =
        "Date, Time, Open, High, Low, Last, Volume, NumberOfTrades, BidVolume, AskVolume";

    private static TextReader R(string csv) => new StringReader(csv);
    private static SierraMarketDataResult Load(string csv, bool footprint = true) =>
        new SierraMarketDataAdapter().Load(R(csv), "MES", TimeSpan.FromMinutes(1), buildFootprint: footprint);

    // Zwei Minuten-Buckets, 1-Tick (NumberOfTrades == 1), Bid/Ask je Trade auf einer Seite.
    private const string TwoMinutes = Header + "\n" +
        "2025/12/28, 23:00:05, 100.00, 100.25, 100.00, 100.00, 5, 1, 0, 5\n" +   // Buy, ask5
        "2025/12/28, 23:00:30, 100.25, 100.50, 100.25, 100.25, 3, 1, 3, 0\n" +   // Sell, bid3
        "2025/12/28, 23:01:10, 100.25, 100.50, 100.00, 100.25, 4, 1, 4, 0\n";    // Sell, bid4

    [Fact]
    public void Produces_orderflow_bars_dataset_in_domain_model()
    {
        var r = Load(TwoMinutes);

        r.Dataset.SourceType.Should().Be(MarketDataSourceType.OrderFlowBars);
        r.Dataset.Symbol.Should().Be("MES");
        r.Dataset.OrderFlowBars.Should().HaveCount(2);
        r.Dataset.Ticks.Should().BeEmpty();
        r.Dataset.Quality.RowsRead.Should().Be(3);
        r.Dataset.Quality.RowsAccepted.Should().Be(3);
    }

    [Fact]
    public void Bars_carry_ohlc_bidask_delta_and_cvd()
    {
        var b = Load(TwoMinutes).Dataset.OrderFlowBars;

        var b0 = b[0];
        b0.OpenTime.Should().Be(new DateTimeOffset(2025, 12, 28, 23, 0, 0, TimeSpan.Zero));
        b0.Open.Should().Be(100.00m);
        b0.High.Should().Be(100.25m);        // OHLC aus dem Last-Trade-Preis (nicht der Sierra-High/Ask-Spalte)
        b0.Low.Should().Be(100.00m);
        b0.TotalVolume.Should().Be(8m);      // 5 + 3
        b0.AskVolume.Should().Be(5m);
        b0.BidVolume.Should().Be(3m);
        b0.Delta.Should().Be(2m);            // Ask - Bid
        b0.CumulativeDelta.Should().Be(2m);
        b[1].CumulativeDelta.Should().Be(-2m); // 2 + (0 - 4)
    }

    [Fact]
    public void Capabilities_reflect_real_orderflow_and_no_dom()
    {
        var caps = Load(TwoMinutes).Dataset.Capabilities;

        caps.SupportsDeltaCvd.Should().BeTrue();
        caps.SupportsAbsorption.Should().BeTrue();
        caps.SupportsBarImbalance.Should().BeTrue();
        caps.SupportsStackedImbalances.Should().BeTrue();  // Footprint echt aus Ticks aggregiert
        caps.SupportsHvnLvn.Should().BeFalse();
    }

    [Fact]
    public void Footprint_price_levels_available_via_aggregation()
    {
        var agg = Load(TwoMinutes).Aggregation;

        agg.Bars[0].PriceLevels.Should().NotBeEmpty();
        agg.Bars[0].PriceLevels.Single(l => l.Price == 100.00m).AskVolume.Should().Be(5m);
        agg.NetDelta.Should().Be(agg.SumAskVolume - agg.SumBidVolume);
    }

    [Fact]
    public void Missing_bid_ask_produces_no_fake_orderflow_capabilities()
    {
        var csv = "Date, Time, Last, Volume\n" +
                  "2025/12/28, 23:00:05, 100.00, 5\n";
        var r = new SierraMarketDataAdapter().Load(R(csv), "MES", TimeSpan.FromMinutes(1));

        r.Dataset.Capabilities.Should().Be(OrderFlowCapabilities.None);   // nichts erfunden
        r.Dataset.OrderFlowBars.Should().HaveCount(1);
    }

    [Fact]
    public void Adapter_assembly_has_no_execution_or_broker_reference()
    {
        var referenced = typeof(SierraMarketDataAdapter).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!).ToList();

        referenced.Should().NotContain(n => n.Contains("TradingBot.Execution"));
        referenced.Should().NotContain(n => n.Contains("TradingBot.DevDashboard"));
        referenced.Should().NotContain(n =>
            n.Contains("Rithmic", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("CQG", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Tradovate", StringComparison.OrdinalIgnoreCase));
    }
}
