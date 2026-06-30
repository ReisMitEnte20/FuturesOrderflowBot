using Moq;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Tests.RiskManagement;

/// <summary>Baukasten für RiskManager-Tests: gültiges Default-Setup, das pro Test getweakt wird.</summary>
internal static class RiskTestData
{
    public static readonly DateOnly Date = new(2026, 6, 23);
    public static readonly DateTimeOffset Noon = new(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);

    public static Mock<IKillSwitchService> KillSwitch(bool active = false)
    {
        var m = new Mock<IKillSwitchService>();
        m.SetupGet(x => x.IsActive).Returns(active);
        return m;
    }

    public static Mock<ISafetyMonitor> Safety(
        bool broker = true, bool feed = true, bool mismatch = false, SafetyStatus status = SafetyStatus.Ok)
    {
        var m = new Mock<ISafetyMonitor>();
        m.SetupGet(x => x.IsBrokerConnected).Returns(broker);
        m.SetupGet(x => x.IsMarketDataConnected).Returns(feed);
        m.SetupGet(x => x.HasPositionMismatch).Returns(mismatch);
        m.SetupGet(x => x.Status).Returns(status);
        m.SetupGet(x => x.IsSafeToTrade).Returns(broker && feed && !mismatch && status == SafetyStatus.Ok);
        return m;
    }

    public static Mock<IClock> Clock(DateTimeOffset? now = null)
    {
        var m = new Mock<IClock>();
        m.SetupGet(x => x.UtcNow).Returns(now ?? Noon);
        return m;
    }

    public static InstrumentProfile Instrument() => new()
    {
        Symbol = "NQ", BrokerSymbol = "NQ", Exchange = "CME", Currency = "USD",
        TickSize = 0.25m, TickValue = 5.00m, PointValue = 20.00m, ContractMultiplier = 20.00m,
        MaxContracts = 5, TradingTimezone = "UTC",
        SessionStart = new TimeOnly(9, 30), SessionEnd = new TimeOnly(16, 0)
    };

    public static FeeProfile Fee() => new()
    {
        BrokerName = "AMP Futures", ExecutionProvider = "Rithmic", Instrument = "NQ",
        CommissionPerSide = 0.85m, ExchangeFeePerSide = 1.18m, NfaFeePerSide = 0.02m
    };

    public static BrokerProfile Broker() => new()
    {
        BrokerName = "AMP Futures", ExecutionProvider = "Rithmic",
        AccountType = AccountType.Funded, SupportsMarketOrders = true
    };

    public static RiskConfig Risk() => new()
    {
        MaxDailyLoss = 1000m, MaxLossPerTrade = 1000m, MaxTradesPerDay = 10,
        MaxContracts = 5, MaxOpenPositions = 1, MaxOrdersPerMinute = 10,
        MaxConsecutiveLosses = 3, EnforceSession = false
    };

    public static TradeSignal Signal(SignalDirection dir = SignalDirection.Long, string symbol = "NQ") => new()
    {
        StrategyName = "test", Symbol = symbol, Direction = dir,
        Timestamp = Noon, ReferencePrice = 20000m, SuggestedStopLossTicks = 40
    };

    public static RiskEvaluationRequest Request() => new()
    {
        Signal = Signal(),
        RequestedContracts = 1,
        DailyState = DailyRiskState.Start(Date),
        Instrument = Instrument(),
        Fee = Fee(),
        Broker = Broker(),
        Risk = Risk(),
        OpenPositionsCount = 0,
        CurrentOpenContracts = 0,
        PositionReconciled = true
    };
}
