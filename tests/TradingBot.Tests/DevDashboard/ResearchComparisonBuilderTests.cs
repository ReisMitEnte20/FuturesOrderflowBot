using FluentAssertions;
using TradingBot.DevDashboard.Services;
using TradingBot.DevDashboard.Services.Quant;
using TradingBot.Quant.Series;
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
    private const long Hr = 3_600_000L;
    private const long Day = 86_400_000L;
    private static readonly long Base = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    // Tag-0-Anker für die Tagesaggregations-Tests (UTC-Mitternacht).
    private static readonly long D0 = new DateTimeOffset(2026, 3, 16, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

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

    // ----------------------------------------------------------------------------------------------
    // Tagesauswertung: Fold-Teilintervalle am selben UTC-Datum werden zu EINER Tagesrendite verkettet
    // (Frequency="Daily"). Keine Teilrenditen addiert, keine fehlenden Tage/Nullrenditen erfunden,
    // Endkapital/Gesamtrendite bleiben durch reine Aggregation erhalten.
    // ----------------------------------------------------------------------------------------------

    private static ResearchRunRecord FamilyDaily(string key, QuantWalkForwardResponse wf, int minObs = 20)
    {
        var r = Family(key, wf, minObs);
        return r with { Config = r.Config with { Options = new QuantEvaluationOptions { Frequency = "Daily" } } };
    }

    [Fact]
    public void Two_fold_partial_intervals_on_same_date_collapse_to_one_compounded_daily_return()
    {
        // Tag 0: zwei Teilintervalle (Fold-Grenze), Tag 1: ein Intervall.
        var ends   = new[] { D0 + 12 * Hr, D0 + 23 * Hr, D0 + Day + 23 * Hr };
        var starts = new[] { D0,           D0 + 12 * Hr, D0 + Day };
        var rets   = new[] { 0.01,         0.02,         -0.03 };

        var cmp = ResearchComparisonBuilder.Build(new[]
        {
            FamilyDaily("a", Wf(ends, starts, rets)),
            FamilyDaily("b", Wf(ends, starts, (double[])rets.Clone())),
        });

        cmp.Available.Should().BeTrue();
        cmp.Frequency.Should().Be("Daily");
        cmp.CommonObservations.Should().Be(2, "3 Fold-Teilintervalle an 2 UTC-Tagen → 2 Tagesrenditen, nicht 3");
        // Tag 0 = (1.01·1.02)-1 = 0.0302 (verkettet, NICHT 0.03 addiert).
        cmp.Families[0].SharedOosEquity![1].Should().BeApproximately(10_000 * 1.01 * 1.02, 1e-6);
        cmp.Notes.Should().Contain(n => n.Contains("zusammengefasst"));
    }

    [Fact]
    public void First_interval_loss_is_measured_against_start_capital()
    {
        var ends   = new[] { D0 + 23 * Hr, D0 + Day + 23 * Hr };
        var starts = new[] { D0,           D0 + Day };
        var rets   = new[] { -0.05,        0.02 };

        var cmp = ResearchComparisonBuilder.Build(new[]
        {
            FamilyDaily("a", Wf(ends, starts, rets)),
            FamilyDaily("b", Wf(ends, starts, (double[])rets.Clone())),
        });

        cmp.CommonObservations.Should().Be(2);
        var eq = cmp.Families[0].SharedOosEquity!;
        eq[0].Should().BeApproximately(10_000, 1e-6, "Startkapital");
        eq[1].Should().BeApproximately(9_500, 1e-6, "erster Tag −5 % gegenüber dem Startkapital bleibt erhalten");
        eq[1].Should().BeLessThan(10_000);
    }

    [Fact]
    public void Gap_between_intervals_is_not_filled_with_a_zero_day()
    {
        // Tag 0 und Tag 2 vorhanden, Tag 1 fehlt (z. B. Handelslücke) — KEIN erfundener Nulltag.
        var ends   = new[] { D0 + 23 * Hr,     D0 + 2 * Day + 23 * Hr };
        var starts = new[] { D0,               D0 + 2 * Day };
        var rets   = new[] { 0.01,             -0.02 };

        var cmp = ResearchComparisonBuilder.Build(new[]
        {
            FamilyDaily("a", Wf(ends, starts, rets)),
            FamilyDaily("b", Wf(ends, starts, (double[])rets.Clone())),
        });

        cmp.CommonObservations.Should().Be(2, "nur die zwei tatsächlich vorhandenen Tage, kein Nulltag dazwischen");
        cmp.AxisT.Should().HaveCount(2);
        (cmp.AxisT[1] - cmp.AxisT[0]).Should().BeGreaterThan(Day, "die Lücke bleibt als Lücke erhalten");
    }

    [Fact]
    public void Daily_aggregation_preserves_total_return_and_final_capital()
    {
        var ends   = new[] { D0 + 12 * Hr, D0 + 23 * Hr, D0 + Day + 23 * Hr, D0 + 2 * Day + 23 * Hr };
        var starts = new[] { D0,           D0 + 12 * Hr, D0 + Day,           D0 + 2 * Day };
        var rets   = new[] { 0.01,         0.02,         -0.03,              0.015 };

        double fragFactor = 1.0;
        foreach (var r in rets) fragFactor *= 1.0 + r;

        var cmp = ResearchComparisonBuilder.Build(new[]
        {
            FamilyDaily("a", Wf(ends, starts, rets)),
            FamilyDaily("b", Wf(ends, starts, (double[])rets.Clone())),
        });

        cmp.CommonObservations.Should().Be(3, "4 Teilintervalle an 3 UTC-Tagen");
        cmp.Families[0].SharedOosEquity![^1].Should().BeApproximately(10_000 * fragFactor, 1e-6);
        cmp.Families[0].OosReturn.Should().BeApproximately(fragFactor - 1.0, 1e-9,
            "Gesamtrendite bleibt durch reine Aggregation erhalten");
    }

    [Fact]
    public void Intraday_loss_and_recovery_makes_daily_maxdd_smaller_than_finer_maxdd()
    {
        // Tag 0: starker Verlust (−10 %) und Erholung auf flat (netto 0 %) in ZWEI Teilintervallen; Tag 1: −5 %.
        // Tages-MaxDD sieht nur den flachen Tagesschluss (⇒ 5 %); die feiner aufgelöste Reihe sieht das Intraday-Tief (⇒ 10 %).
        double recover = 1.0 / 0.9 - 1.0; // bringt 0.90 zurück auf 1.00
        var ends   = new[] { D0 + 12 * Hr, D0 + 23 * Hr, D0 + Day + 23 * Hr };
        var starts = new[] { D0,           D0 + 12 * Hr, D0 + Day };
        var rets   = new[] { -0.10,        recover,      -0.05 };

        var cmp = ResearchComparisonBuilder.Build(new[]
        {
            FamilyDaily("a", Wf(ends, starts, rets)),
            FamilyDaily("b", Wf(ends, starts, (double[])rets.Clone())),
        });

        cmp.CommonObservations.Should().Be(2, "Tag 0 (netto flat) und Tag 1");
        double dayMdd = cmp.Families[0].OosMaxDrawdown!.Value;         // Tages-MaxDD
        double finerMdd = OosDailyMerge.MaxDrawdownFraction(rets);     // feiner aufgelöst (Fold-Fragmente)
        dayMdd.Should().BeApproximately(0.05, 1e-6, "nur der Tagesschluss zählt");
        finerMdd.Should().BeApproximately(0.10, 1e-6, "das Intraday-Tief ist tiefer");
        dayMdd.Should().BeLessThan(finerMdd, "Tages-MaxDD ≤ feiner aufgelöste MaxDD");
    }
}
