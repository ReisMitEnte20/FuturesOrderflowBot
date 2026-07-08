using TradingBot.Application.Strategies;
using TradingBot.Backtesting;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;
using TradingBot.Research;
using TradingBot.Research.Runner;
using TradingBot.Tests.Backtesting;

namespace TradingBot.Tests.Research;

/// <summary>Deterministischer Fake-Runner: liefert StrategyRunResults per Funktion (kein echter Backtest).</summary>
internal sealed class FakeBacktestRunner : IStrategyBacktestRunner
{
    private readonly Func<StrategyRunInputs, StrategyRunResult> _behavior;
    public List<StrategyRunInputs> Calls { get; } = new();

    public FakeBacktestRunner(Func<StrategyRunInputs, StrategyRunResult> behavior) => _behavior = behavior;

    public Task<StrategyRunResult> RunAsync(StrategyRunInputs inputs, CancellationToken cancellationToken = default)
    {
        Calls.Add(inputs);
        return Task.FromResult(_behavior(inputs));
    }
}

internal static class ResearchTestData
{
    private static readonly DateTimeOffset T0 = BacktestTestData.T0;

    public static BacktestTrade Trade(decimal net, decimal fees = 0m, int minute = 0) => new()
    {
        Symbol = "NQ", Side = TradingBot.Domain.Enums.PositionSide.Long, Quantity = 1,
        EntryTime = T0.AddMinutes(minute), ExitTime = T0.AddMinutes(minute + 1),
        EntryPrice = 20000m, ExitPrice = 20000m,
        GrossPnL = net + fees, Fees = fees, NetPnL = net
    };

    public static List<BacktestTrade> Trades(params decimal[] nets)
        => nets.Select((n, i) => Trade(n, minute: i)).ToList();

    public static BacktestStatistics Stats(decimal net, int tradeCount, decimal maxDd = 0m, decimal? pf = null)
        => new()
        {
            TotalTrades = tradeCount, NetProfit = net, GrossProfit = net, MaxDrawdown = maxDd,
            ProfitFactor = pf, Expectancy = tradeCount > 0 ? net / tradeCount : 0m,
            AverageNetPnLPerTrade = tradeCount > 0 ? net / tradeCount : 0m
        };

    public static StrategyConfig StratConfig(string name = "Cand", Dictionary<string, string>? parameters = null) => new()
    {
        Name = name, Symbol = "NQ", Enabled = true, SuggestedContracts = 1,
        Parameters = parameters ?? new Dictionary<string, string>()
    };

    public static StrategyCandidate Candidate(string name = "Cand", Func<StrategyConfig, IStrategy>? factory = null) => new()
    {
        Name = name, BaseConfig = StratConfig(name),
        CreateStrategy = factory ?? (_ => new NoOpStrategy())
    };

    public static StrategyRunResult RunResult(
        string name, decimal net, int tradeCount, decimal maxDd = 0m,
        StrategyConfig? config = null, IReadOnlyList<BacktestTrade>? trades = null,
        ResearchMetricSet? metrics = null) => new()
        {
            StrategyName = name,
            Config = config ?? StratConfig(name),
            Statistics = Stats(net, tradeCount, maxDd),
            Trades = trades ?? Array.Empty<BacktestTrade>(),
            Metrics = metrics ?? ResearchMetricSet.FromBacktest(Stats(net, tradeCount, maxDd))
        };

    public static StrategyRunInputs Template(IReadOnlyList<MarketTick>? ticks = null) => new()
    {
        Candidate = Candidate(),
        Config = StratConfig(),
        Ticks = ticks ?? BacktestTestData.RisingTicks(10),
        Symbol = "NQ",
        Instrument = BacktestTestData.Instrument(),
        Fee = BacktestTestData.Fee(),
        Broker = BacktestTestData.Broker(),
        Risk = BacktestTestData.Risk(),
        Account = BacktestTestData.Account()
    };

    /// <summary>N Dummy-Ticks für Walk-Forward-Fenstertests (Inhalt egal, nur Anzahl zählt).</summary>
    public static IReadOnlyList<MarketTick> DummyTicks(int count)
        => Enumerable.Range(0, count).Select(i => new MarketTick
        {
            Symbol = "NQ", Timestamp = T0.AddSeconds(i), Price = 20000m + i * 0.25m, Volume = 1m
        }).ToList();
}
