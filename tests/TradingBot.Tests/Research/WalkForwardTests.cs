using FluentAssertions;
using TradingBot.Research;
using TradingBot.Research.WalkForward;
using Xunit;
using static TradingBot.Tests.Research.ResearchTestData;

namespace TradingBot.Tests.Research;

public class WalkForwardTests
{
    // ----------------------------- Fenster (pur) -----------------------------

    [Fact]
    public void Rolling_windows_are_disjoint_and_sequential()
    {
        var windows = WalkForwardWindows.Generate(1000, 400, 200, 200, WalkForwardMode.Rolling);

        windows.Should().HaveCount(3);
        windows[0].Should().BeEquivalentTo(new { InSampleStart = 0, InSampleEnd = 400, OutOfSampleStart = 400, OutOfSampleEnd = 600 },
            o => o.ExcludingMissingMembers());
        // Keine Überlappung IS/OOS und OOS folgt IS:
        windows.Should().OnlyContain(w => w.OutOfSampleStart == w.InSampleEnd && w.OutOfSampleStart >= w.InSampleEnd);
        windows[2].InSampleStart.Should().Be(400);
        windows[2].OutOfSampleEnd.Should().Be(1000);
    }

    [Fact]
    public void Anchored_windows_grow_in_sample_from_start()
    {
        var windows = WalkForwardWindows.Generate(1000, 400, 200, 200, WalkForwardMode.Anchored);

        windows.Should().HaveCount(3);
        windows.Should().OnlyContain(w => w.InSampleStart == 0);   // immer verankert
        windows[0].InSampleEnd.Should().Be(400);
        windows[1].InSampleEnd.Should().Be(600);
        windows[2].InSampleEnd.Should().Be(800);
        windows.Should().OnlyContain(w => w.OutOfSampleStart == w.InSampleEnd); // keine Überlappung
    }

    // ----------------------------- Analyzer ----------------------------------

    [Fact]
    public async Task Selects_on_in_sample_and_tests_on_out_of_sample_detecting_overfit()
    {
        // Fake: unterscheidet IS (400 Ticks) von OOS (200 Ticks). "overfit"-Config: IS top, OOS negativ.
        var runner = new FakeBacktestRunner(inputs =>
        {
            bool isInSample = inputs.Ticks.Count == 400;
            bool overfit = inputs.Config.Name == "overfit";
            decimal net = (isInSample, overfit) switch
            {
                (true, true) => 1000m,   // sieht IS grandios aus
                (true, false) => 100m,   // stabile Config schwächer auf IS
                (false, true) => -200m,  // fällt OOS auseinander
                (false, false) => 50m
            };
            int trades = isInSample ? 10 : 5;
            return RunResult(inputs.Config.Name, net, trades, config: inputs.Config);
        });

        var candidate = Candidate("WF");
        var request = new WalkForwardRequest
        {
            Candidate = candidate,
            SelectionConfigs = new[] { StratConfig("overfit"), StratConfig("stable") },
            InSampleSize = 400, OutOfSampleSize = 200, Step = 200, Mode = WalkForwardMode.Rolling
        };
        var template = Template(DummyTicks(1000));

        var result = await new WalkForwardAnalyzer(runner).AnalyzeAsync(request, template);

        result.Segments.Should().HaveCount(3);
        // Auf IS wird "overfit" selektiert (1000 > 100):
        result.Segments.Should().OnlyContain(s => s.SelectedConfig.Name == "overfit");
        // IS und OOS klar getrennt (unterschiedliche Ergebnisse):
        result.Segments[0].InSample.NetProfit.Should().Be(1000m);
        result.Segments[0].OutOfSample.NetProfit.Should().Be(-200m);
        // Aggregat: IS > 0, OOS < 0 -> Overfitting-Verdacht, WFE negativ.
        result.InSampleNetProfit.Should().BeGreaterThan(0m);
        result.OutOfSampleNetProfit.Should().BeLessThan(0m);
        result.OverfittingSuspected.Should().BeTrue();
        result.WalkForwardEfficiency.Should().BeLessThan(0.5m);
    }

    [Fact]
    public async Task Robust_strategy_has_no_overfitting_warning()
    {
        // Beide Configs gleich; OOS ähnlich gut wie IS.
        var runner = new FakeBacktestRunner(inputs =>
        {
            bool isInSample = inputs.Ticks.Count == 400;
            return RunResult(inputs.Config.Name, net: isInSample ? 100m : 90m, tradeCount: isInSample ? 10 : 5,
                config: inputs.Config);
        });
        var request = new WalkForwardRequest
        {
            Candidate = Candidate("WF"), SelectionConfigs = new[] { StratConfig("a") },
            InSampleSize = 400, OutOfSampleSize = 200, Step = 200
        };

        var result = await new WalkForwardAnalyzer(runner).AnalyzeAsync(request, Template(DummyTicks(1000)));

        result.OutOfSampleNetProfit.Should().BeGreaterThan(0m);
        result.OverfittingSuspected.Should().BeFalse();
    }
}
