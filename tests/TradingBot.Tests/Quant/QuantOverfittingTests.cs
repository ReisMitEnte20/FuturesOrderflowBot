using FluentAssertions;
using TradingBot.Quant.MonteCarlo;
using TradingBot.Quant.Overfitting;

namespace TradingBot.Tests.Quant;

/// <summary>
/// Tests der PBO-Schätzung über CSCV. Geprüft werden ein synthetischer NULLFALL (kein echter
/// Vorteil), ein kontrollierter ECHTER Effekt sowie die schwierigen Randfälle: identische
/// Kandidaten, konstante Renditen, zu kleine Stichproben und ungültige Werte.
/// </summary>
public class QuantOverfittingTests
{
    /// <summary>Standardnormalverteilte Zufallszahlen über Box-Muller — reproduzierbar über den Seed.</summary>
    private static double[] Normal(DeterministicRng rng, int n, double mean = 0, double sd = 1)
    {
        var outp = new double[n];
        for (int i = 0; i < n; i++)
        {
            double u1 = Math.Max(rng.NextDouble(), 1e-12);
            double u2 = rng.NextDouble();
            outp[i] = mean + sd * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
        return outp;
    }

    private static List<IReadOnlyList<double>> ToMatrix(IReadOnlyList<double[]> columns)
    {
        int t = columns[0].Length;
        var rows = new List<IReadOnlyList<double>>(t);
        for (int i = 0; i < t; i++)
            rows.Add(columns.Select(c => c[i]).ToArray());
        return rows;
    }

    [Fact]
    public void Mid_rank_shares_the_rank_between_ties()
    {
        // Werte [5,5,1]: die beiden Fünfen belegen die Ränge 2 und 3 → mittlerer Rang 2,5
        CscvPbo.MidRank(new[] { 5.0, 5.0, 1.0 }, 0).Should().Be(2.5);
        CscvPbo.MidRank(new[] { 5.0, 5.0, 1.0 }, 2).Should().Be(1.0);
    }

    [Fact]
    public void Combination_generator_produces_all_subsets_exactly_once()
    {
        var combos = CscvPbo.Combinations(6, 3).ToList();
        combos.Should().HaveCount(20);                                   // C(6,3)
        combos.Select(c => string.Join(",", c)).Distinct().Should().HaveCount(20);
        combos[0].Should().Equal(0, 1, 2);
        combos[^1].Should().Equal(3, 4, 5);
    }

    [Fact]
    public void Null_case_without_any_real_edge_yields_a_pbo_near_one_half()
    {
        // Acht reine Rauschreihen: die in-sample beste Variante ist out-of-sample reiner Zufall.
        var rng = new DeterministicRng(2026);
        var columns = Enumerable.Range(0, 8).Select(_ => Normal(rng, 128)).ToList();

        var result = CscvPbo.Compute(ToMatrix(columns), blocks: 8);

        result.Available.Should().BeTrue();
        result.Combinations.Should().Be(70);          // C(8,4)
        result.Pbo!.Value.Should().BeInRange(0.25, 0.75);
    }

    [Fact]
    public void A_genuine_controlled_effect_produces_a_low_pbo()
    {
        // Sieben Rauschreihen und eine Reihe mit einem echten, deutlichen Vorteil.
        var rng = new DeterministicRng(4711);
        var columns = Enumerable.Range(0, 7).Select(_ => Normal(rng, 128)).ToList();
        columns.Add(Normal(rng, 128, mean: 1.5));     // Sharpe je Periode ≈ 1,5

        var result = CscvPbo.Compute(ToMatrix(columns), blocks: 8);

        result.Available.Should().BeTrue();
        result.Pbo!.Value.Should().BeLessThan(0.10);
        // Der echte Effekt wird auch out-of-sample bestätigt.
        result.ShareNegativeOutOfSample!.Value.Should().BeLessThan(0.10);
    }

    [Fact]
    public void Identical_candidates_are_detected_and_reported()
    {
        var rng = new DeterministicRng(5);
        var one = Normal(rng, 96);
        var columns = Enumerable.Range(0, 6).Select(_ => (double[])one.Clone()).ToList();

        var result = CscvPbo.Compute(ToMatrix(columns), blocks: 6);

        result.Available.Should().BeTrue();
        // Alle gleich → gewählter Kandidat liegt immer exakt in der Mitte → λ = 0, nie < 0.
        result.Pbo!.Value.Should().Be(0.0);
        result.Notes.Should().Contain(n => n.Contains("identisch"));
    }

    [Fact]
    public void Constant_returns_make_the_preselection_metric_undefined()
    {
        var columns = Enumerable.Range(0, 4).Select(_ => Enumerable.Repeat(0.01, 96).ToArray()).ToList();

        var result = CscvPbo.Compute(ToMatrix(columns), blocks: 8);

        result.Available.Should().BeFalse();
        result.UnavailableReason.Should().Contain("Vorauswahlmetrik");
    }

    [Fact]
    public void Too_few_observations_or_candidates_are_reported_not_approximated()
    {
        var rng = new DeterministicRng(9);

        var shortMatrix = ToMatrix(Enumerable.Range(0, 4).Select(_ => Normal(rng, 10)).ToList());
        CscvPbo.Compute(shortMatrix, blocks: 8).UnavailableReason.Should().Contain("Zu wenige Perioden");

        var single = ToMatrix(new List<double[]> { Normal(rng, 128) });
        CscvPbo.Compute(single, blocks: 8).UnavailableReason.Should().Contain("Kandidat");

        var odd = ToMatrix(Enumerable.Range(0, 4).Select(_ => Normal(rng, 128)).ToList());
        CscvPbo.Compute(odd, blocks: 7).UnavailableReason.Should().Contain("gerade");
    }

    [Fact]
    public void Columns_with_invalid_values_are_excluded_and_listed()
    {
        var rng = new DeterministicRng(13);
        var columns = Enumerable.Range(0, 5).Select(_ => Normal(rng, 96)).ToList();
        columns[2][10] = double.NaN;

        var result = CscvPbo.Compute(ToMatrix(columns), blocks: 6);

        result.ExcludedColumns.Should().Equal(2);
        result.Candidates.Should().Be(4);
        result.Notes.Should().Contain(n => n.Contains("ungültige Werte"));
    }

    [Fact]
    public void Leftover_periods_are_dropped_and_reported_instead_of_being_padded()
    {
        var rng = new DeterministicRng(17);
        // 100 Perioden bei 8 Blöcken → Blockgröße 12, 4 Perioden bleiben übrig.
        var columns = Enumerable.Range(0, 4).Select(_ => Normal(rng, 100)).ToList();

        var result = CscvPbo.Compute(ToMatrix(columns), blocks: 8);

        result.Observations.Should().Be(96);
        result.Notes.Should().Contain(n => n.Contains("verworfen"));
    }

    [Fact]
    public void The_applied_definitions_are_part_of_the_result()
    {
        var rng = new DeterministicRng(23);
        var columns = Enumerable.Range(0, 4).Select(_ => Normal(rng, 96)).ToList();

        var result = CscvPbo.Compute(ToMatrix(columns), blocks: 6);

        result.Definitions.Should().Contain(d => d.Contains("Vorauswahlmetrik"));
        result.Definitions.Should().Contain(d => d.Contains("Rangrichtung"));
        result.Definitions.Should().Contain(d => d.Contains("Gleichstände"));
        result.Definitions.Should().Contain(d => d.Contains("Blockaufteilung"));
    }

    [Fact]
    public void Results_are_reproducible_for_the_same_input()
    {
        var columns = Enumerable.Range(0, 6).Select(k => Normal(new DeterministicRng(100 + k), 96)).ToList();
        var matrix = ToMatrix(columns);

        var a = CscvPbo.Compute(matrix, blocks: 6);
        var b = CscvPbo.Compute(matrix, blocks: 6);

        a.Pbo.Should().Be(b.Pbo);
        a.Trials.Select(t => t.Logit).Should().Equal(b.Trials.Select(t => t.Logit));
    }
}
