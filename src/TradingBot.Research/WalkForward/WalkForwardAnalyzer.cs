using TradingBot.Domain.Models;
using TradingBot.Research.Runner;

namespace TradingBot.Research.WalkForward;

/// <summary>
/// Walk-Forward-Analyse: teilt die Daten in disjunkte IS/OOS-Fenster. Pro Fenster wird AUF IS
/// die beste Config selektiert und dann auf dem NIE gesehenen OOS getestet. OOS-Ergebnisse sind
/// strikt von IS getrennt gespeichert. Reduziert Overfitting, weil eine Strategie, die nur auf
/// IS gut aussieht, auf OOS entlarvt wird (Walk-Forward-Efficiency &amp; Overfitting-Warnung).
/// Nutzt den <see cref="IStrategyBacktestRunner"/> – sendet keine echten Orders.
/// </summary>
public sealed class WalkForwardAnalyzer
{
    private readonly IStrategyBacktestRunner _runner;

    public WalkForwardAnalyzer(IStrategyBacktestRunner runner)
        => _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public async Task<WalkForwardResult> AnalyzeAsync(
        WalkForwardRequest request, StrategyRunInputs template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(template);
        if (request.SelectionConfigs.Count == 0)
            throw new ArgumentException("Mindestens eine Selektions-Config nötig.", nameof(request));

        var ticks = template.Ticks;
        var windows = WalkForwardWindows.Generate(
            ticks.Count, request.InSampleSize, request.OutOfSampleSize, request.Step, request.Mode);

        var segments = new List<WalkForwardSegmentResult>();

        foreach (var w in windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isTicks = Slice(ticks, w.InSampleStart, w.InSampleEnd);
            var oosTicks = Slice(ticks, w.OutOfSampleStart, w.OutOfSampleEnd);

            // Selektion NUR auf In-Sample.
            StrategyRunResult? best = null;
            foreach (var config in request.SelectionConfigs)
            {
                var isRun = await _runner.RunAsync(
                    template with { Candidate = request.Candidate, Config = config, Ticks = isTicks }, cancellationToken)
                    .ConfigureAwait(false);
                if (best is null || isRun.Metrics.NetProfit > best.Metrics.NetProfit)
                    best = isRun;
            }

            // Test der selektierten Config auf Out-of-Sample.
            var oosRun = await _runner.RunAsync(
                template with { Candidate = request.Candidate, Config = best!.Config, Ticks = oosTicks }, cancellationToken)
                .ConfigureAwait(false);

            segments.Add(new WalkForwardSegmentResult
            {
                Window = w,
                SelectedConfig = best.Config,
                InSample = best.Metrics,
                OutOfSample = oosRun.Metrics
            });
        }

        return Aggregate(segments, request.EfficiencyWarnThreshold);
    }

    private static WalkForwardResult Aggregate(
        IReadOnlyList<WalkForwardSegmentResult> segments, decimal warnThreshold)
    {
        decimal isNet = segments.Sum(s => s.InSample.NetProfit);
        decimal oosNet = segments.Sum(s => s.OutOfSample.NetProfit);
        int isTrades = segments.Sum(s => s.InSample.TradeCount);
        int oosTrades = segments.Sum(s => s.OutOfSample.TradeCount);

        decimal? wfe = null;
        if (isTrades > 0 && oosTrades > 0)
        {
            decimal isPerTrade = isNet / isTrades;
            decimal oosPerTrade = oosNet / oosTrades;
            if (isPerTrade > 0m) wfe = oosPerTrade / isPerTrade;
        }

        bool overfit = (isNet > 0m && oosNet <= 0m) || (wfe is decimal e && e < warnThreshold);
        string? note = overfit
            ? $"IS NetPnL {isNet:F2} vs OOS NetPnL {oosNet:F2}" +
              (wfe is decimal w ? $", WFE {w:F2}" : "") + " – Overfitting-Verdacht."
            : null;

        return new WalkForwardResult
        {
            Segments = segments,
            InSampleNetProfit = isNet,
            OutOfSampleNetProfit = oosNet,
            InSampleTrades = isTrades,
            OutOfSampleTrades = oosTrades,
            WalkForwardEfficiency = wfe,
            OverfittingSuspected = overfit,
            OverfittingNote = note
        };
    }

    private static IReadOnlyList<MarketTick> Slice(IReadOnlyList<MarketTick> ticks, int start, int end)
    {
        var slice = new List<MarketTick>(end - start);
        for (int i = start; i < end; i++) slice.Add(ticks[i]);
        return slice;
    }
}
