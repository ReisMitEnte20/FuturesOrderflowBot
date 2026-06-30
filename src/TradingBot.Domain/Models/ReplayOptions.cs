using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>Steuert das Abspielen historischer Ticks im Replay-Feed.</summary>
public sealed record ReplayOptions
{
    public ReplayMode Mode { get; init; } = ReplayMode.AsFastAsPossible;

    /// <summary>Beschleunigungsfaktor für <see cref="ReplayMode.FasterThanRealtime"/> (&gt; 0).</summary>
    public double SpeedFactor { get; init; } = 1.0;

    public static ReplayOptions Fast { get; } = new() { Mode = ReplayMode.AsFastAsPossible };
    public static ReplayOptions Realtime { get; } = new() { Mode = ReplayMode.RealTime };

    /// <summary>Berechnet die Wartezeit zwischen zwei aufeinanderfolgenden Ticks.</summary>
    public TimeSpan DelayFor(TimeSpan tickGap)
    {
        if (tickGap < TimeSpan.Zero) tickGap = TimeSpan.Zero;
        return Mode switch
        {
            ReplayMode.AsFastAsPossible => TimeSpan.Zero,
            ReplayMode.RealTime => tickGap,
            ReplayMode.FasterThanRealtime => SpeedFactor > 0
                ? TimeSpan.FromTicks((long)(tickGap.Ticks / SpeedFactor))
                : TimeSpan.Zero,
            _ => TimeSpan.Zero
        };
    }
}
