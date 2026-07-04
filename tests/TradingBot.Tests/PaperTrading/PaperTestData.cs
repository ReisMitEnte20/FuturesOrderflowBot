using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.MarketData;
using TradingBot.PaperTrading;
using TradingBot.Tests.Backtesting;

namespace TradingBot.Tests.PaperTrading;

/// <summary>Zählt Strategie-Aufrufe (für Pause/Resume-Tests). Reines Durchreichen.</summary>
internal sealed class CountingStrategy : IStrategy
{
    private readonly IStrategy _inner;
    private int _tickCalls;

    public CountingStrategy(IStrategy inner) => _inner = inner;

    public int TickCalls => _tickCalls;
    public string Name => _inner.Name;
    public void Initialize(StrategyExecutionContext context) => _inner.Initialize(context);
    public TradeSignal? OnCandle(Candle candle) => _inner.OnCandle(candle);
    public TradeSignal? OnOrderFlowBar(OrderFlowBar bar) => _inner.OnOrderFlowBar(bar);
    public void Reset() => _inner.Reset();

    public TradeSignal? OnTick(MarketTick tick)
    {
        Interlocked.Increment(ref _tickCalls);
        return _inner.OnTick(tick);
    }
}

/// <summary>Testdaten/Helfer für Paper-Trading-Tests. Profile kommen aus BacktestTestData (NQ).</summary>
internal static class PaperTestData
{
    public static PaperTradingRequest Request(
        IStrategy strategy, IEnumerable<MarketTick> ticks,
        ReplayOptions? replay = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        PaperTradingConfiguration? config = null,
        IKillSwitchService? killSwitch = null, ISafetyMonitor? safety = null,
        RiskConfig? risk = null, bool includeRisk = true,
        InstrumentProfile? instrument = null, bool includeInstrument = true,
        FeeProfile? fee = null, bool includeFee = true,
        Func<OrderRequest, bool>? rejectOrder = null) => new()
        {
            MarketData = new ReplayMarketDataProvider(ticks, replay ?? ReplayOptions.Fast, delay),
            Symbol = "NQ",
            Strategy = strategy,
            Account = BacktestTestData.Account(),
            Instrument = includeInstrument ? (instrument ?? BacktestTestData.Instrument()) : null,
            Fee = includeFee ? (fee ?? BacktestTestData.Fee()) : null,
            Broker = BacktestTestData.Broker(),
            Risk = includeRisk ? (risk ?? BacktestTestData.Risk()) : null,
            Config = config ?? new PaperTradingConfiguration(),
            KillSwitch = killSwitch,
            Safety = safety,
            RejectOrder = rejectOrder
        };

    /// <summary>Wartet aktiv, bis eine Bedingung erfüllt ist (Timeout → aussagekräftiger Fehler).</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        long start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs)
                throw new TimeoutException($"Bedingung nicht erreicht: {what}");
            await Task.Delay(10);
        }
    }
}
