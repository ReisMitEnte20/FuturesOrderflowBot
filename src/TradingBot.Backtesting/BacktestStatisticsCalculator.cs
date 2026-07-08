namespace TradingBot.Backtesting;

/// <summary>
/// Berechnet die Backtest-Kennzahlen aus abgeschlossenen Trades. Rein und deterministisch.
/// GrossProfit/NetProfit strikt getrennt; Profit Factor/Streaks/Drawdown auf NetPnL.
/// <paramref name="totalSlippage"/> ist informativ (schon in den Fill-Preisen enthalten).
/// </summary>
public static class BacktestStatisticsCalculator
{
    public static BacktestStatistics Compute(IReadOnlyList<BacktestTrade> trades, decimal totalSlippage)
    {
        ArgumentNullException.ThrowIfNull(trades);
        if (trades.Count == 0)
            return BacktestStatistics.Empty with { TotalSlippage = totalSlippage };

        int wins = 0, losses = 0, breakEven = 0;
        decimal grossProfit = 0m, netProfit = 0m, totalFees = 0m;
        decimal winNetSum = 0m, lossNetSum = 0m;
        decimal largestWin = 0m, largestLoss = 0m;

        int curWin = 0, curLoss = 0, maxWin = 0, maxLoss = 0;
        decimal running = 0m, peak = 0m, maxDrawdown = 0m;

        foreach (var t in trades)
        {
            grossProfit += t.GrossPnL;
            netProfit += t.NetPnL;
            totalFees += t.Fees;

            if (t.IsWinner)
            {
                wins++;
                winNetSum += t.NetPnL;
                if (t.NetPnL > largestWin) largestWin = t.NetPnL;
                curWin++; curLoss = 0;
            }
            else if (t.IsLoser)
            {
                losses++;
                lossNetSum += t.NetPnL; // negativ
                if (t.NetPnL < largestLoss) largestLoss = t.NetPnL;
                curLoss++; curWin = 0;
            }
            else
            {
                breakEven++;
                curWin = 0; curLoss = 0;
            }

            if (curWin > maxWin) maxWin = curWin;
            if (curLoss > maxLoss) maxLoss = curLoss;

            running += t.NetPnL;
            if (running > peak) peak = running;
            var dd = peak - running;
            if (dd > maxDrawdown) maxDrawdown = dd;
        }

        int total = trades.Count;
        decimal? profitFactor = lossNetSum == 0m ? null : winNetSum / Math.Abs(lossNetSum);

        return new BacktestStatistics
        {
            TotalTrades = total,
            WinningTrades = wins,
            LosingTrades = losses,
            BreakEvenTrades = breakEven,
            GrossProfit = grossProfit,
            NetProfit = netProfit,
            TotalFees = totalFees,
            TotalSlippage = totalSlippage,
            MaxDrawdown = maxDrawdown,
            ProfitFactor = profitFactor,
            WinRate = (decimal)wins / total,
            AverageWinner = wins > 0 ? winNetSum / wins : 0m,
            AverageLoser = losses > 0 ? lossNetSum / losses : 0m,
            Expectancy = netProfit / total,
            AverageNetPnLPerTrade = netProfit / total,
            MaxWinningStreak = maxWin,
            MaxLosingStreak = maxLoss,
            TradesPerDay = ComputeTradesPerDay(trades, total),
            LargestWin = largestWin,
            LargestLoss = largestLoss
        };
    }

    private static decimal ComputeTradesPerDay(IReadOnlyList<BacktestTrade> trades, int total)
    {
        int distinctDays = trades.Select(t => DateOnly.FromDateTime(t.ExitTime.UtcDateTime)).Distinct().Count();
        return distinctDays == 0 ? 0m : (decimal)total / distinctDays;
    }

    /// <summary>
    /// Maximaler Drawdown der realisierten Equity-Kurve (kumulativer NetPnL). Startpeak = 0,
    /// d. h. eine anfängliche Verlustserie zählt als Drawdown ab 0. Identisch zur Berechnung in
    /// <see cref="Compute"/> – gemeinsame Definition für Backtest UND Research (Monte Carlo).
    /// Rückgabe ≥ 0.
    /// </summary>
    public static decimal MaxDrawdown(IReadOnlyList<decimal> netPnls)
    {
        ArgumentNullException.ThrowIfNull(netPnls);
        decimal running = 0m, peak = 0m, maxDd = 0m;
        foreach (var pnl in netPnls)
        {
            running += pnl;
            if (running > peak) peak = running;
            var dd = peak - running;
            if (dd > maxDd) maxDd = dd;
        }
        return maxDd;
    }
}
