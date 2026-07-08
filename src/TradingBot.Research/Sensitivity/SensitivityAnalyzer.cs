using TradingBot.Domain.Models;
using TradingBot.Research.Runner;

namespace TradingBot.Research.Sensitivity;

/// <summary>
/// Kosten- und Parameter-Sensitivität. Bewertet Performance IMMER NetPnL-basiert NACH Kosten:
/// höhere Slippage/Fees werden durch ECHTE Re-Runs des Backtests getestet (nicht approximiert),
/// damit auch der Preiseinfluss der Slippage korrekt einfließt. Warnt, wenn eine Strategie nur
/// bei perfekten Kosten profitabel ist.
/// </summary>
public sealed class SensitivityAnalyzer
{
    private readonly IStrategyBacktestRunner _runner;

    public SensitivityAnalyzer(IStrategyBacktestRunner runner)
        => _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public async Task<SlippageSensitivityResult> AnalyzeSlippageAsync(
        StrategyRunInputs template, IReadOnlyList<decimal> slippageTickLevels, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        var points = new List<SlippageSensitivityPoint>();
        decimal? breakEven = null;

        foreach (var slip in slippageTickLevels.OrderBy(s => s))
        {
            var run = await _runner.RunAsync(template with { SlippageTicksOverride = slip }, ct).ConfigureAwait(false);
            var net = run.Metrics.NetProfit;
            points.Add(new SlippageSensitivityPoint(slip, net, run.Metrics.MaxDrawdown, run.Metrics.TradeCount));
            if (breakEven is null && net <= 0m) breakEven = slip;
        }

        decimal baseNet = points.Count > 0 ? points[0].NetProfit : 0m;
        bool fragile = baseNet > 0m && breakEven is not null;
        return new SlippageSensitivityResult { Points = points, BreakEvenSlippageTicks = breakEven, FragileToSlippage = fragile };
    }

    public async Task<FeeSensitivityResult> AnalyzeFeesAsync(
        StrategyRunInputs template, IReadOnlyList<decimal> feeMultipliers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        var points = new List<FeeSensitivityPoint>();
        decimal? breakEven = null;

        foreach (var mult in feeMultipliers.OrderBy(m => m))
        {
            var run = await _runner.RunAsync(template with { Fee = ScaleFees(template.Fee, mult) }, ct).ConfigureAwait(false);
            var net = run.Metrics.NetProfit;
            points.Add(new FeeSensitivityPoint(mult, net, run.Metrics.TradeCount));
            if (breakEven is null && net <= 0m) breakEven = mult;
        }

        decimal baseNet = points.FirstOrDefault(p => p.FeeMultiplier == 1m)?.NetProfit ?? (points.Count > 0 ? points[0].NetProfit : 0m);
        bool fragile = baseNet > 0m && breakEven is not null;
        return new FeeSensitivityResult { Points = points, BreakEvenFeeMultiplier = breakEven, FragileToFees = fragile };
    }

    public async Task<ParameterSensitivityResult> AnalyzeParameterAsync(
        StrategyCandidate candidate, StrategyRunInputs template, string parameterName,
        IReadOnlyList<string> values, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(template);
        var points = new List<ParameterSensitivityPoint>();

        foreach (var value in values)
        {
            var merged = new Dictionary<string, string>(candidate.BaseConfig.Parameters, StringComparer.OrdinalIgnoreCase)
            {
                [parameterName] = value
            };
            var config = candidate.BaseConfig with { Parameters = merged };
            var run = await _runner.RunAsync(template with { Candidate = candidate, Config = config }, ct).ConfigureAwait(false);
            points.Add(new ParameterSensitivityPoint(value, run.Metrics.NetProfit, run.Metrics.TradeCount));
        }

        var nets = points.Select(p => p.NetProfit).ToList();
        decimal mean = nets.Count > 0 ? nets.Average() : 0m;
        decimal variance = nets.Count > 0 ? nets.Average(n => (n - mean) * (n - mean)) : 0m;
        decimal std = (decimal)Math.Sqrt((double)variance);
        decimal cv = mean != 0m ? std / Math.Abs(mean) : (std == 0m ? 0m : decimal.MaxValue);
        decimal stability = cv == decimal.MaxValue ? 0m : 1m / (1m + cv);

        return new ParameterSensitivityResult
        {
            ParameterName = parameterName, Points = points,
            MeanNetProfit = mean, StdDevNetProfit = std, Stability = stability
        };
    }

    private static FeeProfile ScaleFees(FeeProfile fee, decimal multiplier) => fee with
    {
        CommissionPerSide = fee.CommissionPerSide * multiplier,
        ExchangeFeePerSide = fee.ExchangeFeePerSide * multiplier,
        ClearingFeePerSide = fee.ClearingFeePerSide * multiplier,
        RoutingFeePerSide = fee.RoutingFeePerSide * multiplier,
        NfaFeePerSide = fee.NfaFeePerSide * multiplier,
        OtherFeePerSide = fee.OtherFeePerSide * multiplier
    };
}
