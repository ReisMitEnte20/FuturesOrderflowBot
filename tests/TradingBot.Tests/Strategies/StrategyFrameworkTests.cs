using FluentAssertions;
using TradingBot.Application.Strategies;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Tests.Backtesting;
using Xunit;

namespace TradingBot.Tests.Strategies;

public class StrategyFrameworkTests
{
    private static StrategyConfig Config(
        string name, string symbol = "NQ", bool enabled = true,
        StrategyDataType dataType = StrategyDataType.Tick, int? maxSignals = null,
        int suggestedContracts = 1, int? stopTicks = null, int? tpTicks = null) => new()
    {
        Name = name, Symbol = symbol, Enabled = enabled, RequiredDataType = dataType,
        MaxSignalsPerSession = maxSignals, SuggestedContracts = suggestedContracts,
        StopLossTicks = stopTicks, TakeProfitTicks = tpTicks
    };

    private static (StrategyRegistry registry, StrategyEngine engine) NewEngine()
    {
        var registry = new StrategyRegistry();
        return (registry, new StrategyEngine(registry));
    }

    /// <summary>Zählt Aufrufe – beweist, ob die Engine die Strategie überhaupt invoked.</summary>
    private sealed class ProbeStrategy : IStrategy
    {
        public int TickCalls;
        public string Name => "Probe";
        public TradeSignal? OnTick(MarketTick tick) { TickCalls++; return null; }
    }

    // ----------------------------- Registry ----------------------------------

    [Fact]
    public void Registry_registers_and_lists_strategies()
    {
        var registry = new StrategyRegistry();
        registry.Register(new NoOpStrategy(), Config("A"));
        registry.Register(new TestSignalStrategy(), Config("B"));

        registry.All.Should().HaveCount(2);
        registry.Get("A").Should().NotBeNull();
        registry.Get("a").Should().NotBeNull(); // case-insensitive
    }

    [Fact]
    public void Duplicate_strategy_name_is_rejected()
    {
        var registry = new StrategyRegistry();
        registry.Register(new NoOpStrategy(), Config("Same"));

        var act = () => registry.Register(new TestSignalStrategy(), Config("Same"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*bereits registriert*");
    }

    [Fact]
    public void Enable_and_disable_toggle_state()
    {
        var registry = new StrategyRegistry();
        registry.Register(new NoOpStrategy(), Config("S", enabled: false));

        registry.IsEnabled("S").Should().BeFalse();
        registry.Enable("S").Should().BeTrue();
        registry.IsEnabled("S").Should().BeTrue();
        registry.Disable("S").Should().BeTrue();
        registry.IsEnabled("S").Should().BeFalse();
        registry.Enable("unbekannt").Should().BeFalse();
    }

    // ----------------------------- Engine: Enable/Disable --------------------

    [Fact]
    public void Disabled_strategy_is_not_even_invoked()
    {
        var (registry, engine) = NewEngine();
        var probe = new ProbeStrategy();
        registry.Register(probe, Config("Probe", enabled: false));

        var results = engine.OnTick(BacktestTestData.Tick(0, 20000m));

        probe.TickCalls.Should().Be(0);           // harte Framework-Garantie
        results.Should().BeEmpty();
        engine.CollectedSignals.Should().BeEmpty();
    }

    [Fact]
    public void Enabled_strategy_produces_signal()
    {
        var (registry, engine) = NewEngine();
        registry.Register(new TestSignalStrategy(intervalTicks: 1), Config("T"));

        var results = engine.OnTick(BacktestTestData.Tick(0, 20000m));

        results.Should().ContainSingle(r => r.HasSignal);
        engine.CollectedSignals.Should().HaveCount(1);
    }

    [Fact]
    public void Disabling_mid_session_stops_signals()
    {
        var (registry, engine) = NewEngine();
        registry.Register(new TestSignalStrategy(intervalTicks: 1), Config("T"));

        engine.OnTick(BacktestTestData.Tick(0, 20000m));
        registry.Disable("T");
        engine.OnTick(BacktestTestData.Tick(1, 20001m));

        engine.CollectedSignals.Should().HaveCount(1); // nur vor dem Disable
    }

    // ----------------------------- Engine: Routing ---------------------------

    [Fact]
    public void Symbol_mismatch_is_not_dispatched()
    {
        var (registry, engine) = NewEngine();
        var probe = new ProbeStrategy();
        registry.Register(probe, Config("Probe", symbol: "ES"));

        engine.OnTick(BacktestTestData.Tick(0, 20000m)); // NQ-Tick

        probe.TickCalls.Should().Be(0);
    }

    [Fact]
    public void Data_type_routing_only_dispatches_matching_events()
    {
        var (registry, engine) = NewEngine();
        var probe = new ProbeStrategy();
        registry.Register(probe, Config("Probe", dataType: StrategyDataType.Candle));

        engine.OnTick(BacktestTestData.Tick(0, 20000m)); // Tick an Candle-Strategie? Nein.

        probe.TickCalls.Should().Be(0);
    }

    [Fact]
    public void Signals_are_collected_across_strategies_and_events()
    {
        var (registry, engine) = NewEngine();
        registry.Register(new TestSignalStrategy(intervalTicks: 1), Config("A"));
        registry.Register(new TestSignalStrategy(intervalTicks: 1), Config("B"));

        engine.OnTick(BacktestTestData.Tick(0, 20000m));
        engine.OnTick(BacktestTestData.Tick(1, 20001m));

        engine.CollectedSignals.Should().HaveCount(4); // 2 Strategien x 2 Ticks
        engine.States.Should().OnlyContain(s => s.SignalsGenerated == 2);
    }

    // ----------------------------- Engine: Config respektiert ----------------

    [Fact]
    public void MaxSignalsPerSession_is_enforced()
    {
        var (registry, engine) = NewEngine();
        registry.Register(new TestSignalStrategy(intervalTicks: 1), Config("T", maxSignals: 2));

        for (int i = 0; i < 5; i++) engine.OnTick(BacktestTestData.Tick(i, 20000m + i));

        engine.CollectedSignals.Should().HaveCount(2);
        engine.States.Single().SignalsGenerated.Should().Be(2);
    }

    [Fact]
    public void Config_defaults_enrich_missing_signal_values()
    {
        var (registry, engine) = NewEngine();
        // Strategie ohne eigene Mengen-/TP-Angabe:
        var bare = new BareSignalStrategy();
        registry.Register(bare, Config("Bare", suggestedContracts: 3, stopTicks: 25, tpTicks: 50));

        var results = engine.OnTick(BacktestTestData.Tick(0, 20000m));

        var signal = results.Single().Signal!;
        signal.SuggestedQuantity.Should().Be(3);        // aus Config ergänzt
        signal.SuggestedStopLossTicks.Should().Be(25);
        signal.SuggestedTakeProfitTicks.Should().Be(50);
    }

    private sealed class BareSignalStrategy : IStrategy
    {
        public string Name => "Bare";
        public TradeSignal? OnTick(MarketTick tick) => new()
        {
            StrategyName = Name, Symbol = tick.Symbol,
            Direction = SignalDirection.Long, Timestamp = tick.Timestamp, ReferencePrice = tick.Price
        };
    }

    // ----------------------------- Reset --------------------------------------

    [Fact]
    public void Reset_clears_signals_counters_and_strategy_state()
    {
        var (registry, engine) = NewEngine();
        registry.Register(new TestSignalStrategy(intervalTicks: 1), Config("T"));
        engine.OnTick(BacktestTestData.Tick(0, 20000m));

        engine.Reset();

        engine.CollectedSignals.Should().BeEmpty();
        engine.States.Single().SignalsGenerated.Should().Be(0);
    }

    // ----------------------------- Dummy-Strategien ---------------------------

    [Fact]
    public void NoOpStrategy_never_signals()
    {
        var (registry, engine) = NewEngine();
        registry.Register(new NoOpStrategy(), Config("NoOp"));

        for (int i = 0; i < 10; i++) engine.OnTick(BacktestTestData.Tick(i, 20000m + i));

        engine.CollectedSignals.Should().BeEmpty();
    }

    [Fact]
    public void OrderFlowTemplate_produces_no_signal_without_real_orderflow_data()
    {
        IStrategy template = new OrderFlowTemplateStrategy();
        // Bar mit Volumen, aber ohne Bid/Ask-Klassifikation -> niemals ein Signal.
        var fakeBar = new OrderFlowBar
        {
            Symbol = "NQ", TotalVolume = 100m, BidVolume = 0m, AskVolume = 0m,
            Open = 20000m, High = 20010m, Low = 19990m, Close = 20005m
        };

        template.OnOrderFlowBar(fakeBar).Should().BeNull();
    }

    [Fact]
    public void Engine_blocks_unclassified_orderflow_bars_failclosed()
    {
        var (registry, engine) = NewEngine();
        var probe = new OrderFlowProbe();
        registry.Register(probe, Config("OF", dataType: StrategyDataType.OrderFlow));

        var fakeBar = new OrderFlowBar
        {
            Symbol = "NQ", TotalVolume = 100m, BidVolume = 0m, AskVolume = 0m
        };
        var results = engine.OnOrderFlowBar(fakeBar);

        probe.BarCalls.Should().Be(0); // gar nicht erst verteilt
        results.Should().ContainSingle(r => !r.HasSignal && r.Reason!.Contains("Fake"));

        var realBar = fakeBar with { BidVolume = 40m, AskVolume = 60m };
        engine.OnOrderFlowBar(realBar);
        probe.BarCalls.Should().Be(1); // echte Klassifikation -> verteilt
    }

    private sealed class OrderFlowProbe : IStrategy
    {
        public int BarCalls;
        public string Name => "OFProbe";
        public TradeSignal? OnOrderFlowBar(OrderFlowBar bar) { BarCalls++; return null; }
    }

    [Fact]
    public void MovingAverageDummy_signals_deterministically_on_crossover()
    {
        var (registry, engine) = NewEngine();
        var config = Config("MA", dataType: StrategyDataType.Candle) with
        {
            Parameters = new Dictionary<string, string> { ["FastPeriod"] = "2", ["SlowPeriod"] = "3" }
        };
        var strategy = new MovingAverageDummyStrategy();
        registry.Register(strategy, config);
        engine.Initialize(new StrategyExecutionContext { Symbol = "NQ" });

        // Fallende dann steigende Schlusskurse -> Crossover nach oben.
        decimal[] closes = { 100m, 90m, 80m, 70m, 95m, 120m };
        var t0 = BacktestTestData.T0;
        for (int i = 0; i < closes.Length; i++)
        {
            engine.OnCandle(new Candle
            {
                Symbol = "NQ", OpenTime = t0.AddMinutes(i), CloseTime = t0.AddMinutes(i + 1),
                Open = closes[i], High = closes[i], Low = closes[i], Close = closes[i]
            });
        }

        engine.CollectedSignals.Should().NotBeEmpty();
        engine.CollectedSignals[0].Direction.Should().Be(SignalDirection.Long);
    }

    // ----------------------------- Composite / Integration --------------------

    [Fact]
    public async Task CompositeStrategy_runs_inside_paper_session()
    {
        // Integrations-Brücke: eine ganze StrategyEngine läuft als EIN IStrategy in der Paper-Session.
        var registry = new StrategyRegistry();
        registry.Register(new TestSignalStrategy(intervalTicks: 2), Config("T"));
        var composite = new CompositeStrategy(new StrategyEngine(registry));

        var session = new TradingBot.PaperTrading.PaperTradingEngine().Start(
            TradingBot.Tests.PaperTrading.PaperTestData.Request(composite, BacktestTestData.RisingTicks(12)));
        var result = await session.Completion;

        result.Status.Should().Be(TradingBot.PaperTrading.PaperTradingRunStatus.Completed);
        result.SignalsGenerated.Should().BeGreaterThan(0);
        result.OrdersSubmitted.Should().BeGreaterThan(0);
    }

    // ----------------------------- Keine Order-/Broker-Referenzen -------------

    [Fact]
    public void StrategyEngine_has_no_broker_or_execution_dependencies()
    {
        var ctorParams = typeof(StrategyEngine).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .ToList();

        ctorParams.Should().OnlyContain(p => p.ParameterType == typeof(IStrategyRegistry));
        typeof(IStrategyEngine).GetMethods()
            .Should().NotContain(m => m.ReturnType == typeof(OrderRequest));
    }
}
