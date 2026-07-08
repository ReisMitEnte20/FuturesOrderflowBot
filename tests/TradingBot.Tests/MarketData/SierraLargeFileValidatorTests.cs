using FluentAssertions;
using TradingBot.Infrastructure.MarketData;
using TradingBot.Infrastructure.MarketData.Import;
using Xunit;

namespace TradingBot.Tests.MarketData;

/// <summary>
/// Phase 12E-D: Streaming-Validator für große Sierra-Dateien. Nur KLEINE synthetische Eingaben
/// (kein echter GB-Test). Prüft Streaming, Header/Pflichtspalten, UTC-Timestamp, Delta/CVD,
/// MaxRows, Parse-Fehler-Robustheit und dass keine Fake-Capabilities entstehen.
/// </summary>
public class SierraLargeFileValidatorTests
{
    private const string Header =
        "Date, Time, Open, High, Low, Last, Volume, NumberOfTrades, BidVolume, AskVolume";

    private static TextReader R(string csv) => new StringReader(csv);
    private static SierraValidationReport Validate(string csv, long? maxRows = null, Action<long>? p = null)
        => new SierraLargeFileValidator().Validate(R(csv), maxRows, p);

    private const string TwoSingleTicks = Header + "\n" +
        "2025/12/28, 23:00:00, 7017.50, 7018.00, 7017.00, 7017.50, 3, 1, 0, 3\n" +
        "2025/12/28, 23:00:01.500, 7017.75, 7018.00, 7017.50, 7017.75, 2, 1, 2, 0\n";

    [Fact]
    public void Streams_header_and_aggregates_rows()
    {
        var r = Validate(TwoSingleTicks);

        r.RowsProcessed.Should().Be(2);
        r.ValidRows.Should().Be(2);
        r.ParseErrors.Should().Be(0);
        r.TotalVolume.Should().Be(5m);
        r.SumAskVolume.Should().Be(3m);
        r.SumBidVolume.Should().Be(2m);
        r.NetDelta.Should().Be(1m);              // AskVolume - BidVolume
        r.MinPrice.Should().Be(7017.50m);
        r.MaxPrice.Should().Be(7017.75m);
    }

    [Fact]
    public void Date_time_parsed_as_utc()
    {
        var r = Validate(TwoSingleTicks);

        r.FirstTimestamp.Should().Be(new DateTimeOffset(2025, 12, 28, 23, 0, 0, TimeSpan.Zero));
        r.FirstTimestamp!.Value.Offset.Should().Be(TimeSpan.Zero);
        r.LastTimestamp!.Value.Second.Should().Be(1);
    }

    [Fact]
    public void Bid_ask_enable_delta_cvd_but_never_dom()
    {
        var r = Validate(TwoSingleTicks);

        r.SupportsDeltaCvd.Should().BeTrue();
        r.SupportsDomLevel2.Should().BeFalse();  // Text-Export hat kein DOM/Level 2
        r.Granularity.Should().Be(SierraGranularity.SingleTick);
        r.IsSingleTick.Should().BeTrue();
    }

    [Fact]
    public void Missing_required_column_throws()
    {
        var csv = "Date, Time, Last\n2025/12/28, 23:00:00, 7017.50\n"; // volume fehlt
        var act = () => Validate(csv);
        act.Should().Throw<CsvMarketDataException>().WithMessage("*Volume*");
    }

    [Fact]
    public void Empty_input_throws_missing_header()
    {
        var act = () => Validate("   \n");
        act.Should().Throw<CsvMarketDataException>();
    }

    [Fact]
    public void Ohlcv_only_produces_no_fake_delta_capability()
    {
        var csv = "Date, Time, Last, Volume\n" +
                  "2025/12/28, 23:00:00, 7017.50, 3\n";
        var r = Validate(csv);

        r.SupportsDeltaCvd.Should().BeFalse();   // keine Bid/Ask-Spalten -> nichts erfinden
        r.Granularity.Should().Be(SierraGranularity.AggregatedOrUnknown); // kein NumberOfTrades
    }

    [Fact]
    public void Aggregated_records_are_not_marked_single_tick()
    {
        var csv = Header + "\n" +
                  "2025/12/28, 23:00:00, 7017.50, 7020.00, 7015.00, 7017.50, 40, 25, 18, 22\n";
        var r = Validate(csv);

        r.Granularity.Should().Be(SierraGranularity.AggregatedOrUnknown);
        r.IsSingleTick.Should().BeFalse();
        r.Issues.Should().Contain(i => i.Code == "SierraAggregatedRecords");
    }

    [Fact]
    public void Parse_error_row_does_not_crash_and_is_counted()
    {
        var csv = Header + "\n" +
                  "2025/12/28, 23:00:00, 7017.50, 7018.00, 7017.00, NOTANUMBER, 3, 1, 0, 3\n" +
                  "2025/12/28, 23:00:01, 7017.75, 7018.00, 7017.50, 7017.75, 2, 1, 2, 0\n";
        var r = Validate(csv);

        r.RowsProcessed.Should().Be(2);
        r.ParseErrors.Should().Be(1);
        r.ValidRows.Should().Be(1);
        r.Issues.Should().Contain(i => i.Code == "ParseError");
    }

    [Fact]
    public void MaxRows_truncates_processing()
    {
        var csv = Header + "\n" +
                  "2025/12/28, 23:00:00, 7017.50, 7018.00, 7017.00, 7017.50, 3, 1, 0, 3\n" +
                  "2025/12/28, 23:00:01, 7017.75, 7018.00, 7017.50, 7017.75, 2, 1, 2, 0\n" +
                  "2025/12/28, 23:00:02, 7018.00, 7018.50, 7017.75, 7018.00, 1, 1, 0, 1\n";
        var r = Validate(csv, maxRows: 1);

        r.RowsProcessed.Should().Be(1);
        r.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Progress_callback_fires_every_100k_rows_without_loading_all_in_ram()
    {
        var progress = new List<long>();
        // 150_000 Zeilen werden ON THE FLY erzeugt (kein großer String im RAM).
        var reader = new GeneratingSierraReader(Header, dataRows: 150_000);

        var r = new SierraLargeFileValidator().Validate(reader, maxRows: null, onProgress: progress.Add);

        r.RowsProcessed.Should().Be(150_000);
        progress.Should().Contain(100_000);          // Progress bei 100k
        progress.Should().OnlyContain(x => x % 100_000 == 0);
    }

    /// <summary>Erzeugt Header + N identische Datenzeilen zeilenweise (Streaming-Quelle für Tests).</summary>
    private sealed class GeneratingSierraReader : TextReader
    {
        private readonly string _header;
        private readonly long _dataRows;
        private long _emitted; // 0 = Header noch offen

        public GeneratingSierraReader(string header, long dataRows) { _header = header; _dataRows = dataRows; }

        public override string? ReadLine()
        {
            if (_emitted == 0) { _emitted = 1; return _header; }
            if (_emitted <= _dataRows)
            {
                _emitted++;
                return "2025/12/28, 23:00:00, 7017.50, 7018.00, 7017.00, 7017.50, 1, 1, 0, 1";
            }
            return null;
        }
    }
}
