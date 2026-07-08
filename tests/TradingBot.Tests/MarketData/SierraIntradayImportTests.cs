using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using TradingBot.DevDashboard.Services;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.MarketData;
using TradingBot.Infrastructure.MarketData.Import;
using Xunit;

namespace TradingBot.Tests.MarketData;

/// <summary>
/// Phase 12E-A (Sierra): sichert den Sierra-Intraday-Import — UTC-Timestamp aus Date+Time,
/// Last→Price, Bid/Ask-Volumen→Delta/CVD-Capability, und die EHRLICHE Unterscheidung
/// 1-Tick vs. aggregiert (keine falsche Tick-Garantie, kein Fake-Orderflow).
/// </summary>
public class SierraIntradayImportTests
{
    private static TextReader R(string csv) => new StringReader(csv);

    private static CsvImportProfile ExampleProfile()
    {
        var root = RepoLocator.FindRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("Repo-Wurzel nicht gefunden.");
        var json = File.ReadAllText(Path.Combine(root, "config", "import-profiles", "sierra-intraday.example.json"));
        return JsonSerializer.Deserialize<CsvImportProfile>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        })!;
    }

    // Sierra-1-Tick: NumberOfTrades == 1, High=Ask, Low=Bid, Last=Trade Price.
    private const string SingleTickCsv = """
        Date,Time,Open,High,Low,Last,Volume,NumberOfTrades,BidVolume,AskVolume
        2026-07-08,13:30:00.000,20000.00,20000.25,20000.00,20000.00,3,1,0,3
        2026-07-08,13:30:01.000,20000.25,20000.50,20000.25,20000.25,2,1,2,0
        """;

    [Fact]
    public void Sierra_header_is_recognized_and_rows_imported()
    {
        var result = new SierraIntradayCsvImporter(ExampleProfile()).Import(R(SingleTickCsv), "NQ");

        result.Ticks.Should().HaveCount(2);
        result.Quality.RowsRead.Should().Be(2);
        result.Quality.RowsAccepted.Should().Be(2);
        result.Symbol.Should().Be("NQ");
    }

    [Fact]
    public void Date_and_time_are_interpreted_as_utc_timestamp()
    {
        var result = new SierraIntradayCsvImporter(ExampleProfile()).Import(R(SingleTickCsv), "NQ");

        var t0 = result.Ticks[0].Timestamp;
        t0.Offset.Should().Be(TimeSpan.Zero);                                   // UTC
        t0.Should().Be(new DateTimeOffset(2026, 7, 8, 13, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Last_is_mapped_to_trade_price()
    {
        var result = new SierraIntradayCsvImporter(ExampleProfile()).Import(R(SingleTickCsv), "NQ");

        result.Ticks[0].Price.Should().Be(20000.00m); // Last, NICHT Open/High/Low
        result.Ticks[0].Volume.Should().Be(3m);
    }

    [Fact]
    public void High_low_are_ask_bid_only_in_single_tick_mode()
    {
        var result = new SierraIntradayCsvImporter(ExampleProfile()).Import(R(SingleTickCsv), "NQ");

        result.Quality.Issues.Should().Contain(i => i.Code == "SierraSingleTick");
        result.Ticks[0].Ask.Should().Be(20000.25m); // High = Ask
        result.Ticks[0].Bid.Should().Be(20000.00m); // Low  = Bid
    }

    [Fact]
    public void Bid_ask_volume_enables_delta_cvd_capability()
    {
        var result = new SierraIntradayCsvImporter(ExampleProfile()).Import(R(SingleTickCsv), "NQ");

        result.SourceType.Should().Be(MarketDataSourceType.AggressorTick);
        result.Capabilities.SupportsDeltaCvd.Should().BeTrue();
        result.Ticks[0].Aggressor.Should().Be(AggressorSide.Buy);  // AskVolume>0, BidVolume==0
        result.Ticks[1].Aggressor.Should().Be(AggressorSide.Sell); // BidVolume>0, AskVolume==0
    }

    [Fact]
    public void Aggregated_records_do_not_claim_tick_guarantee_and_do_not_fake_quotes()
    {
        // NumberOfTrades > 1 -> aggregiert. High/Low sind Bar-Extreme, NICHT Ask/Bid.
        var csv = """
            Date,Time,Open,High,Low,Last,Volume,NumberOfTrades,BidVolume,AskVolume
            2026-07-08,13:30:00.000,20000.00,20001.00,19999.00,20000.50,40,25,18,22
            2026-07-08,13:31:00.000,20000.50,20002.00,20000.00,20001.50,55,31,20,35
            """;

        var result = new SierraIntradayCsvImporter(ExampleProfile()).Import(R(csv), "NQ");

        result.Quality.Issues.Should().Contain(i => i.Code == "SierraAggregatedRecords");
        result.Quality.Issues.Should().NotContain(i => i.Code == "SierraSingleTick");
        // Keine falsche Quote-Interpretation von High/Low bei aggregierten Records.
        result.Ticks[0].Ask.Should().Be(0m);
        result.Ticks[0].Bid.Should().Be(0m);
    }

    [Fact]
    public void Missing_numberoftrades_column_means_no_tick_guarantee()
    {
        // Ohne NumberOfTrades kann keine Tick-Granularität garantiert werden.
        var csv = """
            Date,Time,Last,Volume,BidVolume,AskVolume
            2026-07-08,13:30:00.000,20000.00,3,0,3
            """;

        var result = new SierraIntradayCsvImporter(ExampleProfile()).Import(R(csv), "NQ");

        result.Quality.Issues.Should().Contain(i => i.Code == "SierraAggregatedRecords");
        result.Ticks[0].Ask.Should().Be(0m); // High/Low nicht vorhanden/kein Tick -> keine Quote
    }

    [Fact]
    public void Missing_required_column_reports_error()
    {
        var csv = """
            Date,Time,Last
            2026-07-08,13:30:00.000,20000.00
            """; // 'volume' fehlt

        var act = () => new SierraIntradayCsvImporter(ExampleProfile()).Import(R(csv), "NQ");

        act.Should().Throw<CsvMarketDataException>().WithMessage("*Volume*");
    }
}
