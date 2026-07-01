using TradingBot.Backtesting;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.MarketData;

namespace TradingBot.Tests.Backtesting;

/// <summary>Gemeinsame Testdaten/Fabriken für Backtest-Tests (NQ, permissive Risk).</summary>
internal static class BacktestTestData
{
    public static readonly DateTimeOffset T0 = new(2026, 6, 23, 13, 30, 0, TimeSpan.Zero);

    public static InstrumentProfile Instrument() => new()
    {
        Symbol = "NQ", BrokerSymbol = "NQ", Exchange = "CME", Currency = "USD",
        TickSize = 0.25m, TickValue = 5.00m, PointValue = 20.00m, ContractMultiplier = 20.00m,
        MaxContracts = 5, TradingTimezone = "UTC",
        SessionStart = new TimeOnly(0, 0), SessionEnd = new TimeOnly(23, 59)
    };

    // Per-Side pro Kontrakt = 0.85 + 1.18 + 0.10 + 0.02 = 2.15
    public static FeeProfile Fee(decimal slippageTicks = 0m) => new()
    {
        BrokerName = "AMP Futures", ExecutionProvider = "Rithmic", Instrument = "NQ",
        CommissionPerSide = 0.85m, ExchangeFeePerSide = 1.18m, ClearingFeePerSide = 0.10m,
        NfaFeePerSide = 0.02m, EstimatedSlippageTicks = slippageTicks
    };

    public static BrokerProfile Broker() => new()
    {
        BrokerName = "AMP Futures", ExecutionProvider = "Rithmic",
        AccountType = AccountType.Funded, SupportsMarketOrders = true
    };

    public static RiskConfig Risk(decimal maxDailyLoss = 1_000_000m) => new()
    {
        MaxDailyLoss = maxDailyLoss, MaxLossPerTrade = 1_000_000m, MaxTradesPerDay = 100_000,
        MaxContracts = 5, MaxOpenPositions = 10, MaxOrdersPerMinute = 100_000,
        MaxConsecutiveLosses = 0, EnforceSession = false
    };

    public static TradingAccount Account() => new() { AccountId = "BT-ACC", BrokerName = "AMP Futures" };

    public static MarketTick Tick(int i, decimal price, decimal volume = 1m) => new()
    {
        Symbol = "NQ", Timestamp = T0.AddSeconds(i), Price = price, Volume = volume
    };

    public static ReplayMarketDataProvider Provider(IEnumerable<MarketTick> ticks)
        => new(ticks, ReplayOptions.Fast);

    public static BacktestRequest Request(
        IStrategy strategy, IEnumerable<MarketTick> ticks,
        BacktestConfiguration? config = null, IKillSwitchService? killSwitch = null,
        RiskConfig? risk = null, Func<OrderRequest, bool>? rejectOrder = null,
        decimal feeSlippageTicks = 0m) => new()
        {
            MarketData = Provider(ticks),
            Symbol = "NQ",
            Strategy = strategy,
            Instrument = Instrument(),
            Fee = Fee(feeSlippageTicks),
            Broker = Broker(),
            Risk = risk ?? Risk(),
            Account = Account(),
            Config = config ?? new BacktestConfiguration(),
            KillSwitch = killSwitch,
            RejectOrder = rejectOrder
        };

    /// <summary>Steigende Preisreihe ab startPrice, Schrittweite step, n Ticks.</summary>
    public static List<MarketTick> RisingTicks(int n, decimal startPrice = 20000m, decimal step = 5m)
    {
        var list = new List<MarketTick>(n);
        for (int i = 0; i < n; i++) list.Add(Tick(i, startPrice + step * i));
        return list;
    }

    public static List<MarketTick> FallingTicks(int n, decimal startPrice = 20020m, decimal step = 5m)
    {
        var list = new List<MarketTick>(n);
        for (int i = 0; i < n; i++) list.Add(Tick(i, startPrice - step * i));
        return list;
    }
}
