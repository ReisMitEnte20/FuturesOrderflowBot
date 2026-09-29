using FluentAssertions;
using TradingBot.Quant.Overfitting;
using TradingBot.Quant.Statistics;

namespace TradingBot.Tests.Quant;

/// <summary>
/// Referenztests der Statistikbausteine gegen VON HAND berechnete Sollwerte bzw. gegen
/// allgemein bekannte Tabellenwerte. Die Erwartungswerte stammen ausdrücklich NICHT aus
/// derselben Implementierung.
/// </summary>
public class QuantStatsReferenceTests
{
    // Referenzreihe x = [1,2,3,4,5]
    //   Mittelwert       = 15/5 = 3
    //   Σ(x−x̄)²         = 4+1+0+1+4 = 10
    //   Stichproben-SD    = √(10/4) = √2,5
    //   Populations-SD    = √(10/5) = √2
    //   m2 = 10/5 = 2 ; m4 = (16+1+0+1+16)/5 = 6,8 ; g2 = 6,8/4 = 1,7
    private static readonly double[] Ref = { 1, 2, 3, 4, 5 };

    [Fact]
    public void Mean_and_standard_deviations_match_hand_computed_values()
    {
        Stats.Mean(Ref).Should().Be(3.0);
        Stats.StdDevSample(Ref)!.Value.Should().BeApproximately(Math.Sqrt(2.5), 1e-12);
        Stats.StdDevPopulation(Ref)!.Value.Should().BeApproximately(Math.Sqrt(2.0), 1e-12);
    }

    [Fact]
    public void Skewness_is_zero_for_symmetric_data_and_kurtosis_matches_hand_value()
    {
        Stats.Skewness(Ref)!.Value.Should().BeApproximately(0.0, 1e-12);
        Stats.Kurtosis(Ref)!.Value.Should().BeApproximately(1.7, 1e-12);
    }

    [Fact]
    public void Percentile_uses_linear_interpolation_between_ranks()
    {
        // Rang = p·(n−1). p=0,25 → Rang 1 → exakt x[1] = 2
        Stats.Percentile(Ref, 0.25).Should().BeApproximately(2.0, 1e-12);
        // p=0,10 → Rang 0,4 → 1 + 0,4·(2−1) = 1,4
        Stats.Percentile(Ref, 0.10).Should().BeApproximately(1.4, 1e-12);
        Stats.Median(Ref).Should().Be(3.0);
    }

    [Fact]
    public void Small_samples_return_null_instead_of_a_substitute_value()
    {
        Stats.StdDevSample(new[] { 1.0 }).Should().BeNull();
        Stats.Skewness(new[] { 1.0, 2.0 }).Should().BeNull();
        Stats.Kurtosis(new[] { 1.0, 2.0, 3.0 }).Should().BeNull();
        Stats.Correlation(new[] { 1.0, 2.0 }, new[] { 1.0 }).Should().BeNull();
        // Konstante Reihe: Korrelation nicht definiert
        Stats.Correlation(new[] { 1.0, 1.0, 1.0 }, new[] { 1.0, 2.0, 3.0 }).Should().BeNull();
    }

    [Fact]
    public void Correlation_is_one_for_a_positive_linear_relation()
        => Stats.Correlation(new[] { 1.0, 2.0, 3.0 }, new[] { 2.0, 4.0, 6.0 })!.Value
            .Should().BeApproximately(1.0, 1e-12);

    [Theory]
    // Tabellenwerte der Standardnormalverteilung
    [InlineData(0.0, 0.5)]
    [InlineData(1.0, 0.8413447461)]
    [InlineData(-1.0, 0.1586552539)]
    [InlineData(1.959963985, 0.975)]
    [InlineData(2.5758293035, 0.995)]
    public void NormalCdf_matches_published_table_values(double z, double expected)
        => Stats.NormalCdf(z).Should().BeApproximately(expected, 1e-7);

    [Theory]
    [InlineData(0.5, 0.0)]
    [InlineData(0.975, 1.959963985)]
    [InlineData(0.995, 2.5758293035)]
    [InlineData(0.05, -1.644853627)]
    public void NormalInverseCdf_matches_published_table_values(double p, double expected)
        => Stats.NormalInverseCdf(p).Should().BeApproximately(expected, 1e-6);

    [Fact]
    public void NormalCdf_and_its_inverse_are_consistent()
    {
        foreach (var p in new[] { 0.01, 0.1, 0.3, 0.5, 0.7, 0.9, 0.99 })
            Stats.NormalCdf(Stats.NormalInverseCdf(p)).Should().BeApproximately(p, 1e-6);
    }

    // ---------------------------------------------------------------------------------------
    // PSR gegen eine vollständig von Hand gerechnete Referenz.
    //
    // x = [1,1,1,1,1,1,1,−3]  (n = 8)
    //   Mittelwert  = 4/8 = 0,5
    //   Σ(x−x̄)²    = 7·0,25 + 12,25 = 14   → Stichprobenvarianz 14/7 = 2 → SD = √2
    //   SR̂          = 0,5/√2 = 0,3535533906
    //   m2 = 14/8 = 1,75 ; m3 = (7·0,125 − 42,875)/8 = −5,25 ; m4 = (7·0,0625 + 150,0625)/8 = 18,8125
    //   γ₃ = −5,25 / 1,75^1,5 = −2,2677868380
    //   γ₄ = 18,8125 / 1,75²  = 6,1428571429
    //   Varianzterm = 1 − γ₃·SR̂ + (γ₄−1)/4·SR̂² = 1 + 0,8017837 + 0,1607143 = 1,9624980
    //   z = SR̂·√(n−1)/√Varianzterm = 0,3535534·2,6457513/1,4008919 = 0,6677297
    //   PSR = Φ(0,6677297) ≈ 0,74784
    // ---------------------------------------------------------------------------------------
    private static readonly double[] PsrRef = { 1, 1, 1, 1, 1, 1, 1, -3 };

    [Fact]
    public void Psr_moments_match_hand_computed_reference()
    {
        Stats.Mean(PsrRef).Should().Be(0.5);
        Stats.StdDevSample(PsrRef)!.Value.Should().BeApproximately(Math.Sqrt(2.0), 1e-12);
        Stats.Skewness(PsrRef)!.Value.Should().BeApproximately(-2.2677868380, 1e-9);
        Stats.Kurtosis(PsrRef)!.Value.Should().BeApproximately(6.1428571429, 1e-9);
    }

    [Fact]
    public void Psr_matches_hand_computed_reference()
    {
        var psr = ProbabilisticSharpe.Compute(PsrRef);

        psr.Available.Should().BeTrue();
        psr.ObservedSharpePerPeriod!.Value.Should().BeApproximately(0.3535533906, 1e-9);
        psr.Psr!.Value.Should().BeApproximately(0.74784, 1e-4);
    }

    [Fact]
    public void Psr_is_exactly_one_half_when_observed_sharpe_equals_the_threshold()
    {
        // Mittelwert 0 → SR̂ = 0 → Zähler 0 → Φ(0) = 0,5. Unabhängig von Schiefe/Wölbung.
        var psr = ProbabilisticSharpe.Compute(new double[] { 2, -2, 1, -1, 3, -3, 0, 0 });
        psr.ObservedSharpePerPeriod!.Value.Should().BeApproximately(0.0, 1e-12);
        psr.Psr!.Value.Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void Psr_falls_when_the_threshold_is_raised()
    {
        double low = ProbabilisticSharpe.Compute(PsrRef, 0.0).Psr!.Value;
        double high = ProbabilisticSharpe.Compute(PsrRef, 0.3).Psr!.Value;
        high.Should().BeLessThan(low);
    }

    [Fact]
    public void Psr_is_not_computable_for_constant_returns_or_tiny_samples()
    {
        var constant = ProbabilisticSharpe.Compute(new[] { 0.01, 0.01, 0.01, 0.01, 0.01 });
        constant.Available.Should().BeFalse();
        constant.UnavailableReason.Should().Contain("Standardabweichung");

        var tiny = ProbabilisticSharpe.Compute(new[] { 0.01, -0.02, 0.03 });
        tiny.Available.Should().BeFalse();
        tiny.UnavailableReason.Should().Contain("Beobachtungen");
    }

    [Fact]
    public void Deflated_sharpe_gets_stricter_as_the_number_of_trials_grows()
    {
        var selected = new[] { 0.02, 0.01, -0.005, 0.03, 0.012, -0.002, 0.018, 0.004, 0.011, -0.001, 0.02, 0.006 };
        var trials = new[] { 0.10, 0.05, -0.02, 0.22, 0.08, -0.11, 0.03, 0.14 };

        var few = ProbabilisticSharpe.ComputeDeflated(selected, trials, effectiveTrials: 3,
            effectiveTrialsRationale: "Test: 3 unabhängige Versuche angenommen.");
        var many = ProbabilisticSharpe.ComputeDeflated(selected, trials, effectiveTrials: 500,
            effectiveTrialsRationale: "Test: 500 unabhängige Versuche angenommen.");

        few.Available.Should().BeTrue();
        many.Available.Should().BeTrue();
        // Mehr Versuche → höherer erwarteter Maximal-Sharpe unter der Nullhypothese → kleinerer DSR.
        many.ExpectedMaxSharpeUnderNull!.Value.Should().BeGreaterThan(few.ExpectedMaxSharpeUnderNull!.Value);
        many.Dsr!.Value.Should().BeLessThan(few.Dsr!.Value);
    }

    [Fact]
    public void Deflated_sharpe_reports_missing_basis_instead_of_guessing()
    {
        var selected = new[] { 0.01, 0.02, -0.01, 0.03, 0.00, 0.015 };

        // Alle Versuche identisch → Var(SR_n) = 0 → SR*₀ nicht bestimmbar.
        var identical = ProbabilisticSharpe.ComputeDeflated(selected, new[] { 0.1, 0.1, 0.1, 0.1 });
        identical.Available.Should().BeFalse();
        identical.UnavailableReason.Should().Contain("Var(SR_n)");

        // Ein einziger Versuch → keine Verteilung.
        var single = ProbabilisticSharpe.ComputeDeflated(selected, new[] { 0.1 });
        single.Available.Should().BeFalse();
    }

    [Fact]
    public void Effective_trial_count_shrinks_when_candidates_are_correlated()
    {
        var baseSeries = Enumerable.Range(0, 60).Select(i => Math.Sin(i * 0.3)).ToArray();
        // Zehn nahezu identische Varianten sind KEINE zehn unabhängigen Versuche.
        var correlated = Enumerable.Range(0, 10)
            .Select(k => (IReadOnlyList<double>)baseSeries.Select((v, i) => v + k * 1e-6 * i).ToArray())
            .ToList();

        var (eff, rationale) = ProbabilisticSharpe.EstimateEffectiveTrials(correlated);

        eff.Should().BeLessThan(2.0);
        eff.Should().BeGreaterThanOrEqualTo(1.0);
        rationale.Should().Contain("Heuristik");
    }
}
