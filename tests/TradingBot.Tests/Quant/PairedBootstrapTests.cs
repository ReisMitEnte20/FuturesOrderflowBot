using FluentAssertions;
using TradingBot.Quant.MonteCarlo;

namespace TradingBot.Tests.Quant;

/// <summary>
/// Gepaarter Block-Bootstrap für den Mehrstrategie-Vergleich: derselbe Index-Pfad je Replikation auf allen
/// Reihen, Reproduzierbarkeit über den Seed, Ordnungsunabhängigkeit und die Sonderfälle identischer Reihen.
/// Kein Profitabilitätsnachweis.
/// </summary>
public class PairedBootstrapTests
{
    private static double[] SeriesA() =>
        new double[] { 0.010, -0.006, 0.004, -0.002, 0.012, -0.009, 0.005, -0.001, 0.007, -0.008,
                       0.003, 0.006, -0.004, 0.002, -0.001, 0.011, -0.007, 0.004, 0.002, -0.005 };

    private static double[] SeriesB() =>
        new double[] { 0.006, -0.004, 0.002, -0.001, 0.008, -0.006, 0.003, -0.002, 0.004, -0.005,
                       0.002, 0.003, -0.002, 0.001, -0.001, 0.006, -0.004, 0.002, 0.001, -0.003 };

    private static MonteCarloOptions Opts(int seed = 4711) => new()
    {
        Method = ResamplingMethod.MovingBlock,
        Accumulation = AccumulationMode.Multiplicative,
        Iterations = 400,
        Seed = seed,
        InitialCapital = 10_000.0
    };

    [Fact]
    public void Identical_series_produce_zero_difference_and_all_ties()
    {
        var s = SeriesA();
        var res = PairedBootstrap.Run(
            new IReadOnlyList<double>[] { s, (double[])s.Clone() },
            new[] { "X", "Y" }, Opts());

        var d = res.Differences.Should().ContainSingle().Subject;
        // Gleiche Reihe, gleicher Pfad je Replikation -> jede Differenz ist exakt 0.
        d.FinalCapitalDelta.Values.Should().OnlyContain(v => v == 0.0);
        d.FinalCapitalDelta.Median.Should().Be(0.0);
        d.ShareLeftBeatsRight.Should().Be(0.0);
        d.ShareTie.Should().Be(1.0);
        // Beide Reihen liefern dieselbe Endkapital-Verteilung.
        res.PerSeries[0].FinalCapital.Values.Should().Equal(res.PerSeries[1].FinalCapital.Values);
    }

    [Fact]
    public void Is_reproducible_for_a_seed_and_differs_for_another()
    {
        var series = new IReadOnlyList<double>[] { SeriesA(), SeriesB() };
        var names = new[] { "A", "B" };

        var r1 = PairedBootstrap.Run(series, names, Opts(seed: 100));
        var r2 = PairedBootstrap.Run(series, names, Opts(seed: 100));
        var r3 = PairedBootstrap.Run(series, names, Opts(seed: 101));

        r1.Differences[0].FinalCapitalDelta.Values.Should().Equal(r2.Differences[0].FinalCapitalDelta.Values);
        r1.PerSeries[0].FinalCapital.Values.Should().Equal(r2.PerSeries[0].FinalCapital.Values);
        r1.Differences[0].FinalCapitalDelta.Values.Should().NotEqual(r3.Differences[0].FinalCapitalDelta.Values);
    }

    [Fact]
    public void Candidate_order_does_not_change_the_paired_result()
    {
        var a = SeriesA(); var b = SeriesB();
        var forward = PairedBootstrap.Run(new IReadOnlyList<double>[] { a, b }, new[] { "A", "B" }, Opts());
        var reversed = PairedBootstrap.Run(new IReadOnlyList<double>[] { b, a }, new[] { "B", "A" }, Opts());

        // Kanonisch nach Name (A,B) -> beide Läufe liefern dasselbe Paar in derselben Orientierung.
        var df = forward.Differences.Single();
        var dr = reversed.Differences.Single();
        df.LeftName.Should().Be("A"); df.RightName.Should().Be("B");
        dr.LeftName.Should().Be("A"); dr.RightName.Should().Be("B");
        df.FinalCapitalDelta.Values.Should().Equal(dr.FinalCapitalDelta.Values);
        df.ShareLeftBeatsRight.Should().Be(dr.ShareLeftBeatsRight);

        // Auch die reihenbezogenen Verteilungen sind namensgleich identisch.
        double MedOf(PairedBootstrapResult r, string name) =>
            r.PerSeries[Array.IndexOf(r.PerSeries.Select(p => p.FinalCapital.Label).ToArray(),
                r.PerSeries.First(p => p.FinalCapital.Label.Contains(name)).FinalCapital.Label)].FinalCapital.Median;
        MedOf(forward, "A").Should().Be(MedOf(reversed, "A"));
    }

    [Fact]
    public void Three_series_yield_all_unordered_pairs_and_paired_shares_are_consistent()
    {
        var res = PairedBootstrap.Run(
            new IReadOnlyList<double>[] { SeriesA(), SeriesB(), SeriesB().Select(x => x * 0.5).ToArray() },
            new[] { "A", "B", "C" }, Opts());

        res.Differences.Should().HaveCount(3); // A-B, A-C, B-C
        res.Differences.Select(d => (d.LeftName, d.RightName))
            .Should().Equal(("A", "B"), ("A", "C"), ("B", "C"));
        // ShareLeftBeats + ShareTie + ShareRightBeats == 1 (Auszählung ist vollständig).
        foreach (var d in res.Differences)
            (d.ShareLeftBeatsRight + d.ShareTie).Should().BeLessThanOrEqualTo(1.0 + 1e-9);
    }
}
