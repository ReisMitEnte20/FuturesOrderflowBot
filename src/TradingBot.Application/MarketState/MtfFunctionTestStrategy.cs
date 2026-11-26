using System.Globalization;
using TradingBot.Domain.Enums;

namespace TradingBot.Application.MarketState;

/// <summary>
/// TECHNISCHER Funktionstest (keine Handelsstrategie, nicht optimiert): beweist, dass eine Regel zum selben Ereignis
/// alle drei Sichten auswertet.
///   Trigger    (rangeXV, geschlossen): die gerade geschlossene RangeXV-Bar schließt in Richtung (Close &gt; Open bzw. &lt;).
///   Kontext    (5m, geschlossen):      Close der letzten GESCHLOSSENEN 5-Minuten-Bar über (Long) bzw. unter (Short) dem Session-VWAP.
///   Bestätigung(1m, geschlossen):      Delta der letzten GESCHLOSSENEN 1-Minuten-Bar ≥ +Schwelle bzw. ≤ −Schwelle.
///   Ausstieg   (rangeXV, geschlossen): RangeXV-Bar schließt gegen die Position.
/// Ausgewertet wird nur, wenn auf diesem Ereignis eine RangeXV-Bar geschlossen hat. Fehlt eine geschlossene 5m-/1m-Bar,
/// ist die Regel „nicht prüfbar“ und blockiert. Offene Bars werden nie mit ihren (noch unbekannten) Endwerten benutzt.
/// </summary>
public sealed class MtfFunctionTestStrategy : IMultiTimeframeStrategy
{
    public const string Id = "mtf-funktionstest";
    private readonly decimal _deltaMin;

    public MtfFunctionTestStrategy(decimal oneMinuteDeltaMin = 20m) => _deltaMin = oneMinuteDeltaMin;

    public string Name => "MTF-Funktionstest";
    public string Version => "mtf-funktionstest-1";
    public IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string>
    {
        ["oneMinuteDeltaMin"] = _deltaMin.ToString(CultureInfo.InvariantCulture) + " Kontrakte (Delta der letzten geschlossenen 1m-Bar)",
        ["context5m"] = "Close letzte geschlossene 5m-Bar vs. Session-VWAP",
        ["trigger"] = "Richtung der gerade geschlossenen rangeXV-Bar",
    };
    public IReadOnlyList<string> Requires => new[] { "trades", "aggressor" };
    public void Reset() { }

    private static string N(decimal? d) => d is { } v ? v.ToString("0.####", CultureInfo.InvariantCulture) : "–";

    public StrategyEvaluation? Evaluate(MarketSnapshot s)
    {
        if (!s.R4.ClosedOnThisEvent) return null;
        var r = s.R4.LastClosed!;
        var used = new List<string> { "rangeXV: geschlossene Bar (Open/Close)" };

        // ---- Ausstieg
        if (s.Position != PositionSide.Flat)
        {
            bool against = s.Position == PositionSide.Long ? r.Close < r.Open : r.Close > r.Open;
            var chk = new RuleCheck("Ausstieg: rangeXV-Bar schließt gegen die Position", "Ausstieg", r.View, "geschlossen",
                $"Open {N(r.Open)} → Close {N(r.Close)}", s.Position == PositionSide.Long ? "Close < Open" : "Close > Open", against ? RuleResult.Met : RuleResult.Violated);
            return against
                ? new StrategyEvaluation(SignalDirection.Flat, "ausstieg", new[] { chk }, $"Ausstieg: rangeXV-Bar #{r.Index} gegen {s.Position}", used)
                : null;
        }

        // ---- Einstieg: Trigger zuerst bestimmen (Richtung), dann Kontext und Bestätigung prüfen
        SignalDirection? dir = r.Close > r.Open ? SignalDirection.Long : r.Close < r.Open ? SignalDirection.Short : null;
        var checks = new List<RuleCheck>
        {
            new("Trigger: geschlossene rangeXV-Bar mit Richtung", "Trigger", r.View, "geschlossen", $"Open {N(r.Open)} → Close {N(r.Close)}", "Close ≠ Open",
                dir is null ? RuleResult.Violated : RuleResult.Met)
        };
        if (dir is null) return new StrategyEvaluation(null, "keine_richtung", checks, "rangeXV-Bar ohne Richtung", used);
        bool longSide = dir == SignalDirection.Long;

        var c5 = s.M5.LastClosed;
        var vwap = s.Context.SessionVwap;
        used.Add("5m: Close der letzten geschlossenen Bar"); used.Add("Session-VWAP");
        checks.Add(c5 is null || vwap is null
            ? new RuleCheck("Kontext 5m: Close vs. Session-VWAP", "Kontext", "5m", "geschlossen", c5 is null ? "noch keine geschlossene 5m-Bar" : "kein VWAP", longSide ? "Close > VWAP" : "Close < VWAP", RuleResult.NotTestable)
            : new RuleCheck("Kontext 5m: Close vs. Session-VWAP", "Kontext", "5m", "geschlossen", $"Close {N(c5.Close)} (Bar {c5.Start:HH:mm}) · VWAP {N(vwap)}",
                longSide ? "Close > VWAP" : "Close < VWAP", (longSide ? c5.Close > vwap : c5.Close < vwap) ? RuleResult.Met : RuleResult.Violated));

        var c1 = s.M1.LastClosed;
        used.Add("1m: Delta der letzten geschlossenen Bar");
        checks.Add(c1 is null
            ? new RuleCheck("Bestätigung 1m: Delta", "Bestätigung", "1m", "geschlossen", "noch keine geschlossene 1m-Bar", longSide ? $"Delta ≥ {N(_deltaMin)}" : $"Delta ≤ −{N(_deltaMin)}", RuleResult.NotTestable)
            : new RuleCheck("Bestätigung 1m: Delta", "Bestätigung", "1m", "geschlossen", $"Delta {N(c1.Delta)} (Bar {c1.Start:HH:mm})",
                longSide ? $"Delta ≥ {N(_deltaMin)}" : $"Delta ≤ −{N(_deltaMin)}", (longSide ? c1.Delta >= _deltaMin : c1.Delta <= -_deltaMin) ? RuleResult.Met : RuleResult.Violated));

        bool ok = checks.All(c => c.Result == RuleResult.Met);
        string sum = ok
            ? $"5m-Kontext erfüllt; 1m-Bestätigung erfüllt; Trigger rangeXV-Bar #{r.Index} bei Ereignis {s.EventIndex} → {dir}"
            : $"Kandidat {dir} abgelehnt bei Ereignis {s.EventIndex}: " + string.Join("; ", checks.Where(c => c.Result != RuleResult.Met).Select(c => $"{c.Rule} {c.Result} ({c.Actual})"));
        return new StrategyEvaluation(ok ? dir : null, ok ? "einstieg" : "kandidat_abgelehnt", checks, sum, used);
    }
}
