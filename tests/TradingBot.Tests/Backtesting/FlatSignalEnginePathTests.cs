using FluentAssertions;
using TradingBot.Backtesting;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using Xunit;

namespace TradingBot.Tests.Backtesting;

/// <summary>
/// Belegt am ECHTEN Engine-Pfad (BacktestEngine + OrderManager + Fills), wie Signale ausgefuehrt werden:
///  - Long -> Flat schliesst die Position OHNE Gegenposition (Flat = Glattstellen).
///  - Long -> Short fuehrt zum vorgesehenen Positionswechsel (konservativ: schliessen, dann per Folgesignal eroeffnen).
///  - Fills erfolgen erst auf einem SPAETEREN Tick (kein Lookahead).
///  - Offene Position und ausstehende Orders am Datenende bleiben im Ergebnis sichtbar.
/// </summary>
public class FlatSignalEnginePathTests
{
    private static readonly IBacktestEngine Engine = new BacktestEngine();

    /// <summary>Emittiert je Tick-Index ein skriptgesteuertes Signal (sonst null).</summary>
    private sealed class ScriptedTickStrategy : IStrategy
    {
        private readonly IReadOnlyDictionary<int, SignalDirection> _script;
        private int _n;
        public ScriptedTickStrategy(IReadOnlyDictionary<int, SignalDirection> script) => _script = script;
        public string Name => "Scripted";
        public StrategyDataRequirements DataRequirements => new() { NeedsTicks = true };
        public TradeSignal? OnTick(MarketTick tick)
        {
            int i = _n++;
            if (!_script.TryGetValue(i, out var dir)) return null;
            return new TradeSignal
            {
                StrategyName = Name, Symbol = tick.Symbol, Direction = dir,
                Timestamp = tick.Timestamp, ReferencePrice = tick.Price, SuggestedQuantity = 1
            };
        }
        public void Reset() => _n = 0;
    }

    private static List<MarketTick> Ticks(int n) => BacktestTestData.RisingTicks(n);

    [Fact]
    public async Task Long_then_Flat_closes_the_position_without_opening_an_opposite()
    {
        // Long @Tick2 (Fill @3), Flat @Tick6 (Close-Fill @7). Danach keine Gegenposition.
        var strat = new ScriptedTickStrategy(new Dictionary<int, SignalDirection>
        {
            [2] = SignalDirection.Long, [6] = SignalDirection.Flat
        });
        var result = await Engine.RunAsync(BacktestTestData.Request(strat, Ticks(12)));

        result.Status.Should().Be(BacktestRunStatus.Completed);
        result.Trades.Should().ContainSingle("Flat schliesst die eine offene Long-Position");
        result.Trades[0].Side.Should().Be(PositionSide.Long);
        result.FinalPositionSide.Should().Be(PositionSide.Flat, "nach Flat ist die Position glatt, keine Gegenposition");
        result.FinalPositionQuantity.Should().Be(0);
    }

    [Fact]
    public async Task Fills_happen_on_a_later_tick_not_on_the_signal_tick()
    {
        // Signal auf Tick 2 -> Market-Order fuellt erst beim naechsten Tick (3).
        var strat = new ScriptedTickStrategy(new Dictionary<int, SignalDirection> { [2] = SignalDirection.Long, [6] = SignalDirection.Flat });
        var result = await Engine.RunAsync(BacktestTestData.Request(strat, Ticks(12)));

        result.Trades.Should().ContainSingle();
        result.Trades[0].EntryTime.Should().Be(BacktestTestData.T0.AddSeconds(3),
            "Entry fuellt am Tick NACH dem Signal (kein Fill auf dem Signal-Tick, kein Lookahead)");
        result.Trades[0].EntryTime.Should().BeAfter(BacktestTestData.T0.AddSeconds(2));
    }

    [Fact]
    public async Task Long_then_Short_changes_the_position_via_close_then_reopen()
    {
        // Long @2 (Fill 3); Short @6 (==offene Menge -> Close, Fill 7); Short @9 (jetzt flat -> Entry Short, Fill 10).
        var strat = new ScriptedTickStrategy(new Dictionary<int, SignalDirection>
        {
            [2] = SignalDirection.Long, [6] = SignalDirection.Short, [9] = SignalDirection.Short
        });
        var result = await Engine.RunAsync(BacktestTestData.Request(strat, Ticks(14)));

        result.Status.Should().Be(BacktestRunStatus.Completed);
        result.Trades.Should().ContainSingle("nur die Long-Position wurde geschlossen; die Short-Position ist am Ende noch offen");
        result.Trades[0].Side.Should().Be(PositionSide.Long);
        // Positionswechsel vollzogen: am Ende Short (Long geschlossen, Short per Folgesignal eroeffnet).
        result.FinalPositionSide.Should().Be(PositionSide.Short);
        result.FinalPositionQuantity.Should().Be(1);
    }

    [Fact]
    public async Task Open_position_and_pending_order_at_data_end_stay_visible()
    {
        // Long @2 (Fill 3) bleibt offen; weiteres Signal am LETZTEN Tick (9) -> Order bleibt ungefuellt (pending).
        var strat = new ScriptedTickStrategy(new Dictionary<int, SignalDirection>
        {
            [2] = SignalDirection.Long, [9] = SignalDirection.Long
        });
        var result = await Engine.RunAsync(BacktestTestData.Request(strat, Ticks(10)));

        result.Status.Should().Be(BacktestRunStatus.Completed);
        result.FinalPositionSide.Should().Be(PositionSide.Long, "die Engine stellt am Datenende NICHT zwangsweise glatt");
        result.FinalPositionQuantity.Should().BeGreaterThan(0);
        result.OrdersUnfilledAtEnd.Should().BeGreaterThan(0, "die Order am letzten Tick kann nicht mehr fuellen");
    }

    [Fact]
    public async Task A_raw_Flat_signal_without_a_position_is_a_noop_not_a_crash()
    {
        var strat = new ScriptedTickStrategy(new Dictionary<int, SignalDirection> { [3] = SignalDirection.Flat });
        var result = await Engine.RunAsync(BacktestTestData.Request(strat, Ticks(8)));

        result.Status.Should().Be(BacktestRunStatus.Completed, "Flat ohne Position darf den Lauf nicht abbrechen");
        result.Trades.Should().BeEmpty();
        result.FinalPositionSide.Should().Be(PositionSide.Flat);
    }
}
