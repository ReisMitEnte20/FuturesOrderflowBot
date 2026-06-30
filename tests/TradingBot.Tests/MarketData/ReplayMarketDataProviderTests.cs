using FluentAssertions;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.MarketData;
using Xunit;

namespace TradingBot.Tests.MarketData;

public class ReplayMarketDataProviderTests
{
    private static async Task<List<MarketTick>> Collect(
        ReplayMarketDataProvider provider, string symbol = "NQ", CancellationToken ct = default)
    {
        var list = new List<MarketTick>();
        await foreach (var t in provider.SubscribeTicksAsync(symbol, ct))
            list.Add(t);
        return list;
    }

    [Fact]
    public async Task Yields_ticks_in_chronological_order_even_if_input_unsorted()
    {
        var ticks = new[] { Md.Tick(2, 102m, 1), Md.Tick(0, 100m, 1), Md.Tick(1, 101m, 1) };
        var sut = new ReplayMarketDataProvider(ticks, ReplayOptions.Fast);
        await sut.ConnectAsync();

        var got = await Collect(sut);

        got.Select(t => t.Price).Should().ContainInOrder(100m, 101m, 102m);
    }

    [Fact]
    public async Task Filters_by_symbol()
    {
        var ticks = new[] { Md.Tick(0, 100m, 1, "NQ"), Md.Tick(1, 50m, 1, "ES"), Md.Tick(2, 101m, 1, "NQ") };
        var sut = new ReplayMarketDataProvider(ticks, ReplayOptions.Fast);
        await sut.ConnectAsync();

        var got = await Collect(sut, "NQ");

        got.Should().HaveCount(2);
        got.Should().OnlyContain(t => t.Symbol == "NQ");
    }

    [Fact]
    public async Task Subscribe_before_connect_throws()
    {
        var sut = new ReplayMarketDataProvider(new[] { Md.Tick(0, 100m, 1) }, ReplayOptions.Fast);

        var act = async () => await Collect(sut);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Cancellation_token_stops_the_stream()
    {
        var ticks = new[] { Md.Tick(0, 100m, 1), Md.Tick(1, 101m, 1), Md.Tick(2, 102m, 1) };
        var cts = new CancellationTokenSource();
        int calls = 0;
        // RealTime ruft delay zwischen Ticks -> beim ersten Aufruf abbrechen.
        Func<TimeSpan, CancellationToken, Task> delay = (_, _) => { if (++calls == 1) cts.Cancel(); return Task.CompletedTask; };

        var sut = new ReplayMarketDataProvider(ticks, ReplayOptions.Realtime, delay);
        await sut.ConnectAsync();

        var got = await Collect(sut, "NQ", cts.Token);

        got.Should().HaveCount(1); // nur der erste Tick vor dem Abbruch
    }

    [Fact]
    public async Task DisconnectAsync_stops_the_stream()
    {
        var ticks = new[] { Md.Tick(0, 100m, 1), Md.Tick(1, 101m, 1), Md.Tick(2, 102m, 1) };
        ReplayMarketDataProvider sut = null!;
        int calls = 0;
        Func<TimeSpan, CancellationToken, Task> delay = (_, _) => { if (++calls == 1) sut.DisconnectAsync(); return Task.CompletedTask; };

        sut = new ReplayMarketDataProvider(ticks, ReplayOptions.Realtime, delay);
        await sut.ConnectAsync();

        var got = await Collect(sut);

        got.Should().HaveCount(1);
        sut.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task RealTime_delays_match_tick_gaps()
    {
        var ticks = new[] { Md.Tick(0, 100m, 1), Md.Tick(0.25, 101m, 1), Md.Tick(0.50, 102m, 1) };
        var recorded = new List<TimeSpan>();
        Func<TimeSpan, CancellationToken, Task> delay = (ts, _) => { recorded.Add(ts); return Task.CompletedTask; };

        var sut = new ReplayMarketDataProvider(ticks, ReplayOptions.Realtime, delay);
        await sut.ConnectAsync();
        await Collect(sut);

        recorded.Should().HaveCount(2);
        recorded[0].Should().Be(TimeSpan.FromMilliseconds(250));
        recorded[1].Should().Be(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task AsFastAsPossible_does_not_delay()
    {
        var ticks = new[] { Md.Tick(0, 100m, 1), Md.Tick(1, 101m, 1) };
        var recorded = new List<TimeSpan>();
        Func<TimeSpan, CancellationToken, Task> delay = (ts, _) => { recorded.Add(ts); return Task.CompletedTask; };

        var sut = new ReplayMarketDataProvider(ticks, ReplayOptions.Fast, delay);
        await sut.ConnectAsync();
        await Collect(sut);

        recorded.Should().BeEmpty();
    }

    [Fact]
    public void Provider_has_no_execution_dependency()
    {
        var ctorParams = typeof(ReplayMarketDataProvider).GetConstructors()[0].GetParameters();
        ctorParams.Should().NotContain(p => p.ParameterType == typeof(IBrokerExecutionAdapter));
        ctorParams.Should().NotContain(p => p.ParameterType == typeof(IOrderManager));
    }
}
