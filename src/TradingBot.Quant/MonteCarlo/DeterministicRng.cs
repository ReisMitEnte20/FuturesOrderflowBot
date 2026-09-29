namespace TradingBot.Quant.MonteCarlo;

/// <summary>
/// Reproduzierbarer Zufallszahlengenerator (xoshiro256**, Startzustand über SplitMix64).
///
/// Bewusst eigenständig implementiert: Reproduzierbarkeit ist hier eine fachliche Anforderung.
/// Derselbe Seed liefert auf jeder Plattform und in jeder Laufzeitversion exakt dieselbe Folge —
/// das gilt für <see cref="System.Random"/> nicht garantiert.
/// </summary>
public sealed class DeterministicRng
{
    private ulong _s0, _s1, _s2, _s3;

    public int Seed { get; }

    public DeterministicRng(int seed)
    {
        Seed = seed;
        ulong x = unchecked((ulong)seed + 0x9E3779B97F4A7C15UL);
        _s0 = SplitMix64(ref x);
        _s1 = SplitMix64(ref x);
        _s2 = SplitMix64(ref x);
        _s3 = SplitMix64(ref x);
    }

    private static ulong SplitMix64(ref ulong x)
    {
        ulong z = unchecked(x += 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    public ulong NextUInt64()
    {
        ulong result = unchecked(Rotl(_s1 * 5UL, 7) * 9UL);
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = Rotl(_s3, 45);
        return result;
    }

    /// <summary>Gleichverteilt in [0,1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>Gleichverteilte ganze Zahl in [0, exclusiveMax) — ohne Modulo-Verzerrung (Lemire mit Rejection).</summary>
    public int NextInt(int exclusiveMax)
    {
        if (exclusiveMax <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMax));
        ulong range = (ulong)exclusiveMax;
        ulong limit = ulong.MaxValue - ulong.MaxValue % range;
        ulong v;
        do { v = NextUInt64(); } while (v >= limit);
        return (int)(v % range);
    }

    /// <summary>Fisher-Yates-Permutation an Ort und Stelle.</summary>
    public void Shuffle<T>(IList<T> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = NextInt(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>Geometrisch verteilte Blocklänge mit Erwartungswert 1/p (für den stationären Bootstrap).</summary>
    public int NextGeometric(double p)
    {
        if (p is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(p));
        if (p >= 1.0) return 1;
        double u = NextDouble();
        if (u <= 0) u = double.Epsilon;
        return Math.Max(1, (int)Math.Ceiling(Math.Log(u) / Math.Log(1.0 - p)));
    }
}
