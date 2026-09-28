using FluentAssertions;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.MarketData.Rithmic.Protocol;
using TradingBot.Infrastructure.MarketData.Rithmic.Protocol.Messages;
using Xunit;

namespace TradingBot.Tests.MarketData;

public class RithmicProtocolClientTests
{
    private static readonly RithmicProtocolOptions Options = new()
    {
        GatewayUri = new Uri("wss://gateway.test:443"),
        AppName = "TestApp",
        AppVersion = "1.2.3",
        RequestTimeout = TimeSpan.FromSeconds(5),
    };

    private static readonly RithmicLoginCredentials Credentials = new("user1", "pw-secret", "Rithmic Test");

    private static RithmicMarketDataClient CreateClient(FakeRithmicServer server) =>
        new(Options, server.CreateTransport);

    [Fact]
    public async Task Connect_checks_system_info_then_logs_in_ticker_and_history_only()
    {
        var server = new FakeRithmicServer();
        await using var client = CreateClient(server);

        await client.ConnectAsync(Credentials);

        client.IsConnected.Should().BeTrue();
        client.HasHistory.Should().BeTrue();
        server.ReceivedTemplates.First().Should().Be(RithmicTemplates.RequestRithmicSystemInfo);
        server.Logins.Select(l => l.InfraType).Should().BeEquivalentTo(new[]
        {
            RequestLogin.Types.SysInfraType.TickerPlant,
            RequestLogin.Types.SysInfraType.HistoryPlant,
        });
        server.Logins.Should().AllSatisfy(l =>
        {
            l.User.Should().Be("user1");
            l.Password.Should().Be("pw-secret");
            l.SystemName.Should().Be("Rithmic Test");
            l.AppName.Should().Be("TestApp");
            l.AppVersion.Should().Be("1.2.3");
            l.TemplateVersion.Should().Be("3.9");
        });
        server.ConnectedUris.Should().OnlyContain(u => u == Options.GatewayUri);
        // Nach dem Login muss mindestens ein Heartbeat gesendet werden.
        server.ReceivedTemplates.Should().Contain(RithmicTemplates.RequestHeartbeat);
    }

    [Fact]
    public async Task Connect_with_unknown_system_fails_before_login_and_lists_available_systems()
    {
        var server = new FakeRithmicServer();
        await using var client = CreateClient(server);

        var act = () => client.ConnectAsync(Credentials with { SystemName = "LucidTrading" });

        (await act.Should().ThrowAsync<RithmicProtocolException>())
            .Which.Message.Should().Contain("LucidTrading").And.Contain("Rithmic Test");
        server.Logins.Should().BeEmpty();
        client.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task Rejected_login_surfaces_rp_code_and_stays_disconnected()
    {
        var server = new FakeRithmicServer { LoginRpCode = ["13", "permission denied"] };
        await using var client = CreateClient(server);

        var act = () => client.ConnectAsync(Credentials);

        var ex = (await act.Should().ThrowAsync<RithmicProtocolException>()).Which;
        ex.Message.Should().Contain("permission denied");
        ex.RpCode.Should().Equal("13", "permission denied");
        client.IsConnected.Should().BeFalse();
        server.Connections.Should().Contain(c => c.Closed);
    }

    [Fact]
    public void Only_ticker_and_history_plants_are_representable()
    {
        Enum.GetValues<RithmicPlant>().Should().BeEquivalentTo(new[] { RithmicPlant.Ticker, RithmicPlant.History });
    }

    [Fact]
    public async Task Live_trades_carry_real_aggressor_and_skip_subscription_snapshot()
    {
        var server = new FakeRithmicServer
        {
            OnSubscribe = (conn, req) =>
            {
                conn.Push(new BestBidOffer
                {
                    TemplateId = RithmicTemplates.BestBidOffer, Symbol = req.Symbol, Exchange = req.Exchange,
                    PresenceBits = 3, BidPrice = 6000.25, BidSize = 12, AskPrice = 6000.50, AskSize = 7, Ssboe = 1_780_000_000,
                });
                conn.Push(Trade(req, 6000.25, 3, LastTrade.Types.TransactionType.Sell, snapshot: true));
                conn.Push(Trade(req, 6000.50, 2, LastTrade.Types.TransactionType.Buy));
                conn.Push(Trade(req, 6000.25, 5, LastTrade.Types.TransactionType.Sell));
            },
        };
        await using var client = CreateClient(server);
        await client.ConnectAsync(Credentials);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ticks = new List<TradingBot.Domain.Models.MarketTick>();
        var reading = Task.Run(async () =>
        {
            await foreach (var t in client.ReadTicksAsync("MESZ6", "CME", cts.Token))
            {
                ticks.Add(t);
                if (ticks.Count == 2) break;
            }
        });
        await Task.Delay(50);
        await client.SubscribeAsync("MESZ6", "CME", cts.Token);
        await reading;

        ticks.Select(t => (t.Price, t.Volume, t.Aggressor)).Should().Equal(
            (6000.50m, 2m, AggressorSide.Buy),
            (6000.25m, 5m, AggressorSide.Sell));
        ticks.Should().AllSatisfy(t => { t.Bid.Should().Be(6000.25m); t.Ask.Should().Be(6000.50m); t.HasOrderFlow.Should().BeTrue(); });

        var quote = client.GetQuote("MESZ6", "CME")!;
        quote.Bid.Should().Be(6000.25m);
        quote.AskSize.Should().Be(7);
        quote.LastPrice.Should().Be(6000.25m);
    }

    [Fact]
    public async Task Repeated_subscribe_is_idempotent_and_sends_only_one_request()
    {
        var server = new FakeRithmicServer();
        await using var client = CreateClient(server);
        await client.ConnectAsync(Credentials);

        await client.SubscribeAsync("MESZ6", "CME");
        var again = () => client.SubscribeAsync("MESZ6", "CME");

        await again.Should().NotThrowAsync();
        server.ReceivedTemplates.Count(t => t == RithmicTemplates.RequestMarketDataUpdate).Should().Be(1);
        client.IsSubscribed("MESZ6", "CME").Should().BeTrue();
    }

    [Fact]
    public async Task Server_side_already_subscribed_1029_is_treated_as_success()
    {
        var server = new FakeRithmicServer();
        server.Subscriptions.Add("CME:MESZ6"); // Abo besteht serverseitig schon (z. B. anderer Client-Zustand)
        await using var client = CreateClient(server);
        await client.ConnectAsync(Credentials);

        var act = () => client.SubscribeAsync("MESZ6", "CME");

        await act.Should().NotThrowAsync();
        client.IsSubscribed("MESZ6", "CME").Should().BeTrue();
    }

    [Fact]
    public async Task Resubscribe_after_unsubscribe_sends_new_request()
    {
        var server = new FakeRithmicServer();
        await using var client = CreateClient(server);
        await client.ConnectAsync(Credentials);

        await client.SubscribeAsync("MESZ6", "CME");
        await client.UnsubscribeAsync("MESZ6", "CME");
        await client.SubscribeAsync("MESZ6", "CME");

        server.ReceivedTemplates.Count(t => t == RithmicTemplates.RequestMarketDataUpdate).Should().Be(3);
        client.IsSubscribed("MESZ6", "CME").Should().BeTrue();
    }

    [Fact]
    public async Task Minute_bars_are_collected_until_final_rp_code()
    {
        var from = DateTimeOffset.FromUnixTimeSeconds(1_780_000_000);
        var server = new FakeRithmicServer
        {
            Bars = req => Enumerable.Range(1, 3).Select(i => new ResponseTimeBarReplay
            {
                Symbol = req.Symbol, Exchange = req.Exchange, Marker = req.StartIndex + i * 60,
                OpenPrice = 100 + i, HighPrice = 101 + i, LowPrice = 99 + i, ClosePrice = 100.5 + i,
                Volume = 10UL * (ulong)i, BidVolume = 4UL * (ulong)i, AskVolume = 6UL * (ulong)i, NumTrades = (ulong)i,
            }),
        };
        await using var client = CreateClient(server);
        await client.ConnectAsync(Credentials);

        var bars = await client.GetMinuteBarsAsync("MESZ6", "CME", from, from.AddMinutes(3));

        bars.Should().HaveCount(3);
        bars[0].EndTime.Should().Be(from.AddMinutes(1));
        bars[2].Close.Should().Be(103.5m);
        bars[2].BidVolume.Should().Be(12);
        bars[2].AskVolume.Should().Be(18);
        server.ReceivedTemplates.Count(t => t == RithmicTemplates.RequestTimeBarReplay).Should().Be(1);
    }

    [Fact]
    public async Task Front_month_is_resolved_from_ticker_plant()
    {
        var server = new FakeRithmicServer { FrontMonth = "MESZ6" };
        await using var client = CreateClient(server);
        await client.ConnectAsync(Credentials);

        (await client.GetFrontMonthAsync("MES", "CME")).Should().Be("MESZ6");
    }

    [Fact]
    public async Task Forced_logout_marks_client_disconnected_with_reason()
    {
        var server = new FakeRithmicServer();
        await using var client = CreateClient(server);
        await client.ConnectAsync(Credentials, includeHistory: false);

        var tickerConnection = server.Connections.Last();
        tickerConnection.Push(new ForcedLogout { TemplateId = RithmicTemplates.ForcedLogout });

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (client.IsConnected && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        client.IsConnected.Should().BeFalse();
        client.LastError.Should().Contain("ForcedLogout");
    }

    [Fact]
    public async Task Disconnect_sends_logout_and_closes_connections()
    {
        var server = new FakeRithmicServer();
        var client = CreateClient(server);
        await client.ConnectAsync(Credentials);

        await client.DisconnectAsync();

        client.IsConnected.Should().BeFalse();
        server.ReceivedTemplates.Count(t => t == RithmicTemplates.RequestLogout).Should().Be(2);
        server.Connections.Should().OnlyContain(c => c.Closed);
    }

    [Fact]
    public void Credentials_ToString_never_contains_password()
    {
        Credentials.ToString().Should().NotContain("pw-secret").And.Contain("user1");
    }

    private static LastTrade Trade(RequestMarketDataUpdate req, double price, int size, LastTrade.Types.TransactionType side, bool snapshot = false) => new()
    {
        TemplateId = RithmicTemplates.LastTrade,
        Symbol = req.Symbol,
        Exchange = req.Exchange,
        PresenceBits = (uint)LastTrade.Types.PresenceBits.LastTrade,
        IsSnapshot = snapshot,
        TradePrice = price,
        TradeSize = size,
        Aggressor = side,
        Ssboe = 1_780_000_001,
        Usecs = 250,
    };
}
