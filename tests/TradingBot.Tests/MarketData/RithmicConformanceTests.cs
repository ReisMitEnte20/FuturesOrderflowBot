using FluentAssertions;
using TradingBot.Infrastructure.MarketData.Rithmic.Protocol;
using TradingBot.Infrastructure.MarketData.Rithmic.Protocol.Messages;
using Xunit;

namespace TradingBot.Tests.MarketData;

public class RithmicConformanceTests
{
    private static readonly RithmicProtocolOptions Options = new()
    {
        GatewayUri = new Uri("wss://gateway.test:443"),
        AppName = "TestApp",
        AppVersion = "1.0",
        RequestTimeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public async Task Start_logs_into_order_plant_of_rithmic_test_and_sends_only_login_and_heartbeat()
    {
        var server = new FakeRithmicServer();
        await using var session = new RithmicConformanceSession(Options, server.CreateTransport);

        await session.StartAsync("test-user", "pw");

        session.IsRunning.Should().BeTrue();
        session.StartedAt.Should().NotBeNull();
        var login = server.Logins.Should().ContainSingle().Subject;
        login.InfraType.Should().Be(RequestLogin.Types.SysInfraType.OrderPlant);
        login.SystemName.Should().Be(RithmicConformanceSession.TestSystemName);
        login.AppName.Should().Be("TestApp");
        server.ReceivedTemplates.Distinct().Should().BeSubsetOf(new[]
        {
            RithmicTemplates.RequestRithmicSystemInfo, RithmicTemplates.RequestLogin, RithmicTemplates.RequestHeartbeat,
        });
    }

    [Fact]
    public async Task Start_is_refused_on_gateway_without_rithmic_test()
    {
        var server = new FakeRithmicServer();
        server.Systems.Clear();
        server.Systems.Add("LucidTrading");
        await using var session = new RithmicConformanceSession(Options, server.CreateTransport);

        var act = () => session.StartAsync("user", "pw");

        (await act.Should().ThrowAsync<RithmicProtocolException>()).Which.Message.Should().Contain("Rithmic Test");
        server.Logins.Should().BeEmpty();
        session.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Stop_sends_logout_and_closes()
    {
        var server = new FakeRithmicServer();
        var session = new RithmicConformanceSession(Options, server.CreateTransport);
        await session.StartAsync("user", "pw");

        await session.StopAsync();

        session.IsRunning.Should().BeFalse();
        server.ReceivedTemplates.Should().Contain(RithmicTemplates.RequestLogout);
        server.Connections.Should().OnlyContain(c => c.Closed);
    }

    [Fact]
    public void No_order_message_types_are_compiled_into_the_project()
    {
        // Top-Level-Messages des R|Protocol-Namespaces: nur Marktdaten + Session-Verwaltung, keine Order-/PnL-Messages.
        var names = typeof(RequestLogin).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(RequestLogin).Namespace && !t.IsNested && typeof(Google.Protobuf.IMessage).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        names.Should().NotBeEmpty();
        names.Should().NotContain(n => n.Contains("Order") || n.Contains("Bracket") || n.Contains("PnL") || n.Contains("Account"));
    }
}
