using FluentAssertions;
using TradingBot.DevDashboard.Services;
using TradingBot.DevDashboard.Services.Quant;
using Xunit;

namespace TradingBot.Tests.DevDashboard;

/// <summary>
/// Der gemeinsame Mehrstrategie-Vergleich vergleicht nur INTERVALLGLEICHE Renditen (gleicher Start UND gleiches
/// Ende), füllt fehlende Perioden NICHT still mit Null und weist eine zu kleine Stichprobe ausdrücklich als nicht
/// belastbar aus (kein Sieger). Kandidatenreihenfolge verändert das Ergebnis nicht.
/// </summary>
public sealed class ResearchComparisonBuilderTests
{
    private const long Min = 60_000L;
    private static readonly long Base = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static QuantWalkForwardResponse Wf(long[] ends, long[] starts, double[] returns)
    {
        var eq = new List<double>(returns.Length);
        double c = 1.0;
        foreach (var r in returns) { c *= 1.0 + r; eq.Add(c); }
        return new QuantWalkForwardResponse
        {
            Ok = true, SelectionMetric = "sharpe", Mode = "Rolling",
            OosT = ends, OosStartT = starts, OosEquity = eq,
            Folds = Array.Empty<QuantFoldDto>(), CampaignId = "c"
        };
    }

    private static ResearchRunRecord Family(string key, QuantWalkForwardResponse wf, int minObs = 20)
        => new()
        {
            RunId = "run-" + key,
            CampaignId = "camp-" + key,
            CampaignGroupId = "grp", FamilyKey = key, FamilyName = key.ToUpperInvariant(),
            Status = ResearchStepStatus.Completed,
            WalkForward = wf,
            Config = new ResearchStartRequest
            {
                Run = new BacktestRunRequest { Strategy = key, Symbol = "MES", InitialBalance = 10_000m },
                Campaign = new CampaignInput { Id = "camp-" + key },
                Options = new QuantEvaluationOptions { Frequency = "Bar" },
                MonteCarloMethod = "MovingBlock", MonteCarloIterations = 200, Seed = 7,
                MinComparisonObservations = minObs,
                Candidates = new[] { new Dictionary<string, string> { ["p"] = "1" } },
            }
        };

    // Regelmäßige, intervallgleiche Reihe: end[i]=Base+(i+1)·Min, start[i]=Base+i·Min.
    private static (long[] ends, long[] starts) Axis(int n)
    {
        var ends = new long[n]; var starts = new long[n];
        for (int i = 0; i < n; i++) { starts[i] = Base + i * Min; ends[i] = Base + (i + 1) * Min; }
        return (ends, starts);
    }

    [Fact]
    public void Identical_intervals_and_returns_give_zero_paired_difference()
    {
        var (ends, starts) = Axis(24);
        var rets = Enumerable.Range(0, 24).Select(i => (i % 2 == 0 ? 0.004 : -0.003)).ToArray();
        var cmp = ResearchComparisonBuilder.Build(new[]
        {
            Family("a", Wf(ends, starts, rets)),
            Family("b", Wf(ends, starts, (double[])rets.Clone())),
        });

        cmp.Available.Should().BeTrue();
        cmp.Sufficient.Should().BeTrue("24 ≥ 20 gemeinsame Perioden");
        cmp.CommonObservations.Should().Be(24);
        cmp.Differences.Should().ContainSingle();
        cmp.Differences[0].DeltaMedianFinal.Should().Be(0.0);
        cmp.Differences[0].ShareTie.Should().Be(1.0);
    }

    [Fact]
    public void Same_end_but_different_start_anchor_is_excluded_not_silently_paired()
    {
        var (ends, starts) = Axis(24);
        var startsB = (long[])starts.Clone();
        startsB[10] = starts[10] - 5 * Min;   // gleicher Endzeitpunkt, ANDERER Startanker → anderes Intervall
        var rets = Enumerable.Range(0, 24).Select(_ => 0.002).ToArray();

        var cmp = ResearchComparisonBuilder.Build(new[]
        {
            Family("a", Wf(ends, starts, rets)),
            Family("b", Wf(ends, startsB, rets)),
        });

        cmp.ExcludedIntervalMismatch.Should().Be(1, "der Punkt mit abweichendem Startanker wird ausgeschlossen");
        cmp.CommonObservations.Should().Be(23);
    }

    [Fact]
    public void Missing_period_in_one_family_is_excluded_without_null_fill()
    {
        var (ends, starts) = Axis(24);
        // Familie B fehlt der Punkt an Index 12 (z. B. Fold ohne Auswahl) — kein stiller Null-Fill.
        var endsB = ends.Where((_, i) => i != 12).ToArray();
        var startsB = starts.Where((_, i) => i != 12).ToArray();
        var retsA = Enumerable.Range(0, 24).Select(_ => 0.001).ToArray();
        var retsB = Enumerable.Range(0, 23).Select(_ => 0.001).ToArray();

        var cmp = ResearchComparisonBuilder.Build(new[]
        {
            Family("a", Wf(ends, starts, retsA)),
            Family("b", Wf(endsB, startsB, retsB)),
        });

        // Gemeinsame Perioden = Schnittmenge (23), der fehlende Punkt ist NICHT als 0 enthalten.
        cmp.CommonObservations.Should().Be(23);
        cmp.Notes.Should().Contain(n => n.Contains("nicht als Nullrendite"));
    }

    [Fact]
    public void Too_few_common_periods_are_marked_insufficient_no_winner()
    {
        var (ends, starts) = Axis(6);   // < Konvention 20
        var rets = new[] { 0.01, -0.02, 0.03, -0.01, 0.02, -0.03 };
        var cmp = ResearchComparisonBuilder.Build(new[]
        {
            Family("a", Wf(ends, starts, rets)),
            Family("b", Wf(ends, starts, rets.Select(r => r * 0.5).ToArray())),
        });

        cmp.Available.Should().BeTrue("technisch berechenbar");
        cmp.Sufficient.Should().BeFalse("fachlich nicht belastbar bei 6 < 20");
        cmp.CommonObservations.Should().Be(6);
        cmp.MinObservations.Should().Be(20);
        cmp.InsufficientReason.Should().NotBeNullOrEmpty();
        cmp.Notes.Should().Contain(n => n.Contains("KEIN Sieger"));
    }

    [Fact]
    public void Candidate_order_does_not_change_the_comparison()
    {
        var (ends, starts) = Axis(24);
        var ra = Enumerable.Range(0, 24).Select(i => Math.Sin(i) * 0.01).ToArray();
        var rb = Enumerable.Range(0, 24).Select(i => Math.Cos(i) * 0.008).ToArray();

        var fwd = ResearchComparisonBuilder.Build(new[] { Family("a", Wf(ends, starts, ra)), Family("b", Wf(ends, starts, rb)) });
        var rev = ResearchComparisonBuilder.Build(new[] { Family("b", Wf(ends, starts, rb)), Family("a", Wf(ends, starts, ra)) });

        fwd.Differences[0].DeltaMedianFinal.Should().Be(rev.Differences[0].DeltaMedianFinal);
        fwd.Differences[0].ShareLeftBeatsRight.Should().Be(rev.Differences[0].ShareLeftBeatsRight);
    }
}
