using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.MarketState;

/// <summary>Sich entwickelndes Profil (Volumen oder TPO): POC und Value Area 70 % – nur aus bisherigen Ereignissen.</summary>
public sealed record ProfileValues(decimal? Poc, decimal? Vah, decimal? Val, decimal Total, int Levels);

/// <summary>Bestätigter Swing: Extrempunkt (Bar/Zeit) und später liegender Bestätigungszeitpunkt (Ereignis/Zeit) getrennt.</summary>
public sealed record SwingPoint(string Kind, decimal Price, int ExtremeBarIndex, DateTimeOffset ExtremeBarStart, long ConfirmedAtEvent, DateTimeOffset ConfirmedAt);

/// <summary>Messbare Absorptionsbeobachtung (keine Behauptung über nachladende Limitorders).</summary>
public sealed record AbsorptionObservation(string Side, decimal PriceLow, decimal PriceHigh, DateTimeOffset From, DateTimeOffset To, decimal AggressiveVolume,
    decimal ProgressTicks, string Definition);

/// <summary>Kontextwerte zum Zeitpunkt eines Ereignisses (alle mit dokumentiertem Anker).</summary>
public sealed record ContextValues(
    decimal? SessionVwap, string VwapBasis, decimal Cvd, string CvdAnchor, ProfileValues VolumeProfile, ProfileValues TpoProfile, string TpoClock,
    decimal? SessionHigh, decimal? SessionLow, SwingPoint? LastSwingHigh, SwingPoint? LastSwingLow, string? StructureBreak, AbsorptionObservation? Absorption);

/// <summary>
/// Sessionkontext. Reset bei jedem Sessionstart (London/New York getrennt): VWAP, CVD, Profile, TPO, Session-Hoch/-Tief, Swings.
/// Konto/Tageslimit gehören NICHT hierher (Engine/RiskManager).
/// </summary>
public sealed class SessionContext
{
    public const string VwapBasisText = "Σ(Tradepreis × Volumen) / Σ Volumen ab Sessionbeginn (eigene Berechnung, nicht die ATAS-VWAP-Linie)";
    private readonly decimal _tick;
    private readonly TimeSpan _tpoPeriod;
    private readonly int _swingStrength;
    private readonly TimeSpan _absWindow;
    private readonly decimal _absMinVolume;
    private readonly int _absMaxProgressTicks;

    private DateTimeOffset? _sessionStart;
    private decimal _pv, _v, _cvd;
    private decimal? _hi, _lo;
    private readonly SortedDictionary<decimal, decimal> _volProfile = new();
    private readonly SortedDictionary<decimal, int> _tpo = new();
    private readonly HashSet<(int Period, decimal Price)> _tpoSeen = new();
    private SwingPoint? _swingHigh, _swingLow;
    private string? _structureBreak;
    private readonly Queue<(DateTimeOffset T, decimal P, decimal V, AggressorSide S)> _recent = new();

    public SessionContext(decimal tickSize, TimeSpan? tpoPeriod = null, int swingStrength = 2, TimeSpan? absorptionWindow = null,
        decimal absorptionMinVolume = 150, int absorptionMaxProgressTicks = 2)
    {
        _tick = tickSize;
        _tpoPeriod = tpoPeriod ?? TimeSpan.FromMinutes(30);
        _swingStrength = swingStrength;
        _absWindow = absorptionWindow ?? TimeSpan.FromSeconds(30);
        _absMinVolume = absorptionMinVolume;
        _absMaxProgressTicks = absorptionMaxProgressTicks;
    }

    public string TpoClockText => $"TPO je {_tpoPeriod.TotalMinutes:0} min ab Sessionbeginn {(_sessionStart is { } s ? s.ToString("HH:mm") + " UTC" : "")}; jede in einer Periode gehandelte Preisstufe zählt einmal";

    public void Reset(DateTimeOffset sessionStart)
    {
        _sessionStart = sessionStart;
        _pv = _v = _cvd = 0; _hi = _lo = null;
        _volProfile.Clear(); _tpo.Clear(); _tpoSeen.Clear();
        _swingHigh = _swingLow = null; _structureBreak = null;
        _recent.Clear();
    }

    public void OnEvent(MarketTick t)
    {
        _sessionStart ??= t.Timestamp;
        _pv += t.Price * t.Volume; _v += t.Volume;
        _cvd += t.Aggressor == AggressorSide.Buy ? t.Volume : t.Aggressor == AggressorSide.Sell ? -t.Volume : 0;
        _hi = _hi is null ? t.Price : Math.Max(_hi.Value, t.Price);
        _lo = _lo is null ? t.Price : Math.Min(_lo.Value, t.Price);
        _volProfile[t.Price] = _volProfile.GetValueOrDefault(t.Price) + t.Volume;
        int period = (int)((t.Timestamp - _sessionStart.Value).Ticks / _tpoPeriod.Ticks);
        if (_tpoSeen.Add((period, t.Price))) _tpo[t.Price] = _tpo.GetValueOrDefault(t.Price) + 1;
        _recent.Enqueue((t.Timestamp, t.Price, t.Volume, t.Aggressor));
        while (_recent.Count > 0 && t.Timestamp - _recent.Peek().T > _absWindow) _recent.Dequeue();
        // Strukturbruch: Kurs über/unter dem zuletzt BESTÄTIGTEN Swing.
        if (_swingHigh is not null && t.Price > _swingHigh.Price) _structureBreak = $"über Swinghoch {_swingHigh.Price} (bestätigt {_swingHigh.ConfirmedAt:HH:mm:ss})";
        else if (_swingLow is not null && t.Price < _swingLow.Price) _structureBreak = $"unter Swingtief {_swingLow.Price} (bestätigt {_swingLow.ConfirmedAt:HH:mm:ss})";
    }

    /// <summary>Nach einem 5-Minuten-Barabschluss: Swing an Bar i ist erst bestätigt, wenn i+N geschlossen ist.</summary>
    public void OnBarClosed(IReadOnlyList<FlowBar> bars, FlowBar closed)
    {
        int n = _swingStrength;
        int i = closed.Index - n;
        if (i < n) return;
        var c = bars[i];
        bool isHigh = true, isLow = true;
        for (int k = i - n; k <= i + n; k++)
        {
            if (k == i) continue;
            if (bars[k].High >= c.High) isHigh = false;
            if (bars[k].Low <= c.Low) isLow = false;
        }
        if (isHigh) _swingHigh = new SwingPoint("Hoch", c.High, c.Index, c.Start, closed.ClosedAtEvent!.Value, closed.ClosedAtTime!.Value);
        if (isLow) _swingLow = new SwingPoint("Tief", c.Low, c.Index, c.Start, closed.ClosedAtEvent!.Value, closed.ClosedAtTime!.Value);
    }

    public ContextValues Values()
    {
        return new ContextValues(_v > 0 ? Math.Round(_pv / _v, 4) : null, VwapBasisText, _cvd,
            _sessionStart is { } s ? $"Sessionbeginn {s:HH:mm} UTC" : "noch kein Ereignis",
            Profile(_volProfile.ToDictionary(k => k.Key, k => k.Value)), Profile(_tpo.ToDictionary(k => k.Key, k => (decimal)k.Value)), TpoClockText,
            _hi, _lo, _swingHigh, _swingLow, _structureBreak, Absorption());
    }

    /// <summary>POC = Stufe mit dem größten Wert (bei Gleichstand die niedrigere); Value Area: ab POC jeweils die größere
    /// Nachbarstufe hinzunehmen, bis ≥ 70 % des Gesamtwerts erreicht sind.</summary>
    public static ProfileValues Profile(IReadOnlyDictionary<decimal, decimal> levels)
    {
        if (levels.Count == 0) return new ProfileValues(null, null, null, 0, 0);
        var prices = levels.Keys.OrderBy(p => p).ToList();
        decimal total = levels.Values.Sum();
        int poc = 0;
        for (int i = 1; i < prices.Count; i++) if (levels[prices[i]] > levels[prices[poc]]) poc = i;
        int lo = poc, hi = poc;
        decimal acc = levels[prices[poc]];
        while (acc < total * 0.7m && (lo > 0 || hi < prices.Count - 1))
        {
            decimal up = hi < prices.Count - 1 ? levels[prices[hi + 1]] : -1, dn = lo > 0 ? levels[prices[lo - 1]] : -1;
            if (up >= dn) { hi++; acc += up; } else { lo--; acc += dn; }
        }
        return new ProfileValues(prices[poc], prices[hi], prices[lo], total, prices.Count);
    }

    /// <summary>Absorption (messbar): im Fenster aggressives Volumen einer Seite ≥ Schwelle, aber Preisfortschritt in diese
    /// Richtung ≤ N Ticks. Verkaufsabsorption = aggressive Verkäufe ohne Fortschritt nach unten (und umgekehrt).</summary>
    private AbsorptionObservation? Absorption()
    {
        if (_recent.Count < 2) return null;
        var first = _recent.Peek();
        decimal sell = 0, buy = 0, lo = decimal.MaxValue, hi = decimal.MinValue;
        DateTimeOffset last = first.T;
        foreach (var r in _recent)
        {
            if (r.S == AggressorSide.Sell) sell += r.V; else if (r.S == AggressorSide.Buy) buy += r.V;
            lo = Math.Min(lo, r.P); hi = Math.Max(hi, r.P); last = r.T;
        }
        string def = $"Fenster {_absWindow.TotalSeconds:0} s, aggressives Volumen ≥ {_absMinVolume}, Fortschritt ≤ {_absMaxProgressTicks} Ticks";
        decimal downTicks = (first.P - lo) / _tick, upTicks = (hi - first.P) / _tick;
        if (sell >= _absMinVolume && sell > buy && downTicks <= _absMaxProgressTicks)
            return new AbsorptionObservation("Verkaufsabsorption (aggressive Verkäufe ohne Fortschritt nach unten)", lo, hi, first.T, last, sell, downTicks, def);
        if (buy >= _absMinVolume && buy > sell && upTicks <= _absMaxProgressTicks)
            return new AbsorptionObservation("Kaufabsorption (aggressive Käufe ohne Fortschritt nach oben)", lo, hi, first.T, last, buy, upTicks, def);
        return null;
    }
}
