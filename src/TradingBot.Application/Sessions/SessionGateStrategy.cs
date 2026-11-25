using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Sessions;

/// <summary>Ein tatsächlich gespeistes Sessionfenster eines Laufs (geplantes UTC-Fenster + Close-Out-Grenze).</summary>
public sealed record SessionSegment(string Id, string SessionKey, string Label, DateOnly Day,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset CloseOutUtc);

/// <summary>Strategien/Wrapper mit sessionbezogenem Zustand (z. B. Teilbar, Session-CVD) setzen ihn hier zurück.</summary>
public interface ISessionAware
{
    /// <summary>Wird VOR dem ersten Tick einer neuen Session aufgerufen. Rückgabe: Anzahl verworfener Teilbar-Ticks.</summary>
    int OnSessionStart(SessionSegment segment);
}

/// <summary>Beobachtung des Session-Gates je Segment (rein beschreibend; Trades/PnL stammen allein aus der Engine).</summary>
public sealed class SessionSegmentObservation
{
    public required SessionSegment Segment { get; init; }
    public DateTimeOffset? FirstTickUtc { get; internal set; }
    public decimal? FirstTickPrice { get; internal set; }
    public DateTimeOffset? LastTickUtc { get; internal set; }
    public decimal? LastTickPrice { get; internal set; }
    public long Ticks { get; internal set; }
    public decimal Volume { get; internal set; }
    internal decimal PriceVolume { get; set; }
    /// <summary>Session-VWAP (startet je Session neu).</summary>
    public decimal? SessionVwap => Volume > 0 ? Math.Round(PriceVolume / Volume, 4) : null;
    /// <summary>Session-CVD = Σ (Käufer- − Verkäufer-Aggressorvolumen) ab Sessionstart.</summary>
    public decimal SessionCvd { get; internal set; }
    public int DiscardedPartialBarTicksAtStart { get; internal set; }
    public int EntrySignalsPassed { get; internal set; }
    public int FlatSignalsPassed { get; internal set; }
    public int EntrySignalsSuppressedAfterCloseOut { get; internal set; }
    public DateTimeOffset? CloseOutReachedUtc { get; internal set; }
    public string PositionAtCloseOut { get; internal set; } = "";
    public DateTimeOffset? SessionCloseSignalUtc { get; internal set; }
    public decimal? SessionCloseSignalPrice { get; internal set; }
    /// <summary>Position nach den Fills des letzten Ticks dieser Session (vor dessen eigenem Signal).</summary>
    public string PositionAtLastTick { get; internal set; } = "Flat";
    /// <summary>Am letzten Tick der Session noch ein Signal ausgegeben → Fill frühestens nach der Pause.</summary>
    public bool SignalOnLastTick { get; internal set; }
    public List<string> Incidents { get; } = new();
}

/// <summary>
/// Session-Gate um eine bestehende Strategie (z. B. OrderFlowBar-Aggregator + SinglePositionGate + DeltaFlow).
/// Erzeugt ausschließlich <see cref="TradeSignal"/>s (keine Orders) – Fills, Kosten, Position, PnL und Risiko bleiben bei
/// der bestehenden Engine bzw. dem RiskManager. Regeln (vorab festgelegt, ohne Lookahead):
/// <list type="bullet">
/// <item>Sessionstart: sessionbezogener Zustand der inneren Strategie wird zurückgesetzt (<see cref="ISessionAware"/>);
/// Session-VWAP/-CVD starten neu. Kontostand, Tages-PnL, Gebühren, Drawdown und Tagesrisikolimits laufen in der Engine weiter.</item>
/// <item>Ab <c>CloseOutUtc</c> (geplantes Sessionende minus Vorlauf, aus dem Plan bekannt – nicht aus dem Wissen, welcher Tick
/// der letzte ist): neue Einstiege werden unterdrückt; eine offene Position erhält EIN Flat-Signal, das die Engine mit dem
/// vorhandenen Fill-/Kostenmodell am nächsten Tick füllt.</item>
/// <item>Ist beim ersten Tick einer neuen Session trotzdem eine Position offen (kein zulässiger Fill vor der Grenze), wird das
/// als Vorfall ausgewiesen und sofort glattgestellt – nichts wird in der Pause erfunden.</item>
/// </list>
/// Die Zeit ist ausschließlich die Marktzeit der Ticks; in der Pause wird nicht gewartet.
/// </summary>
public sealed class SessionGateStrategy : IStrategy, IPositionManagerAware
{
    public const string SessionCloseStrategyName = "SessionClose";

    private readonly IStrategy _inner;
    private readonly IReadOnlyList<SessionSegment> _segments;
    private readonly List<SessionSegmentObservation> _obs = new();
    private IPositionManager? _positions;
    private int _seg = -1;
    private bool _flattenSent;
    private SessionSegmentObservation? _cur;

    public SessionGateStrategy(IStrategy inner, IReadOnlyList<SessionSegment> segments)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(segments);
        for (int i = 1; i < segments.Count; i++)
            if (segments[i].StartUtc < segments[i - 1].EndUtc)
                throw new ArgumentException($"Sessions überlappen: {segments[i - 1].Id} und {segments[i].Id} – Marktereignisse dürfen nicht doppelt laufen.");
        _segments = segments;
    }

    public string Name => _inner.Name;
    public StrategyDataRequirements DataRequirements => _inner.DataRequirements;
    public IReadOnlyList<SessionSegmentObservation> Observations => _obs;

    public void Initialize(StrategyExecutionContext context) => _inner.Initialize(context);

    public void UsePositionManager(IPositionManager positions)
    {
        _positions = positions;
        if (_inner is IPositionManagerAware aware) aware.UsePositionManager(positions);
    }

    public TradeSignal? OnTick(MarketTick tick)
    {
        // Segment zum Tick (Ticks sind chronologisch; außerhalb eines geplanten Fensters ist ein Fehler – fail-closed).
        int s = _seg < 0 ? 0 : _seg;
        while (s < _segments.Count && tick.Timestamp >= _segments[s].EndUtc) s++;
        if (s >= _segments.Count || tick.Timestamp < _segments[s].StartUtc)
            throw new InvalidOperationException($"Tick {tick.Timestamp:O} liegt in keinem geplanten Sessionfenster – Lauf abgebrochen (fail-closed).");

        TradeSignal? carriedFlatten = null;
        if (s != _seg)
        {
            if (_cur is not null && _cur.LastTickUtc is not null && _cur.SignalOnLastTick)
                _cur.Incidents.Add("Am letzten Tick vor der Pause wurde noch ein Signal ausgegeben – ein Fill ist erst nach der Pause möglich.");
            _seg = s;
            _flattenSent = false;
            var seg = _segments[s];
            _cur = new SessionSegmentObservation { Segment = seg };
            _obs.Add(_cur);
            if (_inner is ISessionAware aware) _cur.DiscardedPartialBarTicksAtStart = aware.OnSessionStart(seg);

            // Position nach den Fills dieses ersten Ticks: offen = über die Pause getragen → ausweisen und sofort schließen.
            var open = OpenPosition(tick.Symbol);
            if (open is not null && _obs.Count > 1)
            {
                _cur.Incidents.Add($"Position {Describe(open)} über die Pause getragen: beim ersten Tick nach der Pause noch offen (kein zulässiger Fill vor der Grenze) – sofort glattgestellt; Gap-Risiko.");
                carriedFlatten = Flatten(tick, open, "Glattstellung: Position über die Pause getragen");
                _flattenSent = true;
            }
        }

        var cur = _cur!;
        cur.FirstTickUtc ??= tick.Timestamp;
        cur.FirstTickPrice ??= tick.Price;
        cur.LastTickUtc = tick.Timestamp;
        cur.LastTickPrice = tick.Price;
        cur.Ticks++;
        cur.Volume += tick.Volume;
        cur.PriceVolume += tick.Price * tick.Volume;
        cur.SessionCvd += tick.Aggressor == AggressorSide.Buy ? tick.Volume : tick.Aggressor == AggressorSide.Sell ? -tick.Volume : 0m;
        var pos = OpenPosition(tick.Symbol);
        cur.PositionAtLastTick = pos is null ? "Flat" : Describe(pos);
        if (pos is null) _flattenSent = false; // glatt → eine spätere Position (z. B. Fill nach der Grenze) bekommt wieder ein Flat

        // Innere Strategie sieht JEDEN Tick (Bar-Zustand bleibt konsistent); ihr Signal wird ggf. unterdrückt.
        var sig = _inner.OnTick(tick);
        TradeSignal? result;
        if (carriedFlatten is not null) result = carriedFlatten;
        else if (tick.Timestamp >= cur.Segment.CloseOutUtc)
        {
            if (cur.CloseOutReachedUtc is null)
            {
                cur.CloseOutReachedUtc = tick.Timestamp;
                cur.PositionAtCloseOut = pos is null ? "Flat" : Describe(pos);
            }
            if (sig is not null && sig.Direction != SignalDirection.Flat) cur.EntrySignalsSuppressedAfterCloseOut++;
            if (pos is not null && !_flattenSent)
            {
                _flattenSent = true;
                cur.SessionCloseSignalUtc = tick.Timestamp;
                cur.SessionCloseSignalPrice = tick.Price;
                result = Flatten(tick, pos, "Session-Close-Regel: Close-Out-Grenze erreicht");
            }
            else result = null;
        }
        else
        {
            result = sig;
            if (sig is not null)
            {
                if (sig.Direction == SignalDirection.Flat) cur.FlatSignalsPassed++;
                else cur.EntrySignalsPassed++;
            }
        }
        cur.SignalOnLastTick = result is not null;
        return result;
    }

    public TradeSignal? OnCandle(Candle candle) => null;
    public TradeSignal? OnOrderFlowBar(OrderFlowBar bar) => null;

    public void Reset()
    {
        _inner.Reset();
        _obs.Clear();
        _seg = -1;
        _cur = null;
        _flattenSent = false;
    }

    private Position? OpenPosition(string symbol)
    {
        var p = _positions?.GetPosition(symbol);
        return p is null || p.Quantity == 0 || p.Side == PositionSide.Flat ? null : p;
    }

    private static string Describe(Position p) => FormattableString.Invariant($"{p.Side} {p.Quantity} @ {p.AverageEntryPrice}");

    private static TradeSignal Flatten(MarketTick tick, Position open, string reason) => new()
    {
        StrategyName = SessionCloseStrategyName,
        Symbol = tick.Symbol,
        Direction = SignalDirection.Flat,
        Timestamp = tick.Timestamp,
        ReferencePrice = tick.Price,
        SuggestedQuantity = open.Quantity,
        Reason = reason
    };
}
