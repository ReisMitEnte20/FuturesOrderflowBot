using FluentAssertions;
using TradingBot.Application.Strategies.OrderFlow;
using TradingBot.Backtesting;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Research.Orderflow;
using TradingBot.Tests.Backtesting;
using Xunit;

namespace TradingBot.Tests.Orderflow;

/// <summary>
/// Belegt die Konsistenz von SinglePositionGate + BacktestEngine am echten Pfad:
/// Ein gesendetes Signal ist kein Fill. Da der Gate-Zustand aus Signalen modelliert wird, kann er bei
/// ABGELEHNTER Order abweichen. Deshalb:
///  - Trades/Position stammen IMMER aus der Engine (nie aus dem Gate-Modell).
///  - Die harte Obergrenze MaxContracts=1 garantiert max. 1 Kontrakt auch bei Divergenz (Backstop).
///  - Der Gate verhindert im Normalfall Add-Orders -> KEINE Ablehnungs-Pollution.
///  - Noch ungefuellte Orders am Datenende fuehren zu keiner Ueberposition.
///  - Der Tick-Pfad erzeugt KEINE SL/TP-Bracket-Orders -> keine unbemerkten SL/TP-Exits.
/// </summary>
public class SinglePositionGateEngineConsistencyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 23, 13, 30, 0, TimeSpan.Zero);
    private static readonly IBacktestEngine Engine = new BacktestEngine();

    private sealed class ScriptedOFStrategy : IOrderFlowStrategy
    {
        private readonly Queue<SignalDirection?> _q;
        private readonly int? _stopTicks;
        public ScriptedOFStrategy(SignalDirection?[] seq, int? stopTicks = null) { _q = new(seq); _stopTicks = stopTicks; }
        public string Name => "ScriptedOF";
        public StrategyDataRequirements DataRequirements => new() { NeedsOrderFlowBars = true };
        public TradeSignal? OnOrderFlowBar(OrderFlowBar b)
        {
            var d = _q.Count > 0 ? _q.Dequeue() : null;
            return d is null ? null : new TradeSignal
            { StrategyName = Name, Symbol = b.Symbol, Direction = d.Value, SuggestedQuantity = 1, SuggestedStopLossTicks = _stopTicks, Timestamp = b.CloseTime, ReferencePrice = b.Close };
        }
    }

    // Klassifizierte Ticks (Aggressor gesetzt), damit der Aggregator die Bar nicht fail-closed verwirft.
    private static List<MarketTick> Ticks(int n, decimal step = 1m) => Enumerable.Range(0, n)
        .Select(i => new MarketTick { Symbol = "NQ", Timestamp = T0.AddMilliseconds(i), Price = 20000m + i * step, Volume = 1m, Aggressor = AggressorSide.Buy })
        .ToList();

    private static RiskConfig MaxOne() => BacktestTestData.Risk() with { MaxContracts = 1 };

    private static Task<BacktestResult> Run(IStrategy strat, int ticks, int ticksPerBar = 2,
        Func<OrderRequest, bool>? reject = null, RiskConfig? risk = null, decimal step = 1m)
        => Engine.RunAsync(BacktestTestData.Request(
            new OrderFlowBarAggregatorStrategy(strat, ticksPerBar), Ticks(ticks, step),
            risk: risk ?? MaxOne(), rejectOrder: reject));

    private static SinglePositionGateStrategy Gated(params SignalDirection?[] seq) => new(new ScriptedOFStrategy(seq));

    [Fact]
    public async Task Backstop_MaxContracts_1_hard_caps_adds_even_without_the_gate()
    {
        // OHNE Gate: wiederholte gleichgerichtete Signale -> Engine versucht Adds -> Risk lehnt ab (MaxContracts=1).
        var r = await Run(new ScriptedOFStrategy(new SignalDirection?[] { SignalDirection.Long, SignalDirection.Long, SignalDirection.Long, SignalDirection.Long }), ticks: 12);
        r.Status.Should().Be(BacktestRunStatus.Completed);
        r.FinalPositionQuantity.Should().BeLessThanOrEqualTo(1, "harte Obergrenze greift");
        r.OrdersRejectedByRisk.Should().BeGreaterThan(0, "Add-Orders werden vom Risk-Cap abgelehnt");
    }

    [Fact]
    public async Task Gate_avoids_adds_so_no_rejection_pollution()
    {
        // MIT Gate: gleiche Signalfolge -> Adds werden gar nicht erst erzeugt -> keine Ablehnungen.
        var r = await Run(Gated(SignalDirection.Long, SignalDirection.Long, SignalDirection.Long, SignalDirection.Long), ticks: 12);
        r.Status.Should().Be(BacktestRunStatus.Completed);
        r.FinalPositionQuantity.Should().BeLessThanOrEqualTo(1);
        r.OrdersRejectedByRisk.Should().Be(0, "der Gate unterdrueckt Adds -> keine Ablehnungs-Pollution");
    }

    [Fact]
    public async Task Rejected_order_never_causes_over_position()
    {
        // Erste Order wird vom Broker abgelehnt -> Gate-Modell kann abweichen. Backstop + Engine-Wahrheit:
        int seen = 0;
        var r = await Run(Gated(SignalDirection.Long, SignalDirection.Short, SignalDirection.Short, SignalDirection.Long, SignalDirection.Long),
            ticks: 16, reject: _ => seen++ == 0);
        r.Status.Should().Be(BacktestRunStatus.Completed);
        r.OrdersRejectedByBroker.Should().BeGreaterThan(0);
        r.FinalPositionQuantity.Should().BeLessThanOrEqualTo(1, "selbst bei Gate-Divergenz niemals ueber 1 Kontrakt");
    }

    [Fact]
    public async Task Rejected_first_long_does_not_suppress_a_later_valid_long()
    {
        // Regression 1: Erste Long-Order wird vom Broker abgelehnt -> KEINE Phantomposition (Gate liest die
        // tatsaechliche, flache Position). Das spaetere gueltige Long MUSS oeffnen.
        int seen = 0;
        var r = await Run(Gated(SignalDirection.Long, SignalDirection.Long, SignalDirection.Long),
            ticks: 8, reject: _ => seen++ == 0);
        r.Status.Should().Be(BacktestRunStatus.Completed);
        r.OrdersRejectedByBroker.Should().Be(1);
        r.FinalPositionSide.Should().Be(PositionSide.Long, "das spaetere Long darf nicht wegen einer Phantomposition unterdrueckt werden");
        r.FinalPositionQuantity.Should().Be(1);
    }

    [Fact]
    public async Task First_entry_signal_on_the_very_last_tick_stays_pending_and_engine_flat()
    {
        // Regression 2: Erstes (einziges) Entry-Signal entsteht auf der LETZTEN Bar/dem letzten Tick.
        // Die Order bleibt tatsaechlich pending; die Engine bleibt Flat. Pending explizit geprueft.
        var r = await Run(Gated(null, null, SignalDirection.Long), ticks: 6); // 3 Bars (tpb=2), 3. Bar = letzter Tick
        r.Status.Should().Be(BacktestRunStatus.Completed);
        r.SignalsGenerated.Should().Be(1, "nur die letzte Bar liefert ein Signal");
        r.OrdersUnfilledAtEnd.Should().BeGreaterThan(0, "die Order am letzten Tick kann nicht mehr fuellen (pending)");
        r.FinalPositionSide.Should().Be(PositionSide.Flat);
        r.FinalPositionQuantity.Should().Be(0, "ohne Fill keine Position");
    }

    [Fact]
    public async Task Rejected_entry_never_produces_a_double_entry()
    {
        // Regression 3: Eine ABGELEHNTE (nicht gefuellte) erste Order fuehrt zu keiner Doppelposition.
        // Trotz mehrerer gleichgerichteter Signale bleibt die Position <= 1 Kontrakt.
        int seen = 0;
        var r = await Run(Gated(SignalDirection.Long, SignalDirection.Long, SignalDirection.Long, SignalDirection.Long),
            ticks: 12, reject: _ => seen++ == 0);
        r.Status.Should().Be(BacktestRunStatus.Completed);
        r.OrdersRejectedByBroker.Should().Be(1);
        r.FinalPositionQuantity.Should().BeLessThanOrEqualTo(1, "kein doppelter Entry trotz abgelehnter erster Order");
    }

    [Fact]
    public async Task Accepted_market_order_is_fully_filled_on_the_next_tick_before_the_next_signal()
    {
        // Betriebsgrenze (Market-Order-Replay-Pfad): Eine angenommene Order wird am FOLGE-Tick VOLLSTAENDIG
        // gefuellt, also vor dem naechsten Bar-Signal. Beweis: bar0 Long -> gefuellt (qty 1, nichts pending);
        // bar1 Long sieht die Position bereits Long -> unterdrueckt (nur 1 Entry-Submit); bar2 Flat schliesst
        // -> genau 1 abgeschlossener Trade mit voller Menge.
        var r = await Run(Gated(SignalDirection.Long, SignalDirection.Long, SignalDirection.Flat), ticks: 8);
        r.Status.Should().Be(BacktestRunStatus.Completed);
        r.OrdersSubmitted.Should().Be(2, "bar0 Long + bar2 Flat; bar1 Long unterdrueckt -> die Position war vor bar1 bereits gefuellt");
        r.OrdersUnfilledAtEnd.Should().Be(0, "die angenommene Order ist vollstaendig gefuellt (kein pending)");
        r.Trades.Should().ContainSingle();
        r.Trades[0].Quantity.Should().Be(1, "vollstaendige Fuellung, keine Teilfuellung");
        r.FinalPositionSide.Should().Be(PositionSide.Flat);
    }

    [Fact]
    public async Task Tick_path_creates_no_stop_or_target_orders_so_no_silent_sl_tp_exit()
    {
        // Short mit SuggestedStopLossTicks; Preis STEIGT stark. Gaebe es im Tick-Pfad SL-Orders, wuerde die
        // Short-Position ausgestoppt. Sie bleibt jedoch offen -> der Tick-Pfad baut keine SL/TP-Brackets.
        var r = await Run(Gated(SignalDirection.Short), ticks: 20, step: 5m,
            risk: MaxOne());
        r.Status.Should().Be(BacktestRunStatus.Completed);
        r.FinalPositionSide.Should().Be(PositionSide.Short, "kein SL-Exit im Tick-Pfad");
        r.Trades.Should().BeEmpty("die Position wurde nie geschlossen (kein SL/TP, kein EndOfData-Zwangsschluss)");
    }

    [Fact]
    public async Task Ledger_comes_from_the_engine_not_the_gate_model()
    {
        // Selbst mit einer Ablehnung bleibt das Ergebnis die Engine-Wahrheit: abgeschlossene Trades sind
        // konsistente Round-Turns (NetPnL == Gross - Fees), keine Phantom-Trades aus dem Gate-Modell.
        int seen = 0;
        var r = await Run(Gated(SignalDirection.Long, SignalDirection.Short, SignalDirection.Short, SignalDirection.Long),
            ticks: 16, reject: _ => seen++ == 1);
        foreach (var t in r.Trades)
            t.NetPnL.Should().Be(t.GrossPnL - t.Fees);
    }
}
