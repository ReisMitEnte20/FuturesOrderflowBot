using TradingBot.Application.Sessions;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.MarketState;

/// <summary>Eine Sicht zum Auswertungszeitpunkt (Bars nur bis einschließlich des aktuellen Ereignisses).</summary>
public sealed class MarketView
{
    private readonly BarBuilder _b;
    internal MarketView(BarBuilder b) => _b = b;
    public string Name => _b.View;
    /// <summary>Offene Bar (bisherige Werte, NIE endgültig).</summary>
    public FlowBar? Current => _b.Current;
    /// <summary>Zuletzt geschlossene Bar (endgültige Werte).</summary>
    public FlowBar? LastClosed => _b.LastClosed;
    public bool ClosedOnThisEvent => _b.ClosedOnLastEvent;
    public int ClosedCount => _b.ClosedCount;
    public IReadOnlyList<FlowBar> Bars => _b.All;
}

/// <summary>Konsistenter Zustand aller drei Sichten nach Ereignis <see cref="EventIndex"/>.</summary>
public sealed class MarketSnapshot
{
    private readonly Func<ContextValues> _ctx;
    private ContextValues? _cached;
    internal MarketSnapshot(long ev, DateTimeOffset t, decimal price, string session, MarketView m5, MarketView m1, MarketView r4, Func<ContextValues> ctx,
        PositionSide position)
    { EventIndex = ev; MarketTime = t; Price = price; Session = session; M5 = m5; M1 = m1; R4 = r4; _ctx = ctx; Position = position; }
    public long EventIndex { get; }
    public DateTimeOffset MarketTime { get; }
    public decimal Price { get; }
    public string Session { get; }
    public MarketView M5 { get; }
    public MarketView M1 { get; }
    public MarketView R4 { get; }
    public PositionSide Position { get; }
    public ContextValues Context => _cached ??= _ctx();
}

/// <summary>Eine tatsächlich geprüfte Regel: Istwert, Sollwert, Ergebnis (erfüllt | verletzt | nicht_pruefbar).</summary>
public sealed record RuleCheck(string Rule, string Role, string View, string BarStatus, string Actual, string Expected, string Result);

public static class RuleResult { public const string Met = "erfüllt", Violated = "verletzt", NotTestable = "nicht_pruefbar"; }

/// <summary>Ergebnis einer Strategieauswertung. Signal = null mit Trigger erfüllt = abgelehnter Kandidat (Beinahe-Einstieg).</summary>
public sealed record StrategyEvaluation(SignalDirection? Signal, string Kind, IReadOnlyList<RuleCheck> Checks, string Summary,
    IReadOnlyList<string> ContextUsed);

public interface IMultiTimeframeStrategy
{
    string Name { get; }
    string Version { get; }
    IReadOnlyDictionary<string, string> Parameters { get; }
    /// <summary>Datenbedarf, der vor dem Start geprüft wird (z. B. „aggressor“, „dom_historisch“).</summary>
    IReadOnlyList<string> Requires { get; }
    /// <summary>Nach JEDEM Ereignis aufgerufen; null = keine Auswertung an diesem Ereignis.</summary>
    StrategyEvaluation? Evaluate(MarketSnapshot s);
    void Reset();
}

/// <summary>Entscheidungsspur – gespeichert im Moment der Entscheidung (nicht beim Öffnen des Berichts).</summary>
public sealed record DecisionTrace(
    int Id, string Kind, long EventIndex, DateTimeOffset MarketTime, decimal Price, string Session, string? Direction, Guid? SignalId,
    string Strategy, string StrategyVersion, IReadOnlyDictionary<string, string> Parameters,
    BarSnapshot? M5Open, BarSnapshot? M5Closed, BarSnapshot? M1Open, BarSnapshot? M1Closed, BarSnapshot? R4Open, BarSnapshot? R4Closed,
    ContextValues Context, IReadOnlyList<RuleCheck> Checks, string Summary, IReadOnlyList<string> ContextUsed, string Position);

/// <summary>Positionswechsel, wie er beim Ereignis beobachtet wurde (Fills laufen in der Engine VOR der Strategie).</summary>
public sealed record PositionTransition(long EventIndex, DateTimeOffset MarketTime, string From, string To, decimal AvgPrice, decimal TickPrice);

/// <summary>
/// Hängt die 5-Minuten-, 1-Minuten- und RangeXV-Sicht samt Sessionkontext an den BESTEHENDEN Tick-Pfad (BacktestEngine →
/// RiskManager → OrderManager → PositionManager). Ist selbst ein normales <see cref="IStrategy"/> – keine zweite Engine.
/// Alle Sichten werden bei jedem Ereignis fortgeschrieben, auch ohne Browser.
/// </summary>
public sealed class MultiTimeframeHost : IStrategy, IPositionManagerAware, ISessionAware
{
    private readonly IMultiTimeframeStrategy _s;
    private readonly TimeBarBuilder _m5 = new("5m", TimeSpan.FromMinutes(5));
    private readonly TimeBarBuilder _m1 = new("1m", TimeSpan.FromMinutes(1));
    private readonly RangeXvBarBuilder _r4;
    private readonly SessionContext _ctx;
    private readonly MarketView _v5, _v1, _vr;
    private readonly Func<DateTimeOffset, string> _sessionOf;
    private readonly int _maxRejected;
    private IPositionManager? _positions;
    private long _ev;
    private string _session = "";
    private string _lastPos = "Flat";
    private MarketTick? _last;
    private readonly List<DecisionTrace> _traces = new();
    private readonly List<PositionTransition> _transitions = new();
    private int _rejected;

    /// <summary>Optional: wird nach jedem Ereignis aufgerufen (Rewind-Aufzeichnung). Darf den Zustand nur lesen.</summary>
    public Action<MarketSnapshot, MarketTick>? AfterEvent { get; set; }

    public MultiTimeframeHost(IMultiTimeframeStrategy strategy, decimal tickSize, int rangePeriod = 4, Func<DateTimeOffset, string>? sessionOf = null,
        SessionContext? context = null, int maxRejectedTraces = 200)
    {
        _s = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _r4 = new RangeXvBarBuilder($"rangeXV {rangePeriod}", rangePeriod, tickSize);
        _ctx = context ?? new SessionContext(tickSize);
        _v5 = new MarketView(_m5); _v1 = new MarketView(_m1); _vr = new MarketView(_r4);
        _sessionOf = sessionOf ?? (_ => "Session");
        _maxRejected = maxRejectedTraces;
    }

    public string Name => _s.Name;
    public IReadOnlyList<DecisionTrace> Traces => _traces;
    public IReadOnlyList<PositionTransition> Transitions => _transitions;
    public long EventCount => _ev;
    public string RangeSemantics => RangeXvBarBuilder.SemanticsNote;
    public IReadOnlyList<FlowBar> Bars5 => _m5.All;
    public IReadOnlyList<FlowBar> Bars1 => _m1.All;
    public IReadOnlyList<FlowBar> BarsR => _r4.All;
    public ContextValues CurrentContext() => _ctx.Values();

    public void UsePositionManager(IPositionManager positions) => _positions = positions;

    public int OnSessionStart(SessionSegment segment)
    {
        // Keine Bar spannt über die Pause: offene Bars werden mit dem ersten Ereignis der neuen Session als geschlossen bekannt.
        if (_last is not null)
        {
            _m5.CloseOpenBar(_ev + 1, segment.StartUtc); _m1.CloseOpenBar(_ev + 1, segment.StartUtc); _r4.CloseOpenBar(_ev + 1, segment.StartUtc);
        }
        _ctx.Reset(segment.StartUtc);
        _session = segment.Label;
        return 0;
    }

    public TradeSignal? OnTick(MarketTick tick)
    {
        _ev++;
        if (_last is null && _session.Length == 0) { _session = _sessionOf(tick.Timestamp); _ctx.Reset(tick.Timestamp); }
        // Position nach den Fills dieses Ereignisses (die Engine füllt offene Orders VOR der Strategie).
        var pos = _positions?.GetPosition(tick.Symbol);
        string posText = pos is null || pos.Quantity == 0 || pos.Side == PositionSide.Flat ? "Flat" : $"{pos.Side} {pos.Quantity}";
        if (posText != _lastPos)
        {
            _transitions.Add(new PositionTransition(_ev, tick.Timestamp, _lastPos, posText, pos?.AverageEntryPrice ?? 0, tick.Price));
            _lastPos = posText;
        }
        // 1. Barabschlüsse erkennen + 2. Ereignis anwenden (alle drei Sichten, immer).
        _m5.OnEvent(_ev, tick);
        if (_m5.ClosedOnLastEvent) _ctx.OnBarClosed(_m5.All, _m5.LastClosed!);
        _m1.OnEvent(_ev, tick);
        _r4.OnEvent(_ev, tick);
        _ctx.OnEvent(tick);
        _last = tick;

        // 3. Strategie wertet den Zustand NACH diesem Ereignis aus.
        var snap = new MarketSnapshot(_ev, tick.Timestamp, tick.Price, _session, _v5, _v1, _vr, _ctx.Values,
            pos?.Side is { } sd && pos.Quantity != 0 ? sd : PositionSide.Flat);
        var eval = _s.Evaluate(snap);
        TradeSignal? signal = null;
        if (eval is not null)
        {
            if (eval.Signal is { } dir)
                signal = new TradeSignal
                {
                    StrategyName = _s.Name, Symbol = tick.Symbol, Direction = dir, Timestamp = tick.Timestamp, ReferencePrice = tick.Price,
                    SuggestedQuantity = 1, Reason = eval.Summary
                };
            bool keep = eval.Signal is not null || (_rejected < _maxRejected && eval.Kind == "kandidat_abgelehnt");
            if (keep)
            {
                if (eval.Signal is null) _rejected++;
                _traces.Add(new DecisionTrace(_traces.Count, eval.Kind, _ev, tick.Timestamp, tick.Price, _session, eval.Signal?.ToString(), signal?.SignalId,
                    _s.Name, _s.Version, _s.Parameters, _m5.Current?.Snapshot(), _m5.LastClosed?.Snapshot(), _m1.Current?.Snapshot(), _m1.LastClosed?.Snapshot(),
                    _r4.Current?.Snapshot(), _r4.LastClosed?.Snapshot(), snap.Context, eval.Checks, eval.Summary, eval.ContextUsed, posText));
            }
        }
        AfterEvent?.Invoke(snap, tick);
        return signal;
    }

    public TradeSignal? OnCandle(Candle candle) => null;
    public TradeSignal? OnOrderFlowBar(OrderFlowBar bar) => null;
    public void Reset() => _s.Reset();
}
