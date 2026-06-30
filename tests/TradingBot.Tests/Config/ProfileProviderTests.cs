using FluentAssertions;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Config;
using Xunit;

namespace TradingBot.Tests.Config;

public class ProfileProviderTests
{
    private readonly JsonConfigService _config = new();

    private const string BrokerJson = """
    {
      "note": "Example values only.",
      "brokerName": "AMP Futures",
      "executionProvider": "Rithmic",
      "accountType": "funded",
      "accountCurrency": "USD",
      "supportsMarketOrders": true,
      "supportsLimitOrders": true,
      "supportsStopOrders": true,
      "maxOrdersPerSecond": 5,
      "apiRateLimit": 100
    }
    """;

    private const string InstrumentJson = """
    {
      "symbol": "NQ",
      "brokerSymbol": "NQ",
      "exchange": "CME",
      "currency": "USD",
      "tickSize": 0.25,
      "tickValue": 5.0,
      "pointValue": 20.0,
      "contractMultiplier": 20.0,
      "sessionStart": "09:30:00",
      "sessionEnd": "16:00:00",
      "maxContracts": 5
    }
    """;

    private const string FeeJson = """
    {
      "brokerName": "AMP Futures",
      "executionProvider": "Rithmic",
      "instrument": "MNQ",
      "commissionPerSide": 0.25,
      "exchangeFeePerSide": 0.10,
      "nfaFeePerSide": 0.02,
      "estimatedSlippageTicks": 1.0,
      "maxAllowedSlippageTicks": 3.0
    }
    """;

    [Fact]
    public async Task BrokerProfileProvider_loads_and_indexes_by_name()
    {
        using var dir = new TempDir();
        dir.WriteFile("amp.json", BrokerJson);
        var provider = new BrokerProfileProvider(_config, dir.Path);

        var profile = await provider.GetAsync("AMP Futures");

        profile.Should().NotBeNull();
        profile!.ExecutionProvider.Should().Be("Rithmic");
        profile.AccountType.Should().Be(AccountType.Funded);
        profile.SupportsMarketOrders.Should().BeTrue();
    }

    [Fact]
    public async Task InstrumentProfileProvider_loads_and_indexes_by_symbol()
    {
        using var dir = new TempDir();
        dir.WriteFile("nq.json", InstrumentJson);
        var provider = new InstrumentProfileProvider(_config, dir.Path);

        var nq = await provider.GetAsync("NQ");

        nq.Should().NotBeNull();
        nq!.TickSize.Should().Be(0.25m);
        nq.TickValue.Should().Be(5.0m);
        nq.SessionStart.Should().Be(new TimeOnly(9, 30));
    }

    [Fact]
    public async Task FeeProfileProvider_loads_and_indexes_by_broker_provider_instrument()
    {
        using var dir = new TempDir();
        dir.WriteFile("mnq.json", FeeJson);
        var provider = new FeeProfileProvider(_config, dir.Path);

        var fee = await provider.GetAsync("AMP Futures", "Rithmic", "MNQ");

        fee.Should().NotBeNull();
        fee!.CommissionPerSide.Should().Be(0.25m);
        fee.NfaFeePerSide.Should().Be(0.02m);
    }

    [Fact]
    public async Task InstrumentProfileProvider_returns_null_for_unknown_symbol()
    {
        using var dir = new TempDir();
        dir.WriteFile("nq.json", InstrumentJson);
        var provider = new InstrumentProfileProvider(_config, dir.Path);

        var unknown = await provider.GetAsync("ES");

        unknown.Should().BeNull();
    }

    [Fact]
    public async Task Provider_throws_ConfigFileNotFound_when_directory_missing()
    {
        var provider = new BrokerProfileProvider(_config, @"A:\definitely\missing\dir");

        var act = () => provider.GetAsync("AMP Futures");

        await act.Should().ThrowAsync<ConfigFileNotFoundException>();
    }
}
