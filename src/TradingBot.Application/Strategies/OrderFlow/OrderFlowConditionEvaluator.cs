using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Strategies.OrderFlow;

/// <summary>
/// Modulare Orderflow-Checks über einem rollenden Bar-Fenster. Rein und deterministisch.
///
/// Alle Checks liefern <see cref="ConditionResult"/> mit Met / NotMet / InsufficientData –
/// bei fehlender Datenbasis (zu wenige Bars, fehlendes Instrument, fehlende Klassifikation,
/// fehlende Footprint-/Volume-Profile-Daten) wird NIEMALS geraten.
///
/// Die konkreten Formeln sind bewusst einfache, dokumentierte TEMPLATE-Proxys –
/// die echten Setup-Regeln des Traders ersetzen sie später (siehe docs/ORDERFLOW_STRATEGY_TEMPLATE.md).
/// VWAP ist bar-basiert (Σ TypischerPreis×Volumen / Σ Volumen) – eine Näherung, kein Tick-VWAP.
/// </summary>
public sealed class OrderFlowConditionEvaluator
{
    private readonly OrderFlowTemplateParameters _p;
    private readonly InstrumentProfile? _instrument;
    private readonly List<OrderFlowBar> _bars = new();

    private decimal _sessionHigh = decimal.MinValue;
    private decimal _sessionLow = decimal.MaxValue;
    private decimal _vwapPriceVolume;
    private decimal _vwapVolume;

    public OrderFlowConditionEvaluator(OrderFlowTemplateParameters parameters, InstrumentProfile? instrument)
    {
        _p = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _instrument = instrument;
    }

    public int BarCount => _bars.Count;
    public decimal? SessionHigh => _bars.Count > 0 ? _sessionHigh : null;
    public decimal? SessionLow => _bars.Count > 0 ? _sessionLow : null;
    public decimal? Vwap => _vwapVolume > 0m ? _vwapPriceVolume / _vwapVolume : null;

    private OrderFlowBar Current => _bars[^1];
    private IReadOnlyList<OrderFlowBar> Previous => _bars.Count > 1 ? _bars.GetRange(0, _bars.Count - 1) : Array.Empty<OrderFlowBar>();

    /// <summary>Nimmt die nächste Bar auf (Session-High/Low + VWAP laufen über die GESAMTE Session).</summary>
    public void Add(OrderFlowBar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);

        if (bar.High > _sessionHigh) _sessionHigh = bar.High;
        if (bar.Low < _sessionLow) _sessionLow = bar.Low;

        decimal typical = (bar.High + bar.Low + bar.Close) / 3m;
        _vwapPriceVolume += typical * bar.TotalVolume;
        _vwapVolume += bar.TotalVolume;

        _bars.Add(bar);
        if (_bars.Count > _p.LookbackBars)
            _bars.RemoveAt(0); // rollendes Fenster; Session-Werte bleiben kumulativ
    }

    public void Reset()
    {
        _bars.Clear();
        _sessionHigh = decimal.MinValue;
        _sessionLow = decimal.MaxValue;
        _vwapPriceVolume = 0m;
        _vwapVolume = 0m;
    }

    // ===================== Confirmations =====================

    /// <summary>Delta-Divergenz: neues Extrem im Fenster, aber Delta läuft dagegen.</summary>
    public ConditionResult DeltaDivergence(SignalDirection direction)
    {
        const string name = "DeltaDivergence";
        if (_bars.Count < 2) return ConditionResult.Insufficient(name, "mindestens 2 Bars nötig");

        var cur = Current;
        var prev = _bars[^2];

        if (direction == SignalDirection.Long)
        {
            bool newLow = cur.Low < Previous.Min(b => b.Low);
            bool deltaRising = cur.Delta > prev.Delta;
            return newLow && deltaRising
                ? ConditionResult.Met(name, $"neues Tief {cur.Low} bei steigendem Delta ({prev.Delta}→{cur.Delta})")
                : ConditionResult.NotMet(name);
        }

        bool newHigh = cur.High > Previous.Max(b => b.High);
        bool deltaFalling = cur.Delta < prev.Delta;
        return newHigh && deltaFalling
            ? ConditionResult.Met(name, $"neues Hoch {cur.High} bei fallendem Delta ({prev.Delta}→{cur.Delta})")
            : ConditionResult.NotMet(name);
    }

    /// <summary>Absorption: überdurchschnittliches Volumen bei kleiner Range, Close hält gegen die Aggressoren.</summary>
    public ConditionResult Absorption(SignalDirection direction)
    {
        const string name = "Absorption";
        if (_bars.Count < 2) return ConditionResult.Insufficient(name, "mindestens 2 Bars nötig");
        if (_instrument is null) return ConditionResult.Insufficient(name, "InstrumentProfile (TickSize) fehlt");

        var cur = Current;
        decimal avgVol = Previous.Average(b => b.TotalVolume);
        if (avgVol <= 0m) return ConditionResult.Insufficient(name, "kein Vergleichsvolumen");

        bool highVolume = cur.TotalVolume >= _p.AbsorptionThreshold * avgVol;
        bool smallRange = cur.Range <= _p.AbsorptionMaxRangeTicks * _instrument.TickSize;
        decimal mid = (cur.High + cur.Low) / 2m;

        // Long: aggressive Verkäufer (Delta<0) werden absorbiert, Close hält in der oberen Hälfte.
        bool directionOk = direction == SignalDirection.Long
            ? cur.Delta < 0m && cur.Close >= mid
            : cur.Delta > 0m && cur.Close <= mid;

        return highVolume && smallRange && directionOk
            ? ConditionResult.Met(name, $"Vol {cur.TotalVolume} ≥ {_p.AbsorptionThreshold}×Ø{avgVol:F0}, Range {cur.Range} klein, Delta {cur.Delta}")
            : ConditionResult.NotMet(name);
    }

    /// <summary>Liquidity Sweep: Extrem der Session wird gestoßen, Preis kehrt zurück (Close wieder drin).</summary>
    public ConditionResult LiquiditySweep(SignalDirection direction)
    {
        const string name = "LiquiditySweep";
        if (_bars.Count < 2) return ConditionResult.Insufficient(name, "mindestens 2 Bars nötig");

        var cur = Current;
        if (direction == SignalDirection.Long)
        {
            decimal prevSessionLow = Previous.Min(b => b.Low);
            return cur.Low < prevSessionLow && cur.Close > prevSessionLow
                ? ConditionResult.Met(name, $"Sweep unter Session-Tief {prevSessionLow}, Close {cur.Close} zurück darüber")
                : ConditionResult.NotMet(name);
        }

        decimal prevSessionHigh = Previous.Max(b => b.High);
        return cur.High > prevSessionHigh && cur.Close < prevSessionHigh
            ? ConditionResult.Met(name, $"Sweep über Session-Hoch {prevSessionHigh}, Close {cur.Close} zurück darunter")
            : ConditionResult.NotMet(name);
    }

    /// <summary>CVD-Bestätigung: kumulatives Delta läuft in Signalrichtung (vs. Fensterbeginn).</summary>
    public ConditionResult CvdConfirmation(SignalDirection direction)
    {
        const string name = "CvdConfirmation";
        if (_bars.Count < 2) return ConditionResult.Insufficient(name, "mindestens 2 Bars nötig");

        decimal cvdStart = _bars[0].CumulativeDelta;
        decimal cvdNow = Current.CumulativeDelta;
        bool ok = direction == SignalDirection.Long ? cvdNow > cvdStart : cvdNow < cvdStart;
        return ok
            ? ConditionResult.Met(name, $"CVD {cvdStart}→{cvdNow}")
            : ConditionResult.NotMet(name, $"CVD {cvdStart}→{cvdNow}");
    }

    /// <summary>Volume Spike in Signalrichtung (Spike + Delta-Vorzeichen passt).</summary>
    public ConditionResult VolumeSpike(SignalDirection direction)
    {
        const string name = "VolumeSpike";
        if (_bars.Count < 2) return ConditionResult.Insufficient(name, "mindestens 2 Bars nötig");

        var cur = Current;
        decimal avgVol = Previous.Average(b => b.TotalVolume);
        if (avgVol <= 0m) return ConditionResult.Insufficient(name, "kein Vergleichsvolumen");

        bool spike = cur.TotalVolume >= _p.VolumeSpikeFactor * avgVol;
        bool directionOk = direction == SignalDirection.Long ? cur.Delta > 0m : cur.Delta < 0m;
        return spike && directionOk
            ? ConditionResult.Met(name, $"Vol {cur.TotalVolume} ≥ {_p.VolumeSpikeFactor}×Ø{avgVol:F0}, Delta {cur.Delta}")
            : ConditionResult.NotMet(name);
    }

    /// <summary>Reversal-Bestätigung: Vorbar gegen, aktuelle Bar mit Signalrichtung inkl. Delta.</summary>
    public ConditionResult ReversalConfirmation(SignalDirection direction)
    {
        const string name = "ReversalConfirmation";
        if (_bars.Count < 2) return ConditionResult.Insufficient(name, "mindestens 2 Bars nötig");

        var cur = Current;
        var prev = _bars[^2];
        bool ok = direction == SignalDirection.Long
            ? prev.Close < prev.Open && cur.IsBullish && cur.Delta > 0m
            : prev.Close > prev.Open && !cur.IsBullish && cur.Close < cur.Open && cur.Delta < 0m;
        return ok ? ConditionResult.Met(name, $"Umkehr mit Delta {cur.Delta}") : ConditionResult.NotMet(name);
    }

    /// <summary>Breakout-Bestätigung: Close jenseits des Fenster-Extrems mit Volumen + Delta.</summary>
    public ConditionResult BreakoutConfirmation(SignalDirection direction)
    {
        const string name = "BreakoutConfirmation";
        if (_bars.Count < 2) return ConditionResult.Insufficient(name, "mindestens 2 Bars nötig");

        var cur = Current;
        decimal avgVol = Previous.Average(b => b.TotalVolume);
        bool volumeOk = avgVol > 0m && cur.TotalVolume >= avgVol;

        bool ok = direction == SignalDirection.Long
            ? cur.Close > Previous.Max(b => b.High) && cur.Delta > 0m && volumeOk
            : cur.Close < Previous.Min(b => b.Low) && cur.Delta < 0m && volumeOk;
        return ok ? ConditionResult.Met(name, $"Breakout-Close {cur.Close} mit Delta {cur.Delta}") : ConditionResult.NotMet(name);
    }

    /// <summary>Bar-Level-Imbalance (Ask- vs. Bid-Volumen der GESAMTEN Bar) – bewusst NICHT "stacked".</summary>
    public ConditionResult BarImbalance(SignalDirection direction)
    {
        const string name = "BarImbalance";
        if (_bars.Count < 1) return ConditionResult.Insufficient(name, "keine Bar vorhanden");

        var cur = Current;
        if (cur.BidVolume + cur.AskVolume <= 0m)
            return ConditionResult.Insufficient(name, "keine echte Bid/Ask-Klassifikation");

        bool ok = direction == SignalDirection.Long
            ? cur.AskVolume >= _p.ImbalanceRatio * Math.Max(cur.BidVolume, 1m)
            : cur.BidVolume >= _p.ImbalanceRatio * Math.Max(cur.AskVolume, 1m);
        return ok
            ? ConditionResult.Met(name, $"Ask {cur.AskVolume} / Bid {cur.BidVolume} (Ratio ≥ {_p.ImbalanceRatio})")
            : ConditionResult.NotMet(name);
    }

    /// <summary>
    /// Stacked Imbalances benötigen FOOTPRINT-Daten (Bid/Ask je Preislevel). OrderFlowBar liefert
    /// nur Bar-Summen → ehrlich InsufficientData. KEIN Bar-Proxy unter diesem Namen.
    /// </summary>
    public ConditionResult StackedImbalances()
        => ConditionResult.Insufficient("StackedImbalances",
            "benötigt Footprint-Daten (Bid/Ask je Preislevel); OrderFlowBar liefert nur Bar-Summen");

    /// <summary>
    /// HVN/LVN-Filter benötigt Volume-Profile-Daten (Volumen je Preislevel) → ehrlich InsufficientData.
    /// </summary>
    public ConditionResult HvnLvnFilter()
        => ConditionResult.Insufficient("HvnLvnFilter",
            "benötigt Volume-Profile-Daten (Volumen je Preislevel); derzeit nicht verfügbar");

    // ===================== Filter =====================

    /// <summary>VWAP-Distanz-Filter: Close darf höchstens X Ticks vom (bar-basierten) VWAP entfernt sein.</summary>
    public ConditionResult VwapDistance()
    {
        const string name = "VwapDistanceFilter";
        if (_instrument is null) return ConditionResult.Insufficient(name, "InstrumentProfile (TickSize) fehlt");
        if (Vwap is not decimal vwap) return ConditionResult.Insufficient(name, "kein Volumen für VWAP");

        decimal distance = Math.Abs(Current.Close - vwap);
        decimal maxDistance = _p.MaxDistanceFromVwapTicks * _instrument.TickSize;
        return distance <= maxDistance
            ? ConditionResult.Met(name, $"Distanz {distance:F2} ≤ {maxDistance:F2}")
            : ConditionResult.NotMet(name, $"Distanz {distance:F2} > {maxDistance:F2}");
    }

    /// <summary>Session-High/Low-Reversal-Zone: Long nahe Session-Tief, Short nahe Session-Hoch.</summary>
    public ConditionResult SessionHighLowProximity(SignalDirection direction)
    {
        const string name = "SessionHighLowFilter";
        if (_instrument is null) return ConditionResult.Insufficient(name, "InstrumentProfile (TickSize) fehlt");
        if (_bars.Count < 1) return ConditionResult.Insufficient(name, "keine Bar vorhanden");

        decimal proximity = _p.SessionHighLowProximityTicks * _instrument.TickSize;
        var cur = Current;
        bool ok = direction == SignalDirection.Long
            ? cur.Low - _sessionLow <= proximity
            : _sessionHigh - cur.High <= proximity;
        return ok
            ? ConditionResult.Met(name, direction == SignalDirection.Long
                ? $"nahe Session-Tief {_sessionLow}" : $"nahe Session-Hoch {_sessionHigh}")
            : ConditionResult.NotMet(name);
    }

    /// <summary>Basis-Filter: Mindest-|Delta| und Mindest-Volumen der aktuellen Bar.</summary>
    public ConditionResult BaseFilters()
    {
        const string name = "BaseFilters";
        if (_bars.Count < 1) return ConditionResult.Insufficient(name, "keine Bar vorhanden");

        var cur = Current;
        if (cur.TotalVolume < _p.MinVolume)
            return ConditionResult.NotMet(name, $"Volumen {cur.TotalVolume} < MinVolume {_p.MinVolume}");
        if (Math.Abs(cur.Delta) < _p.MinDelta)
            return ConditionResult.NotMet(name, $"|Delta| {Math.Abs(cur.Delta)} < MinDelta {_p.MinDelta}");
        return ConditionResult.Met(name);
    }
}
