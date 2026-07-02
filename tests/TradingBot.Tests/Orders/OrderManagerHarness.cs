using Moq;
using TradingBot.Application.Orders;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Tests.RiskManagement;

namespace TradingBot.Tests.Orders;

/// <summary>Sammelt ILogger-Ausgaben für Assertions.</summary>
internal sealed class RecordingLogger : ILogger
{
    public List<string> Infos { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();

    public void Info(string message) => Infos.Add(message);
    public void Warning(string message) => Warnings.Add(message);
    public void Error(string message, Exception? exception = null) => Errors.Add(message);
}

/// <summary>Baukasten für OrderManager-Tests mit gemockten Collaborators.</summary>
internal sealed class OrderManagerHarness
{
    public static readonly DateTimeOffset T = new(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);

    public Mock<IOrderContextProvider> Context { get; } = new();
    public Mock<IRiskManager> Risk { get; } = new();
    public Mock<IBrokerExecutionAdapter> Adapter { get; } = new();
    public Mock<IPositionManager> Positions { get; } = new();
    public Mock<IClock> Clock { get; } = new();
    public RecordingLogger Logger { get; } = new();

    public RiskDecision Decision { get; set; } = new()
    {
        Approved = true, RejectionReason = RiskRejectionReason.None,
        Message = "ok", RequestedContracts = 1, ApprovedContracts = 1
    };

    public Func<OrderRequest, OrderResult> OnSubmit { get; set; } =
        r => new OrderResult { OrderId = r.OrderId, Status = OrderStatus.New, Timestamp = T };

    public OrderRequest? LastSubmitted { get; private set; }

    /// <summary>Aktuelle Position, die der PositionManager-Mock zurückgibt (für Intent-Klassifikation).</summary>
    public Position? CurrentPosition { get; set; }

    /// <summary>Der zuletzt an den RiskManager übergebene Request (für Intent-Assertions).</summary>
    public RiskEvaluationRequest? LastRiskRequest { get; private set; }

    public OrderManagerHarness()
    {
        Clock.SetupGet(c => c.UtcNow).Returns(T);

        Context.Setup(c => c.BuildAsync(It.IsAny<TradeSignal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((TradeSignal s, int n, CancellationToken _) => Task.FromResult(RequestFor(s, n)));

        Risk.Setup(r => r.Evaluate(It.IsAny<RiskEvaluationRequest>()))
            .Callback<RiskEvaluationRequest>(r => LastRiskRequest = r)
            .Returns(() => Decision);

        Positions.Setup(p => p.GetPosition(It.IsAny<string>())).Returns(() => CurrentPosition);

        Adapter.Setup(a => a.SubmitOrderAsync(It.IsAny<OrderRequest>(), It.IsAny<CancellationToken>()))
            .Returns((OrderRequest r, CancellationToken _) =>
            {
                LastSubmitted = r;
                return Task.FromResult(OnSubmit(r)); // wirft OnSubmit -> propagiert synchron
            });

        Adapter.Setup(a => a.CancelOrderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken _) =>
                Task.FromResult(new OrderResult { OrderId = id, Status = OrderStatus.Cancelled, Timestamp = T }));

        Adapter.Setup(a => a.ReplaceOrderAsync(It.IsAny<OrderRequest>(), It.IsAny<CancellationToken>()))
            .Returns((OrderRequest r, CancellationToken _) =>
                Task.FromResult(new OrderResult { OrderId = r.OrderId, Status = OrderStatus.New, Timestamp = T }));

        Positions.Setup(p => p.ApplyFill(It.IsAny<FillEvent>(), It.IsAny<InstrumentProfile>(), It.IsAny<FeeProfile>()))
            .Returns(new Position { AccountId = "ACC1", Symbol = "NQ" });
    }

    private static RiskEvaluationRequest RequestFor(TradeSignal s, int n) => new()
    {
        Signal = s,
        RequestedContracts = n,
        DailyState = DailyRiskState.Start(new DateOnly(2026, 6, 23)),
        Instrument = RiskTestData.Instrument(),
        Fee = RiskTestData.Fee(),
        Broker = RiskTestData.Broker(),
        Risk = RiskTestData.Risk(),
        OpenPositionsCount = 0,
        CurrentOpenContracts = 0,
        PositionReconciled = true
    };

    public OrderManager Build() => new(
        Context.Object, Risk.Object, Adapter.Object, Positions.Object,
        Clock.Object, Logger, new TradingAccount { AccountId = "ACC1", BrokerName = "AMP Futures" });

    public TradeSignal Signal(int? qty = null) => new()
    {
        StrategyName = "t", Symbol = "NQ", Direction = SignalDirection.Long,
        Timestamp = T, ReferencePrice = 20000m, SuggestedStopLossTicks = 40, SuggestedQuantity = qty
    };

    public FillEvent Fill(Guid orderId, int qty, decimal price, OrderSide side = OrderSide.Buy) => new()
    {
        OrderId = orderId, Symbol = "NQ", Side = side, Quantity = qty, FillPrice = price, Timestamp = T
    };

    public void RaiseFill(FillEvent fill) => Adapter.Raise(a => a.Filled += null, Adapter.Object, fill);
}
