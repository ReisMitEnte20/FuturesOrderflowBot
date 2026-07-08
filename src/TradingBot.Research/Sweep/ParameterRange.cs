using System.Globalization;

namespace TradingBot.Research.Sweep;

/// <summary>
/// Ein Parameter mit den zu testenden Werten (als Strings – passend zu StrategyConfig.Parameters).
/// Unterstützt Integer-, Decimal- und Boolean-Bereiche sowie explizite Wertlisten.
/// Deterministische, endliche Wertmenge (kein endloser Sweep).
/// </summary>
public sealed record ParameterRange
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Values { get; init; }

    public static ParameterRange Ints(string name, int from, int to, int step = 1)
    {
        if (step <= 0) throw new ArgumentOutOfRangeException(nameof(step), "step muss > 0 sein.");
        var values = new List<string>();
        for (int v = from; v <= to; v += step) values.Add(v.ToString(CultureInfo.InvariantCulture));
        return new ParameterRange { Name = name, Values = values };
    }

    public static ParameterRange Decimals(string name, decimal from, decimal to, decimal step)
    {
        if (step <= 0m) throw new ArgumentOutOfRangeException(nameof(step), "step muss > 0 sein.");
        var values = new List<string>();
        for (decimal v = from; v <= to; v += step) values.Add(v.ToString(CultureInfo.InvariantCulture));
        return new ParameterRange { Name = name, Values = values };
    }

    public static ParameterRange Booleans(string name)
        => new() { Name = name, Values = new[] { "true", "false" } };

    public static ParameterRange Explicit(string name, params string[] values)
        => new() { Name = name, Values = values };
}
