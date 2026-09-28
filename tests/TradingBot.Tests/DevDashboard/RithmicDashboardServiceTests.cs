using FluentAssertions;
using Microsoft.Extensions.Configuration;
using TradingBot.DevDashboard.Services;
using TradingBot.Tests.MarketData;
using Xunit;

namespace TradingBot.Tests.DevDashboard;

public class RithmicDashboardServiceTests
{
    private static IConfiguration Config(string? appName = "TestApp") => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Rithmic:AppName"] = appName,
            ["Rithmic:AppVersion"] = "1.0",
            ["Rithmic:Gateways:Rithmic Test"] = "wss://gateway.test:443",
        })
        .Build();

    private static RithmicConnectRequest Request(string gateway = "Rithmic Test") =>
        new("user", "secret-password", "Rithmic Test", gateway);

    [Fact]
    public async Task Connect_via_configured_gateway_succeeds_and_status_holds_no_password()
    {
        var server = new FakeRithmicServer();
        await using var service = new RithmicDashboardService(Config(), transportFactory: server.CreateTransport);

        var result = await service.ConnectAsync(Request());

        result.Success.Should().BeTrue(result.Message);
        result.GatewayUrl.Should().Be("wss://gateway.test:443");
        var status = service.GetStatus();
        status.IsConnected.Should().BeTrue();
        status.Username.Should().Be("user");
        status.SystemName.Should().Be("Rithmic Test");
        status.HasHistory.Should().BeTrue();
        status.ToString().Should().NotContain("secret-password");
        Request().ToString().Should().NotContain("secret-password");
    }

    [Fact]
    public async Task Unknown_gateway_fails_without_any_connection()
    {
        var server = new FakeRithmicServer();
        await using var service = new RithmicDashboardService(Config(), transportFactory: server.CreateTransport);

        var result = await service.ConnectAsync(Request("Chicago"));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Chicago").And.Contain("Rithmic Test");
        server.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_app_name_fails_without_any_connection()
    {
        var server = new FakeRithmicServer();
        await using var service = new RithmicDashboardService(Config(appName: null), transportFactory: server.CreateTransport);

        var result = await service.ConnectAsync(Request());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("AppName");
        server.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task Rejected_login_is_reported_and_service_stays_disconnected()
    {
        var server = new FakeRithmicServer { LoginRpCode = ["13", "permission denied"] };
        await using var service = new RithmicDashboardService(Config(), transportFactory: server.CreateTransport);

        var result = await service.ConnectAsync(Request());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("permission denied");
        service.GetStatus().IsConnected.Should().BeFalse();
        service.GetStatus().LastError.Should().Contain("permission denied");
    }

    [Fact]
    public async Task Disconnect_resets_status()
    {
        var server = new FakeRithmicServer();
        await using var service = new RithmicDashboardService(Config(), transportFactory: server.CreateTransport);
        await service.ConnectAsync(Request());

        var result = await service.DisconnectAsync();

        result.Success.Should().BeTrue();
        var status = service.GetStatus();
        status.IsConnected.Should().BeFalse();
        status.Username.Should().BeNull();
        status.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Repeated_connect_with_same_account_is_idempotent_other_account_is_refused()
    {
        var server = new FakeRithmicServer();
        await using var service = new RithmicDashboardService(Config(), transportFactory: server.CreateTransport);
        await service.ConnectAsync(Request());

        var again = await service.ConnectAsync(Request());
        var other = await service.ConnectAsync(Request() with { UserId = "someone-else" });

        again.Success.Should().BeTrue();
        other.Success.Should().BeFalse();
        other.Message.Should().Contain("Disconnect");
        server.Logins.Should().HaveCount(2); // nur der erste Connect (Ticker + History) hat eingeloggt
        var status = service.GetStatus();
        status.IsConnected.Should().BeTrue();
        status.Username.Should().Be("user");
        status.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Live_trades_after_subscribe_are_available_as_tape_with_real_aggressor()
    {
        var server = new FakeRithmicServer
        {
            OnSubscribe = (conn, req) =>
            {
                conn.Push(Trade(req, 7746.00, 1, TradingBot.Infrastructure.MarketData.Rithmic.Protocol.Messages.LastTrade.Types.TransactionType.Sell, snapshot: true));
                conn.Push(Trade(req, 7746.25, 2, TradingBot.Infrastructure.MarketData.Rithmic.Protocol.Messages.LastTrade.Types.TransactionType.Buy));
                conn.Push(Trade(req, 7746.00, 4, TradingBot.Infrastructure.MarketData.Rithmic.Protocol.Messages.LastTrade.Types.TransactionType.Sell));
            },
        };
        await using var service = new RithmicDashboardService(Config(), transportFactory: server.CreateTransport);
        await service.ConnectAsync(Request());

        await service.SubscribeAsync("MESZ6", "CME");
        RithmicTickPage page = service.GetTicks("MESZ6", "CME", 0, 100);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (page.Ticks.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            page = service.GetTicks("MESZ6", "CME", 0, 100);
        }

        page.Ticks.Select(t => (t.Price, t.Size, t.Aggressor)).Should().Equal(
            (7746.25m, 2m, TradingBot.Domain.Enums.AggressorSide.Buy),
            (7746.00m, 4m, TradingBot.Domain.Enums.AggressorSide.Sell));
        service.GetTicks("MESZ6", "CME", page.LastSeq, 100).Ticks.Should().BeEmpty();
    }

    [Fact]
    public async Task Ticks_for_unsubscribed_symbol_are_refused()
    {
        var server = new FakeRithmicServer();
        await using var service = new RithmicDashboardService(Config(), transportFactory: server.CreateTransport);
        await service.ConnectAsync(Request());

        var act = () => service.GetTicks("NQZ6", "CME", 0, 100);

        act.Should().Throw<InvalidOperationException>().WithMessage("*nicht abonniert*");
    }

    private static TradingBot.Infrastructure.MarketData.Rithmic.Protocol.Messages.LastTrade Trade(
        TradingBot.Infrastructure.MarketData.Rithmic.Protocol.Messages.RequestMarketDataUpdate req, double price, int size,
        TradingBot.Infrastructure.MarketData.Rithmic.Protocol.Messages.LastTrade.Types.TransactionType side, bool snapshot = false) => new()
    {
        TemplateId = TradingBot.Infrastructure.MarketData.Rithmic.Protocol.RithmicTemplates.LastTrade,
        Symbol = req.Symbol,
        Exchange = req.Exchange,
        PresenceBits = 1,
        IsSnapshot = snapshot,
        TradePrice = price,
        TradeSize = size,
        Aggressor = side,
        Ssboe = 1_780_000_000,
    };

    [Fact]
    public void Options_list_configured_gateways_only()
    {
        var service = new RithmicDashboardService(Config());

        var options = service.GetOptions();

        options.Gateways.Should().Equal("Rithmic Test");
        options.AppConfigured.Should().BeTrue();
    }
}
