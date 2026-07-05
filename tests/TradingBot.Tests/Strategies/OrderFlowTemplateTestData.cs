using TradingBot.Domain.Models;
using TradingBot.Tests.Backtesting;

namespace TradingBot.Tests.Strategies;

/// <summary>Baut ECHT klassifizierte OrderFlowBars (Bid+Ask = TotalVolume) für Template-Tests.</summary>
internal static class OrderFlowTemplateTestData
{
    public static readonly DateTimeOffset T0 = BacktestTestData.T0;

    public static OrderFlowBar Bar(
        int i, decimal open, decimal high, decimal low, decimal close,
        decimal bidVolume, decimal askVolume, decimal cumulativeDelta) => new()
    {
        Symbol = "NQ",
        OpenTime = T0.AddMinutes(i),
        CloseTime = T0.AddMinutes(i + 1),
        Open = open, High = high, Low = low, Close = close,
        TotalVolume = bidVolume + askVolume,
        BidVolume = bidVolume,
        AskVolume = askVolume,
        CumulativeDelta = cumulativeDelta
    };

    /// <summary>
    /// Deterministisches Long-Setup (3 Bars):
    /// Bar3 macht neues Tief mit Sweep, dreht bullisch mit Delta +100, Volumen-Spike und Ask-Imbalance.
    /// Erwartete Met-Confirmations (Defaults): DeltaDivergence, LiquiditySweep, VolumeSpike,
    /// ReversalConfirmation, BarImbalance, CvdConfirmation = 6 von 8.
    /// </summary>
    public static List<OrderFlowBar> LongSetupBars() => new()
    {
        Bar(0, 20000m, 20010m, 19990m, 19995m, bidVolume: 60m, askVolume: 40m, cumulativeDelta: -20m),
        Bar(1, 19995m, 20000m, 19985m, 19990m, bidVolume: 60m, askVolume: 40m, cumulativeDelta: -40m),
        Bar(2, 19990m, 20000m, 19980m, 19998m, bidVolume: 100m, askVolume: 200m, cumulativeDelta: 60m)
    };

    /// <summary>Bar mit Volumen, aber OHNE echte Bid/Ask-Klassifikation (verboten für Orderflow).</summary>
    public static OrderFlowBar UnclassifiedBar(int i = 0) => new()
    {
        Symbol = "NQ",
        OpenTime = T0.AddMinutes(i),
        CloseTime = T0.AddMinutes(i + 1),
        Open = 20000m, High = 20010m, Low = 19990m, Close = 20005m,
        TotalVolume = 100m, BidVolume = 0m, AskVolume = 0m
    };

    public static StrategyExecutionContext Context(StrategyConfig? config = null) => new()
    {
        Symbol = "NQ",
        Instrument = BacktestTestData.Instrument(), // NQ: TickSize 0.25 (aus Test-Profil, nicht hardcoded im Code)
        Config = config
    };

    public static StrategyConfig TemplateConfig(
        Dictionary<string, string>? parameters = null, bool enabled = true, int suggestedContracts = 1) => new()
    {
        Name = "OrderFlowTemplate",
        Symbol = "NQ",
        Enabled = enabled,
        RequiredDataType = TradingBot.Domain.Enums.StrategyDataType.OrderFlow,
        SuggestedContracts = suggestedContracts,
        StopLossTicks = 40,
        TakeProfitTicks = 60,
        Parameters = parameters ?? new Dictionary<string, string>()
    };
}
