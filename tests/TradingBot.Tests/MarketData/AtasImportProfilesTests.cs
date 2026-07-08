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
/// Phase 12E-A: sichert die ATAS-Import-VORBEREITUNG (Export-Guide, Beispiel-Mapping-Profile,
/// Ordnerstruktur) und die Ehrlichkeits-Garantien (kein Fake-Orderflow, InsufficientData bei
/// fehlenden Pflichtspalten). KEINE echten ATAS-Daten nötig.
/// </summary>
public class AtasImportProfilesTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string Root =>
        RepoLocator.FindRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("Repo-Wurzel nicht gefunden.");

    private static string ProfilePath(string file) => Path.Combine(Root, "config", "import-profiles", file);

    // logisches Feld → Pflichtfelder je Beispielprofil (dokumentiert)
    public static IEnumerable<object[]> Profiles() => new[]
    {
        new object[] { "atas-tick.example.json", MarketDataSourceType.AggressorTick,
            new[] { "timestamp", "symbol", "price", "volume" } },
        new object[] { "atas-orderflow-bar.example.json", MarketDataSourceType.OrderFlowBars,
            new[] { "bartimestamp", "symbol", "open", "high", "low", "close", "volume", "bidvolume", "askvolume", "delta" } },
        new object[] { "atas-footprint.example.json", MarketDataSourceType.Footprint,
            new[] { "bartimestamp", "symbol", "pricelevel", "bidvolumeatprice", "askvolumeatprice" } },
        new object[] { "atas-volume-profile.example.json", MarketDataSourceType.VolumeProfile,
            new[] { "sessiondate", "symbol", "pricelevel", "volumeatprice" } },
        new object[] { "sierra-intraday.example.json", MarketDataSourceType.AggressorTick,
            new[] { "date", "time", "last", "volume" } },
    };

    [Fact]
    public void Export_guide_exists_and_covers_data_levels()
    {
        var guide = Path.Combine(Root, "docs", "ATAS_EXPORT_GUIDE.md");
        File.Exists(guide).Should().BeTrue();

        var text = File.ReadAllText(guide);
        text.Should().Contain("InsufficientData");
        text.Should().Contain("Stacked Imbalances");
        text.Should().Contain("HVN");
        text.Should().Contain("Aggressor");
    }

    [Fact]
    public void Market_data_source_guide_states_edge_goal_and_ohlcv_limitation()
    {
        var guide = Path.Combine(Root, "docs", "MARKET_DATA_SOURCE_GUIDE.md");
        File.Exists(guide).Should().BeTrue();

        var text = File.ReadAllText(guide);
        text.Should().Contain(
            "Simple OHLCV strategies are useful for infrastructure validation, but not sufficient for the project's main edge goal.");
        text.Should().Contain("buy-and-hold");
        text.Should().Contain("Sharpe");
        text.Should().Contain("InsufficientData");
    }

    [Fact]
    public void Samples_atas_folder_structure_exists()
    {
        Directory.Exists(Path.Combine(Root, "samples", "atas", "raw")).Should().BeTrue();
        File.Exists(Path.Combine(Root, "samples", "atas", "README.md")).Should().BeTrue();

        // Noch KEINE echten CSV-Exporte vorhanden (nur Platzhalter).
        Directory.GetFiles(Path.Combine(Root, "samples", "atas", "raw"), "*.csv").Should().BeEmpty();
    }

    [Fact]
    public void Samples_sierra_folder_structure_exists()
    {
        Directory.Exists(Path.Combine(Root, "samples", "sierra", "raw")).Should().BeTrue();
        File.Exists(Path.Combine(Root, "samples", "sierra", "README.md")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(Root, "samples", "sierra", "raw"), "*.csv").Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Example_profile_is_valid_json_with_documented_required_fields(
        string file, MarketDataSourceType expectedType, string[] requiredFields)
    {
        var json = File.ReadAllText(ProfilePath(file));

        var profile = JsonSerializer.Deserialize<CsvImportProfile>(json, JsonOpts);
        profile.Should().NotBeNull();
        profile!.SourceType.Should().Be(expectedType);

        // Jede dokumentierte Pflichtspalte ist im Mapping vorhanden (auf einen realen Spaltennamen).
        foreach (var field in requiredFields)
            profile.ColumnMap.Keys.Should().Contain(k => string.Equals(k, field, StringComparison.OrdinalIgnoreCase),
                $"Pflichtfeld '{field}' muss in {file} gemappt sein");
    }

    [Fact]
    public void Tick_profile_maps_real_looking_header_end_to_end()
    {
        var profile = JsonSerializer.Deserialize<CsvImportProfile>(
            File.ReadAllText(ProfilePath("atas-tick.example.json")), JsonOpts)!;

        // CSV mit den (Beispiel-)ATAS-Spaltennamen aus dem Profil-Mapping.
        var csv = """
        Time,Instrument,Price,Volume,AggressorSide
        2026-07-08T13:30:00Z,MNQ,20000.00,3,buy
        2026-07-08T13:30:01Z,MNQ,20000.25,1,sell
        """;

        var result = new AtasTickCsvImporter(profile).Import(new StringReader(csv));

        result.Ticks.Should().HaveCount(2);
        result.SourceType.Should().Be(MarketDataSourceType.AggressorTick);
        result.Capabilities.SupportsDeltaCvd.Should().BeTrue();
    }

    [Fact]
    public void Header_only_sample_is_not_treated_as_real_orderflow()
    {
        // Nur Header, keine Datenzeilen -> keine Bars, keine Orderflow-Capabilities (kein Fake).
        var csv = "bartimestamp,symbol,open,high,low,close,volume,bidvolume,askvolume,delta";

        var result = new AtasOrderFlowBarCsvImporter().Import(new StringReader(csv));

        result.Quality.RowsAccepted.Should().Be(0);
        result.Capabilities.SupportsDeltaCvd.Should().BeFalse();
        result.Capabilities.SupportsStackedImbalances.Should().BeFalse();
    }

    [Fact]
    public void Missing_required_column_reports_insufficient_data()
    {
        // Pflichtspalte 'volume' fehlt -> Import bricht mit klarer Meldung ab (kein stilles Raten).
        var csv = """
        timestamp,symbol,price
        2026-07-08T13:30:00Z,MNQ,20000.00
        """;

        var act = () => new AtasTickCsvImporter().Import(new StringReader(csv));

        act.Should().Throw<CsvMarketDataException>().WithMessage("*volume*");
    }

    [Fact]
    public void Ohlcv_only_ticks_produce_no_fake_orderflow_capabilities()
    {
        var csv = """
        timestamp,symbol,price,volume
        2026-07-08T13:30:00Z,MNQ,20000.00,3
        2026-07-08T13:30:01Z,MNQ,20000.25,1
        """;

        var result = new AtasTickCsvImporter().Import(new StringReader(csv));

        result.SourceType.Should().Be(MarketDataSourceType.MinimalTick);
        result.Capabilities.Should().Be(Domain.Models.OrderFlowCapabilities.None); // nichts erfunden
    }
}
