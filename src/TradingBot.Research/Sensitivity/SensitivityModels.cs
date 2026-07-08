namespace TradingBot.Research.Sensitivity;

public sealed record SlippageSensitivityPoint(decimal SlippageTicks, decimal NetProfit, decimal MaxDrawdown, int TradeCount);

/// <summary>Ergebnis der Slippage-Sensitivität: NetPnL bei steigender Slippage.</summary>
public sealed record SlippageSensitivityResult
{
    public required IReadOnlyList<SlippageSensitivityPoint> Points { get; init; }

    /// <summary>Erste getestete Slippage, bei der NetPnL ≤ 0 wird (null = bleibt profitabel).</summary>
    public decimal? BreakEvenSlippageTicks { get; init; }

    /// <summary>True, wenn die Strategie nur bei perfekten/niedrigen Kosten profitabel ist.</summary>
    public bool FragileToSlippage { get; init; }
}

public sealed record FeeSensitivityPoint(decimal FeeMultiplier, decimal NetProfit, int TradeCount);

/// <summary>Ergebnis der Fee-Sensitivität: NetPnL bei steigenden Gebühren.</summary>
public sealed record FeeSensitivityResult
{
    public required IReadOnlyList<FeeSensitivityPoint> Points { get; init; }
    public decimal? BreakEvenFeeMultiplier { get; init; }
    public bool FragileToFees { get; init; }
}

public sealed record ParameterSensitivityPoint(string Value, decimal NetProfit, int TradeCount);

/// <summary>Ergebnis der Parameter-Sensitivität: NetPnL-Streuung über einen Parameterbereich.</summary>
public sealed record ParameterSensitivityResult
{
    public required string ParameterName { get; init; }
    public required IReadOnlyList<ParameterSensitivityPoint> Points { get; init; }

    public decimal MeanNetProfit { get; init; }
    public decimal StdDevNetProfit { get; init; }

    /// <summary>0..1, höher = stabiler (= 1/(1+Variationskoeffizient)).</summary>
    public decimal Stability { get; init; }
}
