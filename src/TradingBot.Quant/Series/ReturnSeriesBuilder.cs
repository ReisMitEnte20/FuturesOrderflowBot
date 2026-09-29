using System.Globalization;
using TradingBot.Backtesting.Ohlc;

namespace TradingBot.Quant.Series;

/// <summary>Ergebnis der Renditereihen-Bildung inklusive der Hinweise, die der Bericht ausweisen muss.</summary>
public sealed record ReturnSeriesBuildResult(ReturnSeries Series, IReadOnlyList<string> Notes, bool Truncated)
{
    /// <summary>
    /// Die erste Rendite wurde gegen das Startkapital am Startzeitpunkt der Kurve gemessen (nicht gegen
    /// einen vorangehenden Periodenpunkt). Bei aggregierten Reihen kann diese erste Periode eine
    /// TEILPERIODE sein — sie ist ausdrücklich als solche gekennzeichnet und wird nicht als voller
    /// Kalendertag/-woche/-monat ausgegeben.
    /// </summary>
    public bool FirstPeriodFromStartCapital { get; init; }
}

/// <summary>
/// Baut aus einem <see cref="OhlcBacktestResult"/> eine zeitlich ausgerichtete Kapitalkurve und daraus
/// einheitliche Netto-Renditereihen.
///
/// Grundsätze:
/// - REALISIERT und GESAMT werden getrennt geführt (<see cref="EquityBasis"/>), nie vermischt.
/// - Die Gesamtkurve bewertet eine offene Position zum Bar-Close und zieht die geschätzten
///   Glattstellungskosten ab (Round-Turn-Gebühren + Exit-Slippage), damit sie mit der realisierten
///   Kurve gebührenkonsistent ist. Die Engine bucht Gebühren erst beim Exit.
/// - Es werden KEINE Perioden erfunden: Aggregation auf Tag/Woche/Monat nimmt ausschließlich
///   tatsächlich beobachtete Perioden (letzter Bar der Periode).
/// </summary>
public static class ReturnSeriesBuilder
{
    /// <summary>
    /// Zeitlich ausgerichtete Kapitalkurve eines OHLC-Laufs.
    /// </summary>
    /// <param name="result">Backtest-Ergebnis (Equity-Punkte je Bar, Trades mit Bar-Indizes).</param>
    /// <param name="spec">Kontraktspezifikation für die Mark-to-Market-Bewertung.</param>
    /// <param name="frequency">Zielfrequenz. <see cref="ReturnFrequency.Bar"/> = keine Aggregation.</param>
    /// <param name="name">Name der Kurve (erscheint im Bericht).</param>
    public static QuantEquityCurve BuildEquityCurve(
        OhlcBacktestResult result,
        QuantContractSpec spec,
        ReturnFrequency frequency = ReturnFrequency.Daily,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(spec);

        if (result.Equity.Count == 0)
            return QuantEquityCurve.Empty with { Name = name ?? result.StrategyName, InitialCapital = result.InitialBalance };

        decimal slipPrice = result.EffectiveSlippageTicks * spec.TickSize;
        decimal roundTurnFee = result.Config.ApplyFees ? 2m * result.FeePerSide : 0m;

        // Offene Position je Bar-Index: Engine hält höchstens eine Position gleichzeitig.
        // Ein Trade ist von seinem Entry-Bar (inklusive) bis zum Exit-Bar (exklusiv) offen;
        // am Exit-Bar ist er bereits realisiert und in RealizedNetPnL enthalten.
        var openQty = new Dictionary<int, int>();
        foreach (var t in result.Trades)
            for (int i = t.EntryBarIndex; i < t.ExitBarIndex; i++)
                openQty[i] = t.Quantity;

        var barPoints = new List<QuantEquityPoint>(result.Equity.Count);
        foreach (var e in result.Equity)
        {
            int qty = openQty.TryGetValue(e.BarIndex, out var q) ? q : 0;
            decimal exitCost = qty == 0 ? 0m : (slipPrice * spec.PointValue + roundTurnFee) * qty;
            barPoints.Add(new QuantEquityPoint
            {
                Time = e.Time,
                BarIndex = e.BarIndex,
                RealizedEquity = e.Equity,
                TotalEquity = e.Equity + e.OpenPnL - exitCost,
                OpenQuantity = qty
            });
        }

        var points = frequency == ReturnFrequency.Bar ? barPoints : Resample(barPoints, frequency);

        return new QuantEquityCurve
        {
            Name = name ?? (string.IsNullOrWhiteSpace(result.StrategyName) ? "Lauf" : result.StrategyName),
            Points = points,
            InitialCapital = result.InitialBalance,
            Frequency = frequency,
            Currency = spec.Currency,
            // Startanker = Ende des ersten unaggregierten Bars. An diesem Punkt hält die Engine noch
            // keine Position (Fill erst am nächsten Open), das Kapital entspricht dem Startkapital.
            // Wir merken den ZEITPUNKT, nicht den Kapitalwert — so bleibt die erste (ggf. durch
            // Aggregation eingeschmolzene) Periode messbar, auch wenn sie beim Startkapital endet.
            StartTime = barPoints[0].Time
        };
    }

    /// <summary>Letzter Punkt je beobachteter Kalenderperiode (UTC). Nicht beobachtete Perioden entfallen.</summary>
    private static List<QuantEquityPoint> Resample(IReadOnlyList<QuantEquityPoint> points, ReturnFrequency frequency)
    {
        var result = new List<QuantEquityPoint>();
        string? currentKey = null;
        foreach (var p in points)
        {
            string key = PeriodKey(p.Time, frequency);
            if (currentKey is not null && key == currentKey) result[^1] = p;
            else { result.Add(p); currentKey = key; }
        }
        return result;
    }

    internal static string PeriodKey(DateTimeOffset t, ReturnFrequency frequency)
    {
        var utc = t.ToUniversalTime();
        return frequency switch
        {
            ReturnFrequency.Daily => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ReturnFrequency.Weekly => $"{ISOWeek.GetYear(utc.UtcDateTime):D4}-W{ISOWeek.GetWeekOfYear(utc.UtcDateTime):D2}",
            ReturnFrequency.Monthly => utc.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            _ => utc.ToString("O", CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// Einfache Periodenrenditen der gewählten Kapitalbasis. Die Reihe wird beim ersten nicht
    /// positiven Kapitalstand ABGEBROCHEN statt fortgeschrieben — eine Rendite auf ein Kapital ≤ 0
    /// ist nicht definiert und wird nicht ersatzweise erfunden.
    /// </summary>
    public static ReturnSeriesBuildResult ToReturnSeries(QuantEquityCurve curve, EquityBasis basis)
    {
        ArgumentNullException.ThrowIfNull(curve);
        var notes = new List<string>();

        ReturnSeriesBuildResult EmptyResult(string note, bool truncated) => new(
            ReturnSeries.Empty with
            {
                Name = curve.Name, Basis = basis, Frequency = curve.Frequency,
                Currency = curve.Currency, InitialCapital = (double)curve.InitialCapital
            },
            AppendNote(notes, note), truncated);

        if (curve.Points.Count == 0)
            return EmptyResult("Keine Kurvenpunkte — keine Renditen.", truncated: false);

        double anchorCapital = (double)curve.InitialCapital;

        // Startanker über den EXPLIZITEN Startzeitpunkt, nicht über Kapitalgleichheit. Liegt der erste
        // Kurvenpunkt zeitlich beim (oder vor dem) Startzeitpunkt, IST er der unaggregierte Startbar und
        // dient als Basis (erste Rendite ab dem zweiten Punkt). Liegt er später — die erste Periode wurde
        // durch Aggregation eingeschmolzen —, wird die erste Rendite gegen das Startkapital gemessen.
        // Wichtig: Endet diese Periode zufällig wieder beim Startkapital (z. B. nach einem Round-Turn),
        // verschwindet sie NICHT; sie erscheint als reguläre Periode mit Rendite 0.
        bool firstPointIsAnchor = curve.StartTime is null || curve.Points[0].Time <= curve.StartTime.Value;

        double prev;
        int startIndex;
        bool anchoredToStart;
        if (!firstPointIsAnchor && anchorCapital > 0)
        {
            prev = anchorCapital;
            startIndex = 0;
            anchoredToStart = true;
        }
        else
        {
            if (curve.Points.Count < 2)
                return EmptyResult($"Zu wenige Perioden für Renditen ({curve.Points.Count}). Mindestens 2 nötig.", truncated: false);
            prev = (double)Level(curve.Points[0], basis);
            startIndex = 1;
            anchoredToStart = false;
        }

        if (prev <= 0)
            return EmptyResult("Kapital ist bereits zu Beginn ≤ 0 — Renditen nicht definiert.", truncated: true);

        var times = new List<DateTimeOffset>();
        var rets = new List<double>();
        var levels = new List<double>();
        bool truncated = false;

        for (int i = startIndex; i < curve.Points.Count; i++)
        {
            double cur = (double)Level(curve.Points[i], basis);
            if (prev <= 0)
            {
                truncated = true;
                notes.Add($"Reihe bei {curve.Points[i - 1].Time:u} abgebrochen: Kapital ≤ 0 (Rendite nicht definiert).");
                break;
            }
            times.Add(curve.Points[i].Time);
            rets.Add(cur / prev - 1.0);
            levels.Add(cur);
            prev = cur;
        }

        if (anchoredToStart && rets.Count > 0)
            notes.Add($"Erste Rendite gegen das Startkapital am Startzeitpunkt ({curve.StartTime:u}) gemessen. " +
                      $"Sie kann eine Teilperiode sein und ist kein vollständiger {curve.Frequency}-Zeitraum.");

        return new ReturnSeriesBuildResult(
            new ReturnSeries
            {
                Name = curve.Name,
                Timestamps = times,
                Returns = rets,
                EquityLevels = levels,
                InitialCapital = (double)curve.InitialCapital,
                Basis = basis,
                Frequency = curve.Frequency,
                Currency = curve.Currency
            },
            notes, truncated)
        {
            FirstPeriodFromStartCapital = anchoredToStart && rets.Count > 0
        };
    }

    private static List<string> AppendNote(List<string> notes, string note)
    {
        notes.Add(note);
        return notes;
    }

    private static decimal Level(QuantEquityPoint p, EquityBasis basis)
        => basis == EquityBasis.Total ? p.TotalEquity : p.RealizedEquity;

    /// <summary>
    /// Richtet mehrere Renditereihen auf die GEMEINSAMEN Zeitstempel aus (innere Verknüpfung).
    /// Nötig für Benchmarkvergleiche und für gemeinsames Resampling, damit Abhängigkeiten zwischen
    /// den Reihen erhalten bleiben. Reihen ohne gemeinsamen Zeitstempel ergeben leere Reihen.
    /// </summary>
    public static IReadOnlyList<ReturnSeries> AlignOnCommonTimestamps(IReadOnlyList<ReturnSeries> series)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (series.Count == 0) return Array.Empty<ReturnSeries>();

        HashSet<DateTimeOffset>? common = null;
        foreach (var s in series)
        {
            var set = new HashSet<DateTimeOffset>(s.Timestamps.Select(t => t.ToUniversalTime()));
            common = common is null ? set : new HashSet<DateTimeOffset>(common.Intersect(set));
        }
        var keep = (common ?? new HashSet<DateTimeOffset>()).OrderBy(t => t).ToList();

        var aligned = new List<ReturnSeries>(series.Count);
        foreach (var s in series)
        {
            var idx = new Dictionary<DateTimeOffset, int>();
            for (int i = 0; i < s.Timestamps.Count; i++) idx[s.Timestamps[i].ToUniversalTime()] = i;

            var t2 = new List<DateTimeOffset>(keep.Count);
            var r2 = new List<double>(keep.Count);
            var e2 = new List<double>(keep.Count);
            foreach (var t in keep)
            {
                int i = idx[t];
                t2.Add(s.Timestamps[i]); r2.Add(s.Returns[i]); e2.Add(s.EquityLevels[i]);
            }
            aligned.Add(s with { Timestamps = t2, Returns = r2, EquityLevels = e2 });
        }
        return aligned;
    }
}
