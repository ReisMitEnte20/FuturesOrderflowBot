using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Strategies;

/// <summary>
/// Opening-Range-Breakout (ORB), FESTE Baseline nach Zarattini &amp; Aziz, "Can Day Trading Really Be Profitable?"
/// (Concretum Group, 2023), Abschnitt 2 / Strategy Description — hier auf MES übertragen (REFERENZ, keine
/// Edge-Behauptung; die Paper-Renditen gelten für QQQ/TQQQ und werden NICHT auf MES übertragen).
///
/// Regeln (fix, keine Variantenoptimierung):
/// - Opening Range = die ERSTE vollständige 5-Minuten-Kerze ab 09:30 America/New_York (DST-korrekt).
/// - Richtung aus Close gegenüber Open dieser Kerze; Close == Open (Doji) ⇒ KEIN Trade.
/// - Einstieg am OPEN des NÄCHSTEN Bars; fehlt dieser Bar (Lücke), wird der Entry verworfen (kein späterer Fill).
/// - Höchstens EIN Trade pro Session.
/// - Stop am GEGENÜBERLIEGENDEN Extrem der ersten Kerze (Long = Low, Short = High); der Stop-PREIS wird mitgegeben,
///   die Engine leitet R aus dem tatsächlichen Entry-Fill ab.
/// - Ziel 10R; spätestens Glattstellung zum Session-Ende am Bar-Schluss.
///
/// Session-Behandlung (vor der Ausführung, nicht nur Doku): volle Feiertage werden über
/// <see cref="UsEquitySessionCalendar"/> AUSGESCHLOSSEN (kein Trade; in <see cref="ExcludedSessions"/> erfasst);
/// an verkürzten Handelstagen gilt das frühere Session-Ende (13:00 ET). Die Flatten-Deadline (Session-Ende, UTC)
/// wird mitgegeben, damit die Engine bei fehlendem Schluss-Bar keine Position still über Nacht weiterlaufen lässt.
///
/// Abweichungen zum Paper: Instrument MES statt QQQ/TQQQ; FESTE Kontraktzahl (1) statt risikobasierter Größe;
/// unsere (Beispiel-)Gebühren und (angenommene) Slippage. Die Zeitzone der Quelldaten ist UNBESTÄTIGT — vor einem
/// echten Lauf klären.
/// </summary>
public sealed class OpeningRangeBreakoutStrategy : IStrategy
{
    private static readonly TimeZoneInfo NewYork = UsEquitySessionCalendar.NewYork;
    private static readonly TimeSpan RthOpen = new(9, 30, 0);

    /// <summary>Ziel als R-Vielfaches (fix laut Paper-Baseline).</summary>
    private const decimal RewardMultiple = 10m;

    private DateOnly? _openingSeenDate;   // NY-Datum, dessen Opening-Bar bereits verarbeitet wurde
    private bool _entered;                // genau ein Trade pro Session
    private bool _flattened;              // Session-Exit bereits signalisiert
    private TimeSpan _sessionEnd = new(16, 0, 0);
    private readonly List<DateOnly> _excluded = new();

    /// <summary>NY-Daten, deren Session wegen vollen Feiertags ausgeschlossen wurde (für den Ergebnisbericht).</summary>
    public IReadOnlyList<DateOnly> ExcludedSessions => _excluded;

    public string Name => "OpeningRangeBreakoutStrategy";

    public void Initialize(StrategyExecutionContext context) => Reset();

    public TradeSignal? OnCandle(Candle c)
    {
        var nyOpen = TimeZoneInfo.ConvertTime(c.OpenTime, NewYork);
        var nyClose = TimeZoneInfo.ConvertTime(c.CloseTime, NewYork);
        var nyDate = DateOnly.FromDateTime(nyOpen.DateTime);

        // --- Opening Range: die erste 5-Min-Kerze, deren NY-Open exakt 09:30 ist (einmal je NY-Datum). ---
        if (nyOpen.TimeOfDay == RthOpen && _openingSeenDate != nyDate)
        {
            _openingSeenDate = nyDate;
            _entered = false;
            _flattened = false;

            // Voller Feiertag: keine reguläre Session → Session ausschließen (kein Trade).
            if (!UsEquitySessionCalendar.IsRegularSession(nyDate))
            {
                _excluded.Add(nyDate);
                return null;
            }

            if (c.Close == c.Open) return null;   // Doji ⇒ kein Trade

            _sessionEnd = UsEquitySessionCalendar.SessionEnd(nyDate);
            var nyEndLocal = new DateTime(nyDate.Year, nyDate.Month, nyDate.Day,
                _sessionEnd.Hours, _sessionEnd.Minutes, 0, DateTimeKind.Unspecified);
            var deadlineUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(nyEndLocal, NewYork));

            var dir = c.Close > c.Open ? SignalDirection.Long : SignalDirection.Short;
            decimal stopPrice = dir == SignalDirection.Long ? c.Low : c.High;
            _entered = true;

            return new TradeSignal
            {
                StrategyName = Name,
                Symbol = c.Symbol,
                Direction = dir,
                Timestamp = c.CloseTime,
                ReferencePrice = c.Close,
                SuggestedQuantity = 1,
                SuggestedStopLossPrice = stopPrice,
                SuggestedTakeProfitRMultiple = RewardMultiple,
                EntryBarOpenTime = c.CloseTime,   // erwartetes Entry-Open = Schlusszeit der ersten Kerze (Folgebar-Open)
                FlattenDeadline = deadlineUtc,
                Reason = $"ORB {dir}: erste 5-Min-Kerze O={c.Open} C={c.Close}, Stop={stopPrice}, Ziel={RewardMultiple}R, " +
                         $"Session-Ende {_sessionEnd:hh\\:mm} ET"
            };
        }

        // --- Session-Ende: offene Position am ersten Bar mit NY-Schluss >= Session-Ende glattstellen. ---
        if (_openingSeenDate == nyDate && _entered && !_flattened && nyClose.TimeOfDay >= _sessionEnd)
        {
            _flattened = true;
            return new TradeSignal
            {
                StrategyName = Name,
                Symbol = c.Symbol,
                Direction = SignalDirection.Flat,
                Timestamp = c.CloseTime,
                ReferencePrice = c.Close,
                FlattenAtClose = true,
                Reason = $"ORB Session-Exit {_sessionEnd:hh\\:mm} America/New_York"
            };
        }

        return null;
    }

    public void Reset()
    {
        _openingSeenDate = null;
        _entered = false;
        _flattened = false;
        _sessionEnd = new(16, 0, 0);
        _excluded.Clear();
    }
}
