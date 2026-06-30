using FluentAssertions;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.MarketData;
using Xunit;

namespace TradingBot.Tests.MarketData;

public class CsvTickReaderTests
{
    private static IReadOnlyList<Domain.Models.MarketTick> ReadCsv(string content, bool chrono = true)
        => CsvTickReader.Read(new StringReader(content), chrono);

    [Fact]
    public void Reads_valid_minimal_csv()
    {
        var csv = """
        timestamp,symbol,price,volume
        2026-06-23T13:30:00Z,NQ,20000.00,3
        2026-06-23T13:30:00.500Z,NQ,20000.25,1
        """;

        var ticks = ReadCsv(csv);

        ticks.Should().HaveCount(2);
        ticks[0].Symbol.Should().Be("NQ");
        ticks[0].Price.Should().Be(20000.00m);
        ticks[0].Volume.Should().Be(3m);
        ticks[0].Aggressor.Should().Be(AggressorSide.Unknown); // minimal -> kein Orderflow
    }

    [Fact]
    public void Missing_file_throws_FileNotFound()
    {
        var act = () => CsvTickReader.ReadFile(@"A:\does\not\exist\ticks.csv");
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Missing_required_column_throws()
    {
        var csv = "timestamp,symbol,price\n2026-06-23T13:30:00Z,NQ,20000.00"; // volume fehlt
        var act = () => ReadCsv(csv);
        act.Should().Throw<CsvMarketDataException>().WithMessage("*volume*");
    }

    [Fact]
    public void Invalid_price_throws()
    {
        var csv = "timestamp,symbol,price,volume\n2026-06-23T13:30:00Z,NQ,0,3";
        var act = () => ReadCsv(csv);
        act.Should().Throw<CsvMarketDataException>().WithMessage("*Preis*");
    }

    [Fact]
    public void Negative_volume_throws()
    {
        var csv = "timestamp,symbol,price,volume\n2026-06-23T13:30:00Z,NQ,20000,-1";
        var act = () => ReadCsv(csv);
        act.Should().Throw<CsvMarketDataException>().WithMessage("*Volumen*");
    }

    [Fact]
    public void Non_numeric_price_throws()
    {
        var csv = "timestamp,symbol,price,volume\n2026-06-23T13:30:00Z,NQ,abc,3";
        var act = () => ReadCsv(csv);
        act.Should().Throw<CsvMarketDataException>();
    }

    [Fact]
    public void Non_chronological_throws_when_validation_on()
    {
        var csv = """
        timestamp,symbol,price,volume
        2026-06-23T13:30:01Z,NQ,20000.00,1
        2026-06-23T13:30:00Z,NQ,20000.25,1
        """;
        var act = () => ReadCsv(csv, chrono: true);
        act.Should().Throw<CsvMarketDataException>().WithMessage("*chronologisch*");
    }

    [Fact]
    public void Only_header_throws()
    {
        var act = () => ReadCsv("timestamp,symbol,price,volume");
        act.Should().Throw<CsvMarketDataException>().WithMessage("*Keine Datenzeilen*");
    }

    [Fact]
    public void Orderflow_csv_sets_aggressor_from_tradedirection()
    {
        var csv = """
        timestamp,symbol,price,volume,bid,ask,tradedirection
        2026-06-23T13:30:00Z,NQ,20000.25,2,20000.00,20000.25,buy
        2026-06-23T13:30:00.500Z,NQ,20000.00,3,20000.00,20000.25,sell
        """;

        var ticks = ReadCsv(csv);

        ticks[0].Aggressor.Should().Be(AggressorSide.Buy);
        ticks[0].HasOrderFlow.Should().BeTrue();
        ticks[1].Aggressor.Should().Be(AggressorSide.Sell);
    }

    [Fact]
    public void Aggressor_inferred_from_bid_ask_volume_when_no_direction()
    {
        var csv = """
        timestamp,symbol,price,volume,bidvolume,askvolume
        2026-06-23T13:30:00Z,NQ,20000.25,2,0,2
        2026-06-23T13:30:00.500Z,NQ,20000.00,3,3,0
        """;

        var ticks = ReadCsv(csv);

        ticks[0].Aggressor.Should().Be(AggressorSide.Buy);  // askvolume > 0
        ticks[1].Aggressor.Should().Be(AggressorSide.Sell); // bidvolume > 0
    }

    [Fact]
    public void Skips_blank_and_comment_lines()
    {
        var csv = """
        # Kommentar
        timestamp,symbol,price,volume

        2026-06-23T13:30:00Z,NQ,20000.00,3
        """;

        var ticks = ReadCsv(csv);
        ticks.Should().HaveCount(1);
    }
}
