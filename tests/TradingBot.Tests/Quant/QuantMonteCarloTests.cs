using FluentAssertions;
using TradingBot.Quant.MonteCarlo;
using TradingBot.Quant.Statistics;

namespace TradingBot.Tests.Quant;

/// <summary>
/// Tests der Monte-Carlo-Bausteine: Reproduzierbarkeit, die Grenzen der Permutation, Erhalt der
/// Abhängigkeit beim gemeinsamen Resampling und die ehrliche Behandlung fehlender Grundlagen.
/// </summary>
public class QuantMonteCarloTests
{
    private static double[] TradePnLs() =>
        new double[] { 120, -80, 45, -30, 200, -150, 60, -20, 90, -110, 35, 75, -55, 15, -5, 130, -95, 40, 25, -60 };

    [Fact]
    public void Generator_is_reproducible_for_a_given_seed_and_differs_for_another()
    {
        var a = new DeterministicRng(42);
        var b = new DeterministicRng(42);
        var c = new DeterministicRng(43);

        var seqA = Enumerable.Range(0, 20).Select(_ => a.NextUInt64()).ToArray();
        var seqB = Enumerable.Range(0, 20).Select(_ => b.NextUInt64()).ToArray();
        var seqC = Enumerable.Range(0, 20).Select(_ => c.NextUInt64()).ToArray();

        seqA.Should().Equal(seqB);
        seqA.Should().NotEqual(seqC);
    }

    [Fact]
    public void NextInt_stays_in_range_and_covers_the_whole_range()
    {
        var rng = new DeterministicRng(7);
        var seen = new HashSet<int>();
        for (int i = 0; i < 2000; i++)
        {
            int v = rng.NextInt(5);
            v.Should().BeInRange(0, 4);
            seen.Add(v);
        }
        seen.Should().HaveCount(5);
    }

    [Fact]
    public void Same_seed_produces_identical_monte_carlo_results()
    {
        var options = new MonteCarloOptions { Iterations = 200, Seed = 12345, Method = ResamplingMethod.MovingBlock };

        var r1 = MonteCarloEngine.Run(TradePnLs(), options);
        var r2 = MonteCarloEngine.Run(TradePnLs(), options);
        var r3 = MonteCarloEngine.Run(TradePnLs(), options with { Seed = 999 });

        r1.FinalCapital.Values.Should().Equal(r2.FinalCapital.Values);
        r1.MaxDrawdown.Values.Should().Equal(r2.MaxDrawdown.Values);
        r1.FinalCapital.Values.Should().NotEqual(r3.FinalCapital.Values);
    }

    [Fact]
    public void Permutation_keeps_the_final_result_and_only_varies_the_path()
    {
        var pnls = TradePnLs();
        double expectedFinal = 10_000.0 + pnls.Sum();

        var result = MonteCarloEngine.Run(pnls, new MonteCarloOptions
        {
            Method = ResamplingMethod.Permutation,
            Accumulation = AccumulationMode.Additive,
            Iterations = 300,
            InitialCapital = 10_000.0
        });

        // Reihenfolgeanalyse: die Summe ist invariant …
        result.FinalCapital.Values.Should().OnlyContain(v => Math.Abs(v - expectedFinal) < 1e-9);
        // … der Drawdown-Pfad dagegen nicht.
        result.MaxDrawdown.Values.Distinct().Should().HaveCountGreaterThan(1);
        result.Assumptions.Should().Contain(a => a.Contains("Summe der Beobachtungen bleibt unverändert"));
    }

    [Fact]
    public void Block_bootstrap_varies_the_final_result_because_it_draws_with_replacement()
    {
        var result = MonteCarloEngine.Run(TradePnLs(), new MonteCarloOptions
        {
            Method = ResamplingMethod.MovingBlock, Iterations = 300, InitialCapital = 10_000.0
        });

        result.FinalCapital.Values.Distinct().Should().HaveCountGreaterThan(50);
        result.EffectiveBlockLength.Should().Be(Resampling.DefaultBlockLength(20));   // 20^(1/3) ≈ 2,71 → 3
    }

    [Fact]
    public void Default_block_length_follows_the_documented_rule()
    {
        Resampling.DefaultBlockLength(27).Should().Be(3);
        Resampling.DefaultBlockLength(1000).Should().Be(10);
        Resampling.DefaultBlockLength(1).Should().Be(1);
    }

    [Fact]
    public void Stationary_bootstrap_produces_indices_inside_the_original_range()
    {
        var rng = new DeterministicRng(5);
        var path = Resampling.BuildIndexPath(ResamplingMethod.Stationary, 50, rng, horizon: 200, blockLength: 6);

        path.Should().HaveCount(200);
        path.Should().OnlyContain(i => i >= 0 && i < 50);
    }

    [Fact]
    public void Joint_resampling_preserves_the_dependence_between_series()
    {
        // b ist eine exakte lineare Transformation von a → Korrelation 1.
        var rng = new DeterministicRng(11);
        var a = Enumerable.Range(0, 100).Select(_ => rng.NextDouble() - 0.5).ToArray();
        var b = a.Select(v => 2.0 * v + 0.1).ToArray();

        var path = Resampling.BuildIndexPath(ResamplingMethod.Stationary, a.Length, new DeterministicRng(3));
        var joint = Resampling.ApplyJointly(new IReadOnlyList<double>[] { a, b }, path);

        Stats.Correlation(joint[0], joint[1])!.Value.Should().BeApproximately(1.0, 1e-9);

        // Getrennt gezogene Pfade zerstören diese Abhängigkeit.
        var pa = Resampling.Apply(a, Resampling.BuildIndexPath(ResamplingMethod.Stationary, a.Length, new DeterministicRng(3)));
        var pb = Resampling.Apply(b, Resampling.BuildIndexPath(ResamplingMethod.Stationary, b.Length, new DeterministicRng(4)));
        Math.Abs(Stats.Correlation(pa, pb) ?? 0).Should().BeLessThan(0.9);
    }

    [Fact]
    public void Joint_run_uses_one_path_per_iteration_for_all_series()
    {
        var rng = new DeterministicRng(21);
        var a = Enumerable.Range(0, 60).Select(_ => rng.NextDouble() - 0.5).ToArray();
        var b = a.Select(v => v * 3.0).ToArray();

        var results = MonteCarloEngine.RunJointly(
            new IReadOnlyList<double>[] { a, b }, new[] { "A", "B" },
            new MonteCarloOptions { Iterations = 100, Seed = 8, Accumulation = AccumulationMode.Additive, InitialCapital = 100 });

        results.Should().HaveCount(2);
        // Bei identischem Pfad gilt in jedem Lauf: Summe(B) = 3 × Summe(A).
        for (int i = 0; i < results[0].FinalCapital.Values.Count; i++)
        {
            double sumA = results[0].FinalCapital.Values[i] - 100;
            double sumB = results[1].FinalCapital.Values[i] - 100;
            sumB.Should().BeApproximately(3.0 * sumA, 1e-9);
        }
        results[0].Assumptions.Should().Contain(x => x.Contains("derselbe Index-Pfad"));
    }

    [Fact]
    public void Capital_barrier_is_only_reported_when_it_was_explicitly_set()
    {
        var pnls = TradePnLs();

        var without = MonteCarloEngine.Run(pnls, new MonteCarloOptions { Iterations = 50, InitialCapital = 10_000 });
        without.ShareOfRunsBreachingBarrier.Should().BeNull();

        var with = MonteCarloEngine.Run(pnls, new MonteCarloOptions
        {
            Iterations = 50, InitialCapital = 10_000, CapitalBarrier = 9_950,
            Method = ResamplingMethod.Permutation
        });
        with.ShareOfRunsBreachingBarrier.Should().NotBeNull();
        with.Assumptions.Should().Contain(a => a.Contains("Kapitalgrenze"));
    }

    [Fact]
    public void Empty_input_yields_no_simulation_and_says_so()
    {
        var result = MonteCarloEngine.Run(Array.Empty<double>(), new MonteCarloOptions { Iterations = 10 });

        result.CompletedIterations.Should().Be(0);
        result.Notes.Should().Contain(n => n.Contains("Keine Beobachtungen"));
    }

    [Fact]
    public void Tiny_samples_are_flagged_as_not_meaningful()
    {
        var result = MonteCarloEngine.Run(new double[] { 10, -5, 3 }, new MonteCarloOptions { Iterations = 20 });
        result.Notes.Should().Contain(n => n.Contains("Beobachtungen"));
    }

    [Fact]
    public void Results_are_always_labelled_as_scenarios_not_as_probabilities()
    {
        var result = MonteCarloEngine.Run(TradePnLs(), new MonteCarloOptions { Iterations = 20 });
        result.Assumptions.Should().Contain(a => a.Contains("keine empirischen Wahrscheinlichkeiten"));
    }

    [Fact]
    public void Simulated_path_metrics_match_a_hand_computed_example()
    {
        // Additiv, Start 100: 100 → 120 → 90 → 110
        //   Höchststand 120, Tiefpunkt 90 → Drawdown (120−90)/120 = 0,25
        //   Längste Verlustserie = 1 (nur ein negativer Schritt hintereinander)
        var (final, dd, streak, breached) = MonteCarloEngine.Simulate(
            new double[] { 20, -30, 20 },
            new MonteCarloOptions { Accumulation = AccumulationMode.Additive, InitialCapital = 100, CapitalBarrier = 95 });

        final.Should().Be(110);
        dd.Should().BeApproximately(0.25, 1e-12);
        streak.Should().Be(1);
        breached.Should().BeTrue();     // 90 ≤ 95
    }
}
