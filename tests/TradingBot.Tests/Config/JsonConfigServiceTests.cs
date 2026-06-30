using FluentAssertions;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Config;
using Xunit;

namespace TradingBot.Tests.Config;

public class JsonConfigServiceTests
{
    private readonly JsonConfigService _sut = new();

    [Fact]
    public async Task SaveAsync_then_LoadAsync_roundtrips_the_object()
    {
        using var dir = new TempDir();
        var path = dir.File("dashboard.json");
        var original = new DashboardConfig { Enabled = true, Host = "127.0.0.1", Port = 8080, RefreshIntervalMs = 500 };

        await _sut.SaveAsync(path, original);
        var loaded = await _sut.LoadAsync<DashboardConfig>(path);

        File.Exists(path).Should().BeTrue();
        loaded.Should().Be(original);
    }

    [Fact]
    public async Task LoadAsync_throws_ConfigFileNotFound_when_file_missing()
    {
        using var dir = new TempDir();
        var path = dir.File("does-not-exist.json");

        var act = () => _sut.LoadAsync<DashboardConfig>(path);

        await act.Should().ThrowAsync<ConfigFileNotFoundException>();
    }

    [Fact]
    public async Task LoadAsync_throws_ConfigParse_on_invalid_json()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("broken.json", "{ this is : not valid json ");

        var act = () => _sut.LoadAsync<DashboardConfig>(path);

        await act.Should().ThrowAsync<ConfigParseException>();
    }

    [Fact]
    public async Task LoadAsync_throws_ConfigParse_when_json_is_literal_null()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("null.json", "null");

        var act = () => _sut.LoadAsync<DashboardConfig>(path);

        await act.Should().ThrowAsync<ConfigParseException>();
    }

    [Fact]
    public async Task RiskConfig_loads_via_generic_ConfigService()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("risk.json", """
        {
          "maxDailyLoss": 1000.0,
          "maxLossPerTrade": 300.0,
          "maxTradesPerDay": 10,
          "maxContracts": 5,
          "maxOpenPositions": 1,
          "maxOrdersPerMinute": 10,
          "maxConsecutiveLosses": 3,
          "enforceSession": true
        }
        """);

        var risk = await _sut.LoadAsync<RiskConfig>(path);

        risk.MaxDailyLoss.Should().Be(1000.0m);
        risk.MaxContracts.Should().Be(5);
        risk.EnforceSession.Should().BeTrue();
    }
}
