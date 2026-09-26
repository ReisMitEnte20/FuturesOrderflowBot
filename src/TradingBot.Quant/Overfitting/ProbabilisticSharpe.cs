using TradingBot.Quant.Statistics;

namespace TradingBot.Quant.Overfitting;

/// <summary>Ergebnis einer PSR-Berechnung inklusive aller Eingangsgrößen.</summary>
public sealed record PsrResult
{
    /// <summary>Wahrscheinlichkeit, dass der wahre Sharpe über der Schwelle liegt. Null = nicht berechenbar.</summary>
    public double? Psr { get; init; }

    /// <summary>Beobachteter Sharpe JE PERIODE (nicht annualisiert) — so verlangt es die Originalformel.</summary>
    public double? ObservedSharpePerPeriod { get; init; }
    public double BenchmarkSharpePerPeriod { get; init; }

    public int Observations { get; init; }
    public double? Skewness { get; init; }
    public double? Kurtosis { get; init; }

    /// <summary>Mindestlänge der Historie, ab der PSR das geforderte Konfidenzniveau erreichen würde.</summary>
    public double? MinimumTrackRecordLength { get; init; }
    public double Confidence { get; init; } = 0.95;

    public required IReadOnlyList<string> Definitions { get; init; }
    public string? UnavailableReason { get; init; }
    public bool Available => Psr.HasValue;
}

/// <summary>Ergebnis der Deflated Sharpe Ratio.</summary>
public sealed record DsrResult
{
    public double? Dsr { get; init; }
    /// <summary>Erwarteter maximaler Sharpe unter der Nullhypothese bei der angesetzten Versuchszahl.</summary>
    public double? ExpectedMaxSharpeUnderNull { get; init; }

    /// <summary>Tatsächlich durchgeführte Versuche (aus dem Register).</summary>
    public int ActualTrials { get; init; }
    /// <summary>Angesetzte EFFEKTIVE Versuchszahl (bei korrelierten Varianten kleiner als die tatsächliche).</summary>
    public double EffectiveTrials { get; init; }
    public required string EffectiveTrialsRationale { get; init; }

    public double? VarianceOfTrialSharpes { get; init; }
    public PsrResult? Psr { get; init; }

    public required IReadOnlyList<string> Definitions { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string? UnavailableReason { get; init; }
    public bool Available => Dsr.HasValue;
}

/// <summary>
/// Probabilistic Sharpe Ratio (PSR) und Deflated Sharpe Ratio (DSR) nach Bailey und López de Prado.
///
/// PSR(SR*) = Φ[ (SR̂ − SR*)·√(n−1) / √(1 − γ₃·SR̂ + ((γ₄−1)/4)·SR̂²) ]
/// mit SR̂ = Sharpe JE PERIODE, γ₃ = Schiefe, γ₄ = Wölbung (nicht-exzessiv, Normalverteilung = 3),
/// n = Zahl der Beobachtungen.
///
/// DSR = PSR(SR*₀) mit dem erwarteten Maximum unter der Nullhypothese
/// SR*₀ = √Var(SR_n) · [ (1−γ)·Φ⁻¹(1 − 1/N) + γ·Φ⁻¹(1 − 1/(N·e)) ], γ = Euler-Mascheroni-Konstante.
///
/// Wichtig: SR̂ ist NICHT annualisiert. Wird versehentlich ein annualisierter Sharpe eingesetzt,
/// sind PSR und DSR systematisch falsch.
/// </summary>
public static class ProbabilisticSharpe
{
    public const double EulerMascheroni = 0.5772156649015328606;

    /// <summary>PSR aus einer Renditereihe.</summary>
    /// <param name="returns">Periodenrenditen (nicht annualisiert).</param>
    /// <param name="benchmarkSharpePerPeriod">Schwelle SR* je Periode (Standard 0).</param>
    /// <param name="confidence">Konfidenzniveau für die Mindest-Historienlänge.</param>
    public static PsrResult Compute(IReadOnlyList<double> returns, double benchmarkSharpePerPeriod = 0.0,
        double confidence = 0.95)
    {
        ArgumentNullException.ThrowIfNull(returns);
        var definitions = new List<string>
        {
            "PSR(SR*) = Φ[(SR̂ − SR*)·√(n−1) / √(1 − γ₃·SR̂ + ((γ₄−1)/4)·SR̂²)] nach Bailey/López de Prado.",
            "SR̂ ist der Sharpe JE PERIODE (nicht annualisiert), γ₃ = Schiefe, γ₄ = Wölbung (nicht-exzessiv).",
            "Voraussetzung: identisch verteilte, seriell unabhängige Renditen. Autokorrelation verletzt sie und lässt PSR zu hoch erscheinen."
        };

        int n = returns.Count;
        if (n < 4)
            return new PsrResult
            {
                Definitions = definitions, Observations = n, BenchmarkSharpePerPeriod = benchmarkSharpePerPeriod,
                Confidence = confidence,
                UnavailableReason = $"Nur {n} Beobachtungen — Schiefe und Wölbung brauchen mindestens 4."
            };

        var sd = Stats.StdDevSample(returns);
        if (sd is null || Stats.IsNegligibleDeviation(sd.Value, Stats.MaxAbs(returns)))
            return new PsrResult
            {
                Definitions = definitions, Observations = n, BenchmarkSharpePerPeriod = benchmarkSharpePerPeriod,
                Confidence = confidence,
                UnavailableReason = "Standardabweichung = 0 (konstante Renditen) — Sharpe und damit PSR nicht definiert."
            };

        double sr = Stats.Mean(returns) / sd.Value;
        double? skew = Stats.Skewness(returns);
        double? kurt = Stats.Kurtosis(returns);
        if (skew is null || kurt is null)
            return new PsrResult
            {
                Definitions = definitions, Observations = n, ObservedSharpePerPeriod = sr,
                BenchmarkSharpePerPeriod = benchmarkSharpePerPeriod, Confidence = confidence,
                UnavailableReason = "Schiefe/Wölbung nicht bestimmbar."
            };

        double variance = 1.0 - skew.Value * sr + (kurt.Value - 1.0) / 4.0 * sr * sr;
        if (variance <= 0)
            return new PsrResult
            {
                Definitions = definitions, Observations = n, ObservedSharpePerPeriod = sr,
                Skewness = skew, Kurtosis = kurt, BenchmarkSharpePerPeriod = benchmarkSharpePerPeriod,
                Confidence = confidence,
                UnavailableReason = "Der Varianzterm der Sharpe-Schätzung ist ≤ 0 — die Näherung ist für diese " +
                                    "Renditeverteilung nicht anwendbar (extreme Schiefe/Wölbung)."
            };

        double z = (sr - benchmarkSharpePerPeriod) * Math.Sqrt(n - 1) / Math.Sqrt(variance);
        double psr = Stats.NormalCdf(z);

        double? minTrl = null;
        if (sr > benchmarkSharpePerPeriod && confidence is > 0 and < 1)
        {
            double zAlpha = Stats.NormalInverseCdf(confidence);
            minTrl = 1.0 + variance * Math.Pow(zAlpha / (sr - benchmarkSharpePerPeriod), 2.0);
        }

        return new PsrResult
        {
            Psr = psr,
            ObservedSharpePerPeriod = sr,
            BenchmarkSharpePerPeriod = benchmarkSharpePerPeriod,
            Observations = n,
            Skewness = skew,
            Kurtosis = kurt,
            MinimumTrackRecordLength = minTrl,
            Confidence = confidence,
            Definitions = definitions
        };
    }

    /// <summary>
    /// Deflated Sharpe Ratio.
    /// </summary>
    /// <param name="selectedReturns">Renditereihe des AUSGEWÄHLTEN Kandidaten (je Periode).</param>
    /// <param name="trialSharpesPerPeriod">Sharpe je Periode ALLER Versuche der Kampagne — auch der negativen und verworfenen.</param>
    /// <param name="effectiveTrials">Angesetzte effektive Versuchszahl; null = tatsächliche Zahl (Annahme: unabhängig).</param>
    /// <param name="effectiveTrialsRationale">Begründung für die angesetzte effektive Versuchszahl.</param>
    public static DsrResult ComputeDeflated(
        IReadOnlyList<double> selectedReturns,
        IReadOnlyList<double> trialSharpesPerPeriod,
        double? effectiveTrials = null,
        string? effectiveTrialsRationale = null)
    {
        ArgumentNullException.ThrowIfNull(selectedReturns);
        ArgumentNullException.ThrowIfNull(trialSharpesPerPeriod);

        var definitions = new List<string>
        {
            "DSR = PSR(SR*₀) mit SR*₀ = √Var(SR_n)·[(1−γ)·Φ⁻¹(1 − 1/N) + γ·Φ⁻¹(1 − 1/(N·e))], γ = Euler-Mascheroni.",
            "Var(SR_n) ist die Varianz der Sharpe-Werte ALLER Versuche der Kampagne (je Periode).",
            "N ist die EFFEKTIVE Versuchszahl. Korrelierte Varianten sind keine unabhängigen Versuche."
        };
        var warnings = new List<string>();

        int actual = trialSharpesPerPeriod.Count;
        double eff = effectiveTrials ?? actual;
        string rationale = effectiveTrialsRationale ??
            $"Tatsächliche Versuchszahl {actual} unverändert als effektive Zahl angesetzt " +
            "(Annahme: unabhängige Versuche). Bei korrelierten Parametervarianten ist das zu konservativ zugunsten der Strategie.";

        if (actual < 2)
            return new DsrResult
            {
                ActualTrials = actual, EffectiveTrials = eff, EffectiveTrialsRationale = rationale,
                Definitions = definitions, Warnings = warnings,
                UnavailableReason = $"Nur {actual} Versuch(e) erfasst — ohne Versuchsverteilung ist DSR nicht berechenbar."
            };
        if (eff < 2)
            return new DsrResult
            {
                ActualTrials = actual, EffectiveTrials = eff, EffectiveTrialsRationale = rationale,
                Definitions = definitions, Warnings = warnings,
                UnavailableReason = $"Effektive Versuchszahl {eff:0.##} < 2 — SR*₀ nicht definiert."
            };

        var varSr = Stats.StdDevSample(trialSharpesPerPeriod);
        if (varSr is null || Stats.IsNegligibleDeviation(varSr.Value, Stats.MaxAbs(trialSharpesPerPeriod)))
            return new DsrResult
            {
                ActualTrials = actual, EffectiveTrials = eff, EffectiveTrialsRationale = rationale,
                Definitions = definitions, Warnings = warnings,
                UnavailableReason = "Alle Versuche haben denselben Sharpe — Var(SR_n) = 0, SR*₀ nicht bestimmbar."
            };

        double n = eff;
        double srStar = varSr.Value * (
            (1.0 - EulerMascheroni) * Stats.NormalInverseCdf(1.0 - 1.0 / n) +
            EulerMascheroni * Stats.NormalInverseCdf(1.0 - 1.0 / (n * Math.E)));

        var psr = Compute(selectedReturns, srStar);

        if (effectiveTrials is null)
            warnings.Add("Effektive Versuchszahl = tatsächliche Versuchszahl. Wurden ähnliche Parametervarianten " +
                         "geprüft, sind diese korreliert und die effektive Zahl ist kleiner — DSR fällt dann günstiger aus, als er sollte.");
        if (actual < 10)
            warnings.Add($"Nur {actual} erfasste Versuche. Fehlen verworfene oder gescheiterte Versuche im Register, " +
                         "wird die Mehrfachtest-Korrektur zu schwach.");

        return new DsrResult
        {
            Dsr = psr.Psr,
            ExpectedMaxSharpeUnderNull = srStar,
            ActualTrials = actual,
            EffectiveTrials = eff,
            EffectiveTrialsRationale = rationale,
            VarianceOfTrialSharpes = varSr.Value * varSr.Value,
            Psr = psr,
            Definitions = definitions,
            Warnings = warnings,
            UnavailableReason = psr.Available ? null : psr.UnavailableReason
        };
    }

    /// <summary>
    /// Heuristische Schätzung der effektiven Versuchszahl aus der mittleren paarweisen Korrelation
    /// der Kandidaten-Renditereihen: N_eff = N / (1 + (N−1)·ρ̄).
    ///
    /// Diese Formel stammt NICHT aus den PSR/DSR-Originalquellen. Sie ist eine ausdrücklich
    /// gekennzeichnete Annahme, um korrelierte Varianten nicht als unabhängige Versuche zu zählen.
    /// </summary>
    public static (double EffectiveTrials, string Rationale) EstimateEffectiveTrials(
        IReadOnlyList<IReadOnlyList<double>> candidateReturns)
    {
        ArgumentNullException.ThrowIfNull(candidateReturns);
        int n = candidateReturns.Count;
        if (n <= 1)
            return (Math.Max(1, n), $"Nur {n} Kandidat(en) — effektive Versuchszahl = {Math.Max(1, n)}.");

        double sum = 0;
        int pairs = 0;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                var c = Stats.Correlation(candidateReturns[i], candidateReturns[j]);
                if (c is { } v) { sum += v; pairs++; }
            }

        if (pairs == 0)
            return (n, $"Korrelationen nicht bestimmbar — effektive Versuchszahl = tatsächliche Zahl {n} (Annahme: unabhängig).");

        double rho = Math.Clamp(sum / pairs, 0.0, 0.999);
        double eff = Math.Clamp(n / (1.0 + (n - 1) * rho), 1.0, n);
        return (eff,
            $"Mittlere paarweise Korrelation der {n} Kandidaten: ρ̄ = {rho:0.###}. " +
            $"Effektive Versuchszahl N/(1+(N−1)·ρ̄) = {eff:0.##}. " +
            "Heuristik, nicht Teil der PSR/DSR-Originalquellen — bewusst als Annahme ausgewiesen.");
    }
}
