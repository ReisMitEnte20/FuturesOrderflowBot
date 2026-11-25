using TradingBot.Domain.Models;
using TradingBot.Quant.MonteCarlo;
using TradingBot.Quant.Statistics;

namespace TradingBot.Quant.Report;

// =================================================================================================
// 09 — Stabilität über Zeit und Marktbedingungen
// =================================================================================================

public sealed record GroupRow
{
    public required string Key { get; init; }
    public int Trades { get; init; }
    public decimal Net { get; init; }
    public double? Expectancy { get; init; }
    public double? WinRate { get; init; }
    public int Sessions { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    /// <summary>Dünn besetzt (weniger Trades als die Mindestzahl) — keine Erkenntnis ableiten.</summary>
    public bool Thin { get; init; }
}

public sealed record GroupBreakdown(string Dimension, string Definition, IReadOnlyList<GroupRow> Rows);

public sealed record RollingPoint(int TradeNumber, long T, double Expectancy, double Drawdown);

public static class StabilityAnalysis
{
    public static IReadOnlyList<GroupRow> Group(IReadOnlyList<ReportTrade> trades, Func<ReportTrade, string> key, int minTrades,
        IComparer<string>? order = null)
    {
        return trades.GroupBy(key)
            .Select(g =>
            {
                var l = g.ToList();
                return new GroupRow
                {
                    Key = g.Key,
                    Trades = l.Count,
                    Net = l.Sum(t => t.NetPnL),
                    Expectancy = l.Count == 0 ? null : (double)(l.Sum(t => t.NetPnL) / l.Count),
                    WinRate = l.Count == 0 ? null : l.Count(t => t.NetPnL > 0) / (double)l.Count,
                    Sessions = l.Select(t => t.SessionKey).Distinct().Count(),
                    From = l.Min(t => t.EntryTime),
                    To = l.Max(t => t.ExitTime),
                    Thin = l.Count < minTrades
                };
            })
            .OrderBy(r => r.Key, order ?? StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Rollierender Erwartungswert und Drawdown über die letzten <paramref name="window"/> Trades.</summary>
    public static IReadOnlyList<RollingPoint> Rolling(IReadOnlyList<ReportTrade> trades, int window)
    {
        var ordered = trades.OrderBy(t => t.ExitTime).ThenBy(t => t.Index).ToList();
        var res = new List<RollingPoint>();
        if (window < 2 || ordered.Count < window) return res;
        for (int i = window - 1; i < ordered.Count; i++)
        {
            double sum = 0, eq = 0, peak = 0, dd = 0;
            for (int k = i - window + 1; k <= i; k++)
            {
                double x = (double)ordered[k].NetPnL;
                sum += x; eq += x;
                if (eq > peak) peak = eq;
                dd = Math.Max(dd, peak - eq);
            }
            res.Add(new RollingPoint(i + 1, ordered[i].ExitTime.ToUnixTimeMilliseconds(), sum / window, dd));
        }
        return res;
    }
}

/// <summary>
/// Marktregime zum Einstiegszeitpunkt — ausschließlich aus Kerzen VOR der Einstiegskerze (kein Blick nach vorn).
/// Volatilität: ATR(14) der Vorkerzen gegen den Median der ATR(14)-Werte der vorangehenden 250 Kerzen.
/// Trend: Effizienzquote der letzten 20 Vorkerzen |C[t−1] − C[t−21]| / Σ|ΔC| ≥ 0,30 ⇒ Trend, sonst Seitwärts.
/// </summary>
public static class RegimeClassifier
{
    public const string VolDefinition = "Volatilität: ATR(14) der Kerzen vor dem Einstieg im Vergleich zum Median der ATR(14) über die " +
        "250 Kerzen davor — darüber „hoch“, sonst „niedrig“. Nur Daten, die zum Einstieg bekannt waren.";
    public const string TrendDefinition = "Trend/Seitwärts: Effizienzquote der 20 Kerzen vor dem Einstieg " +
        "|C(t−1) − C(t−21)| / Σ|ΔC| ≥ 0,30 ⇒ Trend, sonst Seitwärts. Nur Daten, die zum Einstieg bekannt waren.";

    public static (string Vol, string Trend) Classify(IReadOnlyList<Candle> candles, int entryBarIndex)
    {
        const int atrN = 14, lookback = 250, erN = 20;
        string vol = "unbestimmt (zu wenig Vorlauf)", trend = "unbestimmt (zu wenig Vorlauf)";
        int last = entryBarIndex - 1;   // letzte VOLLSTÄNDIG bekannte Kerze vor dem Einstieg
        if (last >= erN)
        {
            double move = Math.Abs((double)(candles[last].Close - candles[last - erN].Close));
            double path = 0;
            for (int k = last - erN + 1; k <= last; k++) path += Math.Abs((double)(candles[k].Close - candles[k - 1].Close));
            trend = path <= 0 ? "Seitwärts" : move / path >= 0.30 ? "Trend" : "Seitwärts";
        }
        if (last >= atrN + 60)
        {
            double Atr(int end)
            {
                double s = 0;
                for (int k = end - atrN + 1; k <= end; k++)
                {
                    var c = candles[k]; var p = candles[k - 1];
                    double tr = Math.Max((double)(c.High - c.Low), Math.Max(Math.Abs((double)(c.High - p.Close)), Math.Abs((double)(c.Low - p.Close))));
                    s += tr;
                }
                return s / atrN;
            }
            double now = Atr(last);
            var hist = new List<double>();
            for (int e = Math.Max(atrN, last - lookback); e < last; e++) hist.Add(Atr(e));
            if (hist.Count >= 50) vol = now > Stats.Median(hist) ? "hoch" : "niedrig";
        }
        return (vol, trend);
    }
}

// =================================================================================================
// 10 — Konzentration und Ausreißer
// =================================================================================================

public sealed record LeaveOneOutRow(string SessionKey, decimal SessionNet, decimal NetWithout);

public sealed record ConcentrationResult
{
    public decimal TotalNet { get; init; }
    public ReportValue Top5TradesShare { get; init; } = ReportValue.Missing("-");
    public decimal Top5TradesNet { get; init; }
    public ReportValue BestDayShare { get; init; } = ReportValue.Missing("-");
    public string? BestDay { get; init; }
    public decimal BestDayNet { get; init; }
    public ReportValue BestWeekShare { get; init; } = ReportValue.Missing("-");
    public string? BestWeek { get; init; }
    public decimal BestWeekNet { get; init; }
    public decimal NetWithoutBestDay { get; init; }
    public decimal NetWithoutTop5Trades { get; init; }
    public decimal NetWithoutBestWeek { get; init; }
    public bool LeaveOneOutAvailable { get; init; }
    public string? LeaveOneOutReason { get; init; }
    public IReadOnlyList<LeaveOneOutRow> LeaveOneOut { get; init; } = Array.Empty<LeaveOneOutRow>();
    public decimal? LeaveOneOutMin { get; init; }
    public decimal? LeaveOneOutMax { get; init; }
    /// <summary>Anzahl Handelstage, deren Weglassen das Vorzeichen des Gesamtergebnisses umdreht.</summary>
    public int SignFlippingDays { get; init; }
}

public static class ConcentrationAnalysis
{
    public static ConcentrationResult Compute(IReadOnlyList<ReportTrade> trades, int minSessionsForLoo = 5)
    {
        decimal total = trades.Sum(t => t.NetPnL);
        string shareReason = total <= 0
            ? "Gesamtergebnis ≤ 0 — kein Gewinnanteil definierbar (Ergebnisse ohne beste Trades/Tage siehe unten)."
            : "";
        ReportValue Share(decimal part) => total > 0 ? ReportValue.Of((double)(part / total)) : ReportValue.Missing(shareReason);

        var top5 = trades.OrderByDescending(t => t.NetPnL).Take(5).ToList();
        decimal top5Net = top5.Sum(t => t.NetPnL);
        var days = trades.GroupBy(t => t.SessionKey).Select(g => (Key: g.Key, Net: g.Sum(t => t.NetPnL))).ToList();
        var bestDay = days.Count == 0 ? ((string?)null, 0m) : days.OrderByDescending(d => d.Net).Select(d => ((string?)d.Key, d.Net)).First();
        var weeks = trades.GroupBy(t => DateOnly.TryParse(t.SessionKey, out var d) ? TradeDateCalendar.WeekKey(d) : t.SessionKey)
            .Select(g => (Key: g.Key, Net: g.Sum(t => t.NetPnL))).ToList();
        var bestWeek = weeks.Count == 0 ? ((string?)null, 0m) : weeks.OrderByDescending(d => d.Net).Select(d => ((string?)d.Key, d.Net)).First();

        var loo = new List<LeaveOneOutRow>();
        bool looOk = days.Count >= minSessionsForLoo;
        if (looOk)
            foreach (var d in days.OrderBy(d => d.Key, StringComparer.Ordinal))
                loo.Add(new LeaveOneOutRow(d.Key, d.Net, total - d.Net));
        int flips = loo.Count(r => Math.Sign(r.NetWithout) != Math.Sign(total) && total != 0);

        return new ConcentrationResult
        {
            TotalNet = total,
            Top5TradesNet = top5Net,
            Top5TradesShare = Share(top5Net),
            BestDay = bestDay.Item1,
            BestDayNet = bestDay.Item2,
            BestDayShare = Share(bestDay.Item2),
            BestWeek = bestWeek.Item1,
            BestWeekNet = bestWeek.Item2,
            BestWeekShare = Share(bestWeek.Item2),
            NetWithoutBestDay = total - bestDay.Item2,
            NetWithoutTop5Trades = total - top5Net,
            NetWithoutBestWeek = total - bestWeek.Item2,
            LeaveOneOutAvailable = looOk,
            LeaveOneOutReason = looOk ? null : $"{minSessionsForLoo} Handelstage benötigt, {days.Count} vorhanden.",
            LeaveOneOut = loo,
            LeaveOneOutMin = loo.Count > 0 ? loo.Min(r => r.NetWithout) : null,
            LeaveOneOutMax = loo.Count > 0 ? loo.Max(r => r.NetWithout) : null,
            SignFlippingDays = flips
        };
    }
}

// =================================================================================================
// 08 — Kosten: Gewinnschwelle und vorab festgelegte Szenarien
// =================================================================================================

public sealed record CostScenario(string Id, string Label, decimal ExtraFeePerSide, decimal ExtraSlippageTicksPerMarketSide);

public sealed record CostScenarioRow(string Id, string Label, decimal Net, double? Expectancy, decimal TotalCosts, bool Hypothetical);

public sealed record CostBreakevenResult
{
    public int Trades { get; init; }
    /// <summary>Mittleres Ergebnis je Trade VOR allen Kosten.</summary>
    public double? PreCostExpectancy { get; init; }
    /// <summary>Mittlere tatsächlich modellierte Kosten je Round-Turn (Gebühren + enthaltene Slippage).</summary>
    public double? CostPerTrade { get; init; }
    /// <summary>Gesamtkosten je Round-Turn, bei denen der historische Erwartungswert auf null fällt.</summary>
    public ReportValue BreakevenCostPerTrade { get; init; } = ReportValue.Missing("-");
    /// <summary>Dieselbe Schwelle in Ticks je Seite (bei der gegebenen Kontraktzahl).</summary>
    public ReportValue BreakevenTicksPerSide { get; init; } = ReportValue.Missing("-");
    /// <summary>Zusätzliche Kosten je Round-Turn, die bis zur Gewinnschwelle noch tragbar sind.</summary>
    public ReportValue Headroom { get; init; } = ReportValue.Missing("-");
    public IReadOnlyList<CostScenarioRow> Scenarios { get; init; } = Array.Empty<CostScenarioRow>();
}

public static class CostAnalysis
{
    public static IReadOnlyList<CostScenario> DefaultScenarios => new[]
    {
        new CostScenario("base", "Basisprofil", 0m, 0m),
        new CostScenario("fee+50", "Gebühren +50 %", -1m, 0m),          // Platzhalter: -1 = relativ, wird unten aufgelöst
        new CostScenario("slip+1", "+1 Tick Slippage je Market-Fill", 0m, 1m),
        new CostScenario("slip+2", "+2 Ticks Slippage je Market-Fill", 0m, 2m),
        new CostScenario("combo", "Gebühren +50 % und +2 Ticks", -1m, 2m)
    };

    public static CostBreakevenResult Compute(IReadOnlyList<ReportTrade> trades, decimal tickValue, decimal feePerSide,
        IReadOnlyList<CostScenario>? scenarios = null)
    {
        int n = trades.Count;
        if (n == 0) return new CostBreakevenResult { Trades = 0, BreakevenCostPerTrade = ReportValue.Missing("Keine Trades.") };
        double pre = (double)trades.Average(t => t.PreCostPnL);
        double cost = (double)trades.Average(t => t.Fees + t.SlippageCost);
        double qty = trades.Average(t => (double)Math.Max(1, t.Quantity));
        ReportValue be = pre <= 0
            ? ReportValue.Missing($"Bereits VOR Kosten nicht positiv (Ø {pre:0.00} je Trade) — keine Gewinnschwelle.")
            : ReportValue.Of(pre);
        ReportValue beTicks = pre <= 0 || tickValue <= 0
            ? ReportValue.Missing(pre <= 0 ? "Keine Gewinnschwelle." : "Tick-Wert unbekannt.")
            : ReportValue.Of(pre / (2.0 * (double)tickValue * qty));
        var rows = new List<CostScenarioRow>();
        foreach (var s in scenarios ?? DefaultScenarios)
        {
            decimal extraFee = s.ExtraFeePerSide < 0 ? feePerSide * 0.5m : s.ExtraFeePerSide;
            decimal net = 0, costs = 0;
            foreach (var t in trades)
            {
                decimal add = 2m * extraFee * t.Quantity + s.ExtraSlippageTicksPerMarketSide * tickValue * t.Quantity * t.MarketSides;
                net += t.NetPnL - add;
                costs += t.Fees + t.SlippageCost + add;
            }
            rows.Add(new CostScenarioRow(s.Id, s.Label, net, (double)(net / n), costs, s.Id != "base"));
        }
        return new CostBreakevenResult
        {
            Trades = n,
            PreCostExpectancy = pre,
            CostPerTrade = cost,
            BreakevenCostPerTrade = be,
            BreakevenTicksPerSide = beTicks,
            Headroom = pre <= 0 ? ReportValue.Missing("Keine Gewinnschwelle.") : ReportValue.Of(pre - cost),
            Scenarios = rows
        };
    }
}

// =================================================================================================
// 11 — Zufallskontrolle mit vergleichbaren Handelszeiten, Exposure, Tradezahl und Kosten
// =================================================================================================

public sealed record RandomControlResult
{
    public bool Available { get; init; }
    public string? UnavailableReason { get; init; }
    public int Iterations { get; init; }
    public int Seed { get; init; }
    public int TradesPerRun { get; init; }
    public SimDistribution? Net { get; init; }
    /// <summary>Anteil der Zufallsläufe mit Netto ≥ Strategie-Netto (Einordnung, keine Signifikanzaussage).</summary>
    public double? ShareRandomAtLeastStrategy { get; init; }
    public decimal StrategyNet { get; init; }
    public string Definition { get; init; } = "";
}

public static class RandomEntryControl
{
    /// <summary>
    /// Zufällige Einstiege mit derselben Tradezahl, derselben Uhrzeit-Verteilung (Stunde in New York), demselben
    /// Long-Anteil, denselben Haltedauern (in Kerzen) und denselben Kosten (Slippage in den Fills, Gebühren je Seite).
    /// Einstieg am OPEN der gezogenen Kerze, Ausstieg am OPEN der Kerze nach der Haltedauer — kein Blick nach vorn.
    /// </summary>
    public static RandomControlResult Run(IReadOnlyList<Candle> candles, int firstBar, int lastBar, IReadOnlyList<ReportTrade> trades,
        decimal slipPrice, decimal feePerSide, decimal pointValue, bool applyFees, int iterations, int seed, CancellationToken ct = default)
    {
        string def = "Je Lauf so viele Zufallstrades wie die Strategie: Einstiegskerze zufällig aus derselben New-York-Stunde wie der " +
                     "jeweilige Originaltrade, Richtung zufällig mit demselben Long-Anteil, gleiche Haltedauer in Kerzen, Fill am Open " +
                     "± Slippage, gleiche Gebühren. Überlappungen sind möglich (Exposure je Trade identisch).";
        if (trades.Count < 5)
            return new RandomControlResult { Available = false, UnavailableReason = $"Mindestens 5 Trades nötig, {trades.Count} vorhanden.", Definition = def };
        var byHour = new Dictionary<int, List<int>>();
        for (int i = firstBar; i <= lastBar; i++)
        {
            int h = TradeDateCalendar.ToNewYork(candles[i].OpenTime).Hour;
            (byHour.TryGetValue(h, out var l) ? l : byHour[h] = new List<int>()).Add(i);
        }
        double longShare = trades.Count(t => t.IsLong) / (double)trades.Count;
        var rng = new DeterministicRng(seed);
        var nets = new List<double>(iterations);
        decimal stratNet = trades.Sum(t => t.NetPnL);
        int atLeast = 0;
        for (int it = 0; it < iterations; it++)
        {
            if ((it & 63) == 0) ct.ThrowIfCancellationRequested();
            decimal net = 0;
            foreach (var t in trades)
            {
                int h = TradeDateCalendar.ToNewYork(t.EntryTime).Hour;
                if (!byHour.TryGetValue(h, out var pool) || pool.Count == 0) pool = byHour.Values.First(p => p.Count > 0);
                int e = pool[rng.NextInt(pool.Count)];
                int hold = Math.Max(1, t.ExitBarIndex - t.EntryBarIndex);
                int x = Math.Min(lastBar, e + hold);
                bool isLong = rng.NextDouble() < longShare;
                decimal entry = isLong ? candles[e].Open + slipPrice : candles[e].Open - slipPrice;
                decimal exitRef = x == e ? candles[e].Close : candles[x].Open;
                decimal exit = isLong ? exitRef - slipPrice : exitRef + slipPrice;
                decimal gross = (exit - entry) * (isLong ? 1 : -1) * pointValue * t.Quantity;
                net += gross - (applyFees ? 2m * feePerSide * t.Quantity : 0m);
            }
            nets.Add((double)net);
            if (net >= stratNet) atLeast++;
        }
        return new RandomControlResult
        {
            Available = true, Iterations = iterations, Seed = seed, TradesPerRun = trades.Count,
            Net = SimDistribution.From("random_net", "Netto der Zufallskontrolle", "currency", nets),
            ShareRandomAtLeastStrategy = atLeast / (double)iterations,
            StrategyNet = stratNet, Definition = def
        };
    }
}

// =================================================================================================
// 12 — MAE/MFE aus dem tatsächlichen Kursverlauf
// =================================================================================================

public sealed record ExcursionRow(int TradeIndex, double? Mae, double? Mfe, int CoveredBars, bool LowerBound, string Basis);

public static class ExcursionAnalysis
{
    /// <summary>
    /// MAE/MFE nur aus Kerzen, die VOLLSTÄNDIG innerhalb der Haltedauer lagen (Einstieg am Open der Einstiegskerze ⇒
    /// diese Kerze zählt; eine Ausstiegskerze zählt nur, wenn zum Schlusskurs ausgestiegen wurde). Teilweise gehaltene
    /// Kerzen werden ausgelassen (ihr Hoch/Tief kann außerhalb der Haltezeit liegen); dann ist der Wert eine Untergrenze.
    /// Zusätzlich fließen die tatsächlichen Fill-Preise ein. Kein Hoch/Tief einer nur teilweise gehaltenen Kerze.
    /// </summary>
    public static ExcursionRow Compute(IReadOnlyList<Candle> candles, ReportTrade t, decimal pointValue)
    {
        int lastCovered;
        bool exitAtClose = t.ExitBarIndex >= 0 && t.ExitBarIndex < candles.Count && t.ExitTime == candles[t.ExitBarIndex].CloseTime;
        lastCovered = exitAtClose ? t.ExitBarIndex : t.ExitBarIndex - 1;
        bool partialExit = !exitAtClose && t.ExitBarIndex < candles.Count && t.ExitTime > candles[Math.Max(0, t.ExitBarIndex)].OpenTime;
        double sign = t.IsLong ? 1 : -1;
        double pv = (double)pointValue * t.Quantity;
        double worst = Math.Min(0, ((double)(t.ExitPrice - t.EntryPrice)) * sign);
        double best = Math.Max(0, ((double)(t.ExitPrice - t.EntryPrice)) * sign);
        int covered = 0;
        for (int i = Math.Max(0, t.EntryBarIndex); i <= lastCovered && i < candles.Count; i++)
        {
            covered++;
            var c = candles[i];
            double up = (double)(c.High - t.EntryPrice) * sign, dn = (double)(c.Low - t.EntryPrice) * sign;
            worst = Math.Min(worst, Math.Min(up, dn));
            best = Math.Max(best, Math.Max(up, dn));
        }
        string basis = covered == 0
            ? "Nur Fill-Preise (Ein- und Ausstieg in derselben Kerze — Verlauf dazwischen aus OHLC nicht belegbar)"
            : partialExit ? $"{covered} vollständig gehaltene Kerze(n) + Fills; Ausstiegskerze nur teilweise gehalten → Untergrenze"
            : $"{covered} vollständig gehaltene Kerze(n) + Fills";
        return new ExcursionRow(t.Index, -worst * pv, best * pv, covered, covered == 0 || partialExit, basis);
    }
}

// =================================================================================================
// 05 — Einordnung der Parametersensitivität (beschreibende Regel, kein Gesamtscore)
// =================================================================================================

public static class SensitivityRules
{
    public const string Definition = "Regel: „zu wenige Trades“, wenn der Basiswert weniger Trades als die Mindestzahl hat; " +
        "„stabil positiv“, wenn Basiswert UND beide direkten Nachbarn netto > 0 sind; „einzelne Spitze“, wenn der Basiswert " +
        "positiv, aber kein direkter Nachbar positiv ist; „stabiler Verlust“, wenn alle geprüften Werte ≤ 0 sind; sonst „gemischt“.";

    public static string Classify(IReadOnlyList<(double Value, decimal Net, int Trades, bool Valid)> points, int baseIndex, int minTrades)
    {
        if (baseIndex < 0 || baseIndex >= points.Count) return "nicht bestimmbar";
        var b = points[baseIndex];
        if (!b.Valid) return "nicht bestimmbar";
        if (b.Trades < minTrades) return "zu wenige Trades";
        var valid = points.Where(p => p.Valid).ToList();
        if (valid.All(p => p.Net <= 0)) return "stabiler Verlust";
        var neigh = new List<(double Value, decimal Net, int Trades, bool Valid)>();
        if (baseIndex > 0 && points[baseIndex - 1].Valid) neigh.Add(points[baseIndex - 1]);
        if (baseIndex < points.Count - 1 && points[baseIndex + 1].Valid) neigh.Add(points[baseIndex + 1]);
        if (b.Net > 0 && neigh.Count > 0 && neigh.All(p => p.Net > 0)) return "stabil positiv";
        if (b.Net > 0 && neigh.All(p => p.Net <= 0)) return "einzelne Spitze";
        return "gemischt";
    }

    /// <summary>Nachbarwerte um einen Basiswert: ±20 % und ±10 % (bei Ganzzahlen gerundet, eindeutig, &gt; 0).</summary>
    public static IReadOnlyList<double> Neighbors(double baseValue, bool integer)
    {
        var f = new[] { 0.8, 0.9, 1.0, 1.1, 1.2 };
        var vals = f.Select(x => baseValue * x).Select(v => integer ? Math.Round(v, MidpointRounding.AwayFromZero) : Math.Round(v, 6))
            .Where(v => v > 0).Distinct().OrderBy(v => v).ToList();
        if (integer && vals.Count < 5)
        {
            // Bei kleinen Ganzzahlen (z. B. 3) auf ±1/±2 ausweichen, damit Nachbarn existieren.
            vals = new[] { baseValue - 2, baseValue - 1, baseValue, baseValue + 1, baseValue + 2 }.Where(v => v > 0).Distinct().ToList();
        }
        return vals;
    }
}
