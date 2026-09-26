namespace TradingBot.Quant.Statistics;

/// <summary>
/// Elementare Statistikfunktionen mit ausdrücklich festgelegten Konventionen. Die Konventionen sind
/// Teil der Kennzahlendefinition und dürfen nicht stillschweigend geändert werden.
/// </summary>
public static class Stats
{
    public static double Mean(IReadOnlyList<double> x)
    {
        if (x.Count == 0) return double.NaN;
        double s = 0;
        foreach (var v in x) s += v;
        return s / x.Count;
    }

    /// <summary>Stichproben-Standardabweichung (Nenner n−1). Null bei n &lt; 2.</summary>
    public static double? StdDevSample(IReadOnlyList<double> x)
    {
        if (x.Count < 2) return null;
        double m = Mean(x), ss = 0;
        foreach (var v in x) { double d = v - m; ss += d * d; }
        return Math.Sqrt(ss / (x.Count - 1));
    }

    /// <summary>Populations-Standardabweichung (Nenner n). Für Momentenschätzer in PSR/DSR.</summary>
    public static double? StdDevPopulation(IReadOnlyList<double> x)
    {
        if (x.Count < 1) return null;
        double m = Mean(x), ss = 0;
        foreach (var v in x) { double d = v - m; ss += d * d; }
        return Math.Sqrt(ss / x.Count);
    }

    /// <summary>Schiefe als Momentenschätzer g1 = m3 / m2^(3/2) (biased, wie in den PSR/DSR-Quellen).</summary>
    public static double? Skewness(IReadOnlyList<double> x)
    {
        if (x.Count < 3) return null;
        double m = Mean(x), m2 = 0, m3 = 0;
        foreach (var v in x) { double d = v - m; m2 += d * d; m3 += d * d * d; }
        m2 /= x.Count; m3 /= x.Count;
        if (m2 <= 0) return null;
        return m3 / Math.Pow(m2, 1.5);
    }

    /// <summary>Wölbung als Momentenschätzer g2 = m4 / m2² (NICHT-exzessiv; Normalverteilung = 3).</summary>
    public static double? Kurtosis(IReadOnlyList<double> x)
    {
        if (x.Count < 4) return null;
        double m = Mean(x), m2 = 0, m4 = 0;
        foreach (var v in x) { double d = v - m; m2 += d * d; m4 += d * d * d * d; }
        m2 /= x.Count; m4 /= x.Count;
        if (m2 <= 0) return null;
        return m4 / (m2 * m2);
    }

    /// <summary>Perzentil mit linearer Interpolation zwischen Rängen (entspricht numpy 'linear'). p in [0,1].</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return double.NaN;
        if (p < 0 || p > 1) throw new ArgumentOutOfRangeException(nameof(p));
        if (values.Count == 1) return values[0];
        var s = values.OrderBy(v => v).ToArray();
        double rank = p * (s.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return lo == hi ? s[lo] : s[lo] + (s[hi] - s[lo]) * (rank - lo);
    }

    public static double Median(IReadOnlyList<double> values) => Percentile(values, 0.5);

    /// <summary>
    /// Ist eine Streuung gegenüber dem Skalenniveau der Daten vernachlässigbar?
    ///
    /// Eine rechnerisch konstante Reihe hat wegen Gleitkomma-Rundung selten exakt die
    /// Standardabweichung 0; ein Vergleich gegen 0 würde dann ein absurd großes Verhältnis
    /// (Sharpe, Information Ratio) erzeugen statt „nicht definiert" zu melden. Deshalb wird die
    /// Streuung RELATIV zum Skalenniveau geprüft.
    /// </summary>
    /// <param name="stdDev">Gemessene Streuung.</param>
    /// <param name="scale">Bezugsgröße, z. B. der größte Absolutwert der Reihe.</param>
    /// <param name="relativeTolerance">Anteil der Bezugsgröße, unterhalb dessen die Streuung als 0 gilt.</param>
    public static bool IsNegligibleDeviation(double stdDev, double scale, double relativeTolerance = 1e-12)
        => !(stdDev > relativeTolerance * Math.Max(Math.Abs(scale), 1e-300));

    /// <summary>Größter Absolutwert einer Reihe — als Skalenniveau für <see cref="IsNegligibleDeviation"/>.</summary>
    public static double MaxAbs(IReadOnlyList<double> x)
    {
        double m = 0;
        foreach (var v in x) { double a = Math.Abs(v); if (a > m) m = a; }
        return m;
    }

    /// <summary>Pearson-Korrelation. Null, wenn n &lt; 2 oder eine Reihe konstant ist.</summary>
    public static double? Correlation(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a.Count != b.Count || a.Count < 2) return null;
        double ma = Mean(a), mb = Mean(b), sab = 0, sa = 0, sb = 0;
        for (int i = 0; i < a.Count; i++)
        {
            double da = a[i] - ma, db = b[i] - mb;
            sab += da * db; sa += da * da; sb += db * db;
        }
        if (sa <= 0 || sb <= 0) return null;
        return sab / Math.Sqrt(sa * sb);
    }

    /// <summary>
    /// Verteilungsfunktion der Standardnormalverteilung. Nutzt die komplementäre Fehlerfunktion
    /// nach Numerical Recipes (erfc), relative Genauigkeit besser als 1,2e−7.
    /// </summary>
    public static double NormalCdf(double z) => 0.5 * Erfc(-z / Math.Sqrt(2.0));

    /// <summary>Komplementäre Fehlerfunktion (Chebyshev-Approximation, Numerical Recipes).</summary>
    public static double Erfc(double x)
    {
        double z = Math.Abs(x);
        double t = 2.0 / (2.0 + z);
        double ty = 4.0 * t - 2.0;
        double[] cof =
        {
            -1.3026537197817094, 6.4196979235649026e-1, 1.9476473204185836e-2, -9.561514786808631e-3,
            -9.46595344482036e-4, 3.66839497852761e-4, 4.2523324806907e-5, -2.0278578112534e-5,
            -1.624290004647e-6, 1.303655835580e-6, 1.5626441722e-8, -8.5238095915e-8,
            6.529054439e-9, 5.059343495e-9, -9.91364156e-10, -2.27365122e-10,
            9.6467911e-11, 2.394038e-12, -6.886027e-12, 8.94487e-13, 3.13092e-13,
            -1.12708e-13, 3.81e-16, 7.106e-15
        };
        double d = 0.0, dd = 0.0;
        for (int j = cof.Length - 1; j > 0; j--)
        {
            double tmp = d;
            d = ty * d - dd + cof[j];
            dd = tmp;
        }
        double ans = t * Math.Exp(-z * z + 0.5 * (cof[0] + ty * d) - dd);
        return x >= 0.0 ? ans : 2.0 - ans;
    }

    /// <summary>Quantilfunktion der Standardnormalverteilung (Acklam-Approximation, |Fehler| &lt; 1,15e−9).</summary>
    public static double NormalInverseCdf(double p)
    {
        if (p <= 0 || p >= 1) throw new ArgumentOutOfRangeException(nameof(p), "p muss in (0,1) liegen.");
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        const double pLow = 0.02425, pHigh = 1 - pLow;

        if (p < pLow)
        {
            double q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                   ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p > pHigh)
        {
            double q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                    ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        double r = p - 0.5, s = r * r;
        return (((((a[0] * s + a[1]) * s + a[2]) * s + a[3]) * s + a[4]) * s + a[5]) * r /
               (((((b[0] * s + b[1]) * s + b[2]) * s + b[3]) * s + b[4]) * s + 1);
    }
}
