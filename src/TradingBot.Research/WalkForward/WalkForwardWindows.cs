namespace TradingBot.Research.WalkForward;

/// <summary>
/// Reiner, deterministischer Generator der Walk-Forward-Fenster. Garantiert: OOS beginnt exakt
/// am IS-Ende (KEINE Überlappung) und liegt zeitlich NACH dem IS – so kann OOS niemals in die
/// Selektion einfließen.
/// </summary>
public static class WalkForwardWindows
{
    public static IReadOnlyList<WalkForwardWindow> Generate(
        int totalCount, int inSampleSize, int outOfSampleSize, int step, WalkForwardMode mode)
    {
        if (inSampleSize <= 0) throw new ArgumentOutOfRangeException(nameof(inSampleSize));
        if (outOfSampleSize <= 0) throw new ArgumentOutOfRangeException(nameof(outOfSampleSize));
        if (step <= 0) throw new ArgumentOutOfRangeException(nameof(step));

        var windows = new List<WalkForwardWindow>();
        int k = 0;
        while (true)
        {
            int isStart = mode == WalkForwardMode.Rolling ? k * step : 0;
            int isEnd = mode == WalkForwardMode.Rolling ? isStart + inSampleSize : inSampleSize + k * step;
            int oosStart = isEnd;
            int oosEnd = oosStart + outOfSampleSize;

            if (oosEnd > totalCount) break;

            windows.Add(new WalkForwardWindow
            {
                Index = k,
                InSampleStart = isStart, InSampleEnd = isEnd,
                OutOfSampleStart = oosStart, OutOfSampleEnd = oosEnd
            });
            k++;
        }
        return windows;
    }
}
