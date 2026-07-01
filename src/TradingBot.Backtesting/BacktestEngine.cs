using TradingBot.Application.Fees;
using TradingBot.Application.MarketData;
using TradingBot.Application.Orders;
using TradingBot.Application.Pnl;
using TradingBot.Application.Positions;
using TradingBot.Application.Risk;
using TradingBot.Backtesting.Execution;
using TradingBot.Backtesting.Positions;
using TradingBot.Backtesting.Risk;
using TradingBot.Backtesting.Time;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;

namespace TradingBot.Backtesting;

/// <summary>
/// Deterministische Backtest-Engine. Verdrahtet die reale Pipeline (Strategy → RiskManager →
/// OrderManager → PositionManager) mit einem SIMULIERTEN Execution-Adapter und einem
/// Marktdaten-Replay. Sendet niemals echte Orders (kein Live-Adapter, keine Netzwerkverbindung,
/// kein LiveTradingMode).
///
/// Ablauf pro Tick (Reihenfolge ist sicherheitsrelevant):
///   1. Uhr auf Tick-Zeit setzen  2. offene Orders gegen den Tick füllen (Fills VOR Strategie
///   → Market füllt am Folge-Tick, kein Lookahead)  3. Strategie auswerten  4. Signal über den
///   OrderManager (Risk-Prüfung, Order) einreichen.
/// </summary>
public sealed class BacktestEngine : IBacktestEngine
{
    public async Task<BacktestResult> RunAsync(BacktestRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Config.FillDataMode == FillDataMode.Candle)
            return new BacktestResult
            {
                Status = BacktestRunStatus.Failed,
                Message = "Candle-Fill-Modus wird nicht unterstützt – für seriöse Fills sind Tickdaten erforderlich."
            };

        // ---- Verdrahtung (alles in-memory, deterministisch) ----
        var clock = new SimulatedClock(DateTimeOffset.MinValue);
        var feeCalc = new FeeCalculator();
        var pnlCalc = new PnLCalculator(feeCalc);
        var innerPm = new PositionManager(feeCalc, pnlCalc);
        var positions = new RecordingPositionManager(innerPm);

        var dailyState = new BacktestDailyStateProvider(request.Risk.MaxDailyLoss);
        positions.TradeClosed = dailyState.ApplyTrade;

        var killSwitch = request.KillSwitch ?? new BacktestKillSwitch();
        var safety = request.Safety ?? new BacktestSafetyMonitor();
        var riskManager = new RiskManager(killSwitch, safety, clock);

        decimal slippageTicks = request.Config.SlippageTicksOverride ?? request.Fee.EstimatedSlippageTicks;
        var fillModel = new FillModel();
        var adapter = new BacktestExecutionAdapter(fillModel, request.Instrument, slippageTicks, request.RejectOrder);

        var context = new BacktestOrderContextProvider(
            request.Instrument, request.Fee, request.Broker, request.Risk, dailyState, positions, clock);

        var feedHealth = new FeedHealthMonitor(clock, TimeSpan.FromHours(1)); // informativ, nicht gating

        using var orderManager = new OrderManager(
            context, riskManager, adapter, positions, clock, SilentLogger.Instance, request.Account);

        int ticks = 0, signals = 0, submitted = 0, riskRejected = 0, brokerRejected = 0;
        bool cancelled = false;

        try
        {
            await adapter.ConnectAsync(cancellationToken).ConfigureAwait(false);
            feedHealth.SetConnected(true);
            await request.MarketData.ConnectAsync(cancellationToken).ConfigureAwait(false);

            await foreach (var tick in request.MarketData
                .SubscribeTicksAsync(request.Symbol, cancellationToken).ConfigureAwait(false))
            {
                if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }

                clock.Set(tick.Timestamp);
                feedHealth.RecordTick(tick);
                adapter.ProcessTick(tick);          // Fills offener Orders VOR der Strategie
                ticks++;

                var signal = request.Strategy.OnTick(tick);
                if (signal is null) continue;
                signals++;

                var state = await orderManager.ProcessSignalAsync(signal, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (state is null)
                {
                    if (orderManager.LastDecision is { Approved: false }) riskRejected++;
                }
                else if (state.Lifecycle == OrderLifecycleState.Rejected)
                {
                    brokerRejected++;
                }
                else
                {
                    submitted++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            return new BacktestResult
            {
                Status = BacktestRunStatus.Failed,
                Message = ex.Message,
                TicksProcessed = ticks,
                SignalsGenerated = signals
            };
        }

        cancelled = cancelled || cancellationToken.IsCancellationRequested;

        var stats = BacktestStatisticsCalculator.Compute(positions.Trades, adapter.TotalSlippageCost);

        return new BacktestResult
        {
            Status = cancelled ? BacktestRunStatus.Cancelled : BacktestRunStatus.Completed,
            Statistics = stats,
            Trades = positions.Trades.ToList(),
            TicksProcessed = ticks,
            SignalsGenerated = signals,
            OrdersSubmitted = submitted,
            OrdersRejectedByRisk = riskRejected,
            OrdersRejectedByBroker = brokerRejected,
            OrdersUnfilledAtEnd = adapter.PendingCount,
            FinalFeedState = feedHealth.State
        };
    }

    /// <summary>Stiller ILogger für den Backtest (keine Diagnose-Ausgabe nötig).</summary>
    private sealed class SilentLogger : ILogger
    {
        public static readonly SilentLogger Instance = new();
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
