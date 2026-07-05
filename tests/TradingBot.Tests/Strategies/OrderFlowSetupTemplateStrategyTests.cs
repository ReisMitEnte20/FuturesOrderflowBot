using FluentAssertions;
using TradingBot.Application.Strategies;
using TradingBot.Application.Strategies.OrderFlow;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using Xunit;
using static TradingBot.Tests.Strategies.OrderFlowTemplateTestData;

namespace TradingBot.Tests.Strategies;

public class OrderFlowSetupTemplateStrategyTests
{
    private static OrderFlowSetupTemplateStrategy NewInitialized(Dictionary<string, string>? parameters = null)
    {
        var strategy = new OrderFlowSetupTemplateStrategy();
        strategy.Initialize(Context(TemplateConfig(parameters)));
        return strategy;
    }

    private static TradeSignal? Feed(OrderFlowSetupTemplateStrategy strategy, IEnumerable<OrderFlowBar> bars)
    {
        TradeSignal? last = null;
        foreach (var bar in bars)
            last = strategy.OnOrderFlowBar(bar) ?? last;
        return last;
    }

    // ----------------------------- Signal-Erzeugung --------------------------

    [Fact]
    public void Long_setup_produces_long_signal_with_reasons()
    {
        var strategy = NewInitialized(new() { ["RequiredConfirmations"] = "2" });

        var signal = Feed(strategy, LongSetupBars());

        signal.Should().NotBeNull();
        signal!.Direction.Should().Be(SignalDirection.Long);
        signal.StrategyName.Should().Be("OrderFlowSetupTemplateStrategy");
        signal.Reason.Should().Contain("Long signal");
        signal.TriggeredConditions.Should().Contain("DeltaDivergence");
        signal.TriggeredConditions.Should().Contain("LiquiditySweep");
        signal.FailedConditions.Should().Contain("BreakoutConfirmation"); // bewusst nicht erfüllt
        signal.Confidence.Should().Be(0.75m);                             // 6 von 8 Confirmations
        signal.DebugNotes.Should().BeNull();                              // keine InsufficientData-Checks aktiv
    }

    [Fact]
    public void No_signal_without_orderflow_classification()
    {
        var strategy = NewInitialized();

        var signal = strategy.OnOrderFlowBar(UnclassifiedBar());

        signal.Should().BeNull();
    }

    [Fact]
    public void No_signal_when_required_confirmations_not_reached()
    {
        var strategy = NewInitialized(new() { ["RequiredConfirmations"] = "7" }); // nur 6 erfüllbar

        Feed(strategy, LongSetupBars()).Should().BeNull();
    }

    [Fact]
    public void Required_confirmations_boundary_is_exact()
    {
        var at6 = NewInitialized(new() { ["RequiredConfirmations"] = "6" });
        Feed(at6, LongSetupBars()).Should().NotBeNull(); // genau 6 erfüllt

        var at7 = NewInitialized(new() { ["RequiredConfirmations"] = "7" });
        Feed(at7, LongSetupBars()).Should().BeNull();
    }

    // ----------------------------- Config respektiert ------------------------

    [Fact]
    public void MinVolume_filter_blocks_signal()
    {
        var strategy = NewInitialized(new()
        {
            ["RequiredConfirmations"] = "2",
            ["MinVolume"] = "999999"
        });

        Feed(strategy, LongSetupBars()).Should().BeNull();
    }

    [Fact]
    public void ImbalanceRatio_parameter_changes_confirmation_count()
    {
        // Ratio 1000 -> BarImbalance faellt weg -> nur noch 5 Confirmations -> Required 6 scheitert.
        var strict = NewInitialized(new()
        {
            ["RequiredConfirmations"] = "6",
            ["ImbalanceRatio"] = "1000"
        });

        Feed(strict, LongSetupBars()).Should().BeNull();
    }

    [Fact]
    public void Vwap_filter_blocks_when_too_far_from_vwap()
    {
        var strategy = NewInitialized(new()
        {
            ["RequiredConfirmations"] = "2",
            ["UseVwapFilter"] = "true",
            ["MaxDistanceFromVwapTicks"] = "1" // 0.25 Punkte - unrealistisch eng -> Filter blockt
        });

        Feed(strategy, LongSetupBars()).Should().BeNull();
    }

    [Fact]
    public void Cooldown_suppresses_immediate_followup_signals()
    {
        var strategy = NewInitialized(new()
        {
            ["RequiredConfirmations"] = "2",
            ["CooldownBars"] = "10"
        });

        var bars = LongSetupBars();
        Feed(strategy, bars).Should().NotBeNull();            // erstes Signal

        // Gleiches Setup direkt nochmal anhaengen -> Cooldown unterdrueckt.
        var repeat = Bar(3, 19990m, 20000m, 19975m, 19998m, 100m, 200m, 160m);
        strategy.OnOrderFlowBar(repeat).Should().BeNull();
    }

    // ----------------------------- Framework-Integration ---------------------

    [Fact]
    public void Disabled_strategy_is_not_invoked_by_engine()
    {
        var registry = new StrategyRegistry();
        var engine = new StrategyEngine(registry);
        registry.Register(new OrderFlowSetupTemplateStrategy(), TemplateConfig(enabled: false));
        engine.Initialize(Context(TemplateConfig(enabled: false)));

        foreach (var bar in LongSetupBars()) engine.OnOrderFlowBar(bar);

        engine.CollectedSignals.Should().BeEmpty();
    }

    [Fact]
    public void Engine_registers_runs_template_and_enriches_signal()
    {
        var registry = new StrategyRegistry();
        var engine = new StrategyEngine(registry);
        var config = TemplateConfig(new() { ["RequiredConfirmations"] = "2" }, suggestedContracts: 2);
        registry.Register(new OrderFlowSetupTemplateStrategy(), config);
        engine.Initialize(Context(config));

        foreach (var bar in LongSetupBars()) engine.OnOrderFlowBar(bar);

        engine.CollectedSignals.Should().HaveCount(1);
        var signal = engine.CollectedSignals[0];
        signal.Direction.Should().Be(SignalDirection.Long);
        signal.SuggestedQuantity.Should().Be(2);      // aus StrategyConfig ergaenzt
        signal.SuggestedStopLossTicks.Should().Be(40);
        signal.SuggestedTakeProfitTicks.Should().Be(60);
    }

    [Fact]
    public void Engine_blocks_unclassified_bars_before_template()
    {
        var registry = new StrategyRegistry();
        var engine = new StrategyEngine(registry);
        registry.Register(new OrderFlowSetupTemplateStrategy(), TemplateConfig());
        engine.Initialize(Context(TemplateConfig()));

        var results = engine.OnOrderFlowBar(UnclassifiedBar());

        results.Should().ContainSingle(r => !r.HasSignal && r.Reason!.Contains("Fake"));
        engine.CollectedSignals.Should().BeEmpty();
    }

    // ----------------------------- Determinismus & Safety --------------------

    [Fact]
    public void Same_bars_and_config_produce_identical_signals()
    {
        var a = Feed(NewInitialized(new() { ["RequiredConfirmations"] = "2" }), LongSetupBars());
        var b = Feed(NewInitialized(new() { ["RequiredConfirmations"] = "2" }), LongSetupBars());

        a!.Direction.Should().Be(b!.Direction);
        a.Reason.Should().Be(b.Reason);
        a.TriggeredConditions.Should().BeEquivalentTo(b.TriggeredConditions);
        a.Confidence.Should().Be(b.Confidence);
    }

    [Fact]
    public void Strategy_declares_orderflow_data_requirements()
    {
        IStrategy strategy = new OrderFlowSetupTemplateStrategy();

        strategy.DataRequirements.NeedsOrderFlowBars.Should().BeTrue();
        strategy.DataRequirements.NeedsBidAskVolume.Should().BeTrue();
        strategy.DataRequirements.NeedsDelta.Should().BeTrue();
    }

    [Fact]
    public void Strategy_has_no_broker_or_execution_dependencies()
    {
        typeof(OrderFlowSetupTemplateStrategy).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Should().BeEmpty(); // parameterlos - keinerlei Order-/Broker-/Netzwerk-Referenzen

        // Und: alle Handler liefern TradeSignal, niemals OrderRequest.
        typeof(IStrategy).GetMethods()
            .Should().NotContain(m => m.ReturnType == typeof(OrderRequest));
    }

    [Fact]
    public void Reset_clears_state_for_new_session()
    {
        var strategy = NewInitialized(new() { ["RequiredConfirmations"] = "2" });
        Feed(strategy, LongSetupBars()).Should().NotBeNull();

        ((IStrategy)strategy).Reset();

        // Nach Reset: gleiche Bars -> gleiches Signal (Zustand vollstaendig zurueckgesetzt).
        Feed(strategy, LongSetupBars()).Should().NotBeNull();
    }
}
