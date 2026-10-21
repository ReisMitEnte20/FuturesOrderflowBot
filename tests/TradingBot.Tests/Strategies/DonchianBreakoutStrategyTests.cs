using FluentAssertions;
using TradingBot.Application.Strategies;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using Xunit;

namespace TradingBot.Tests.Strategies;

/// <summary>
/// Deterministische OHLC-Referenzstrategie (Donchian-Kanalausbruch). Tests belegen: deterministische Signale,
/// Ausbruch nur bei Richtungswechsel, kein Lookahead auf die eigene Bar. KEIN Profitabilitätsnachweis.
/// </summary>
public sealed class DonchianBreakoutStrategyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DonchianBreakoutStrategy New(int channel)
    {
        var s = new DonchianBreakoutStrategy();
        s.Initialize(new StrategyExecutionContext
        {
            Symbol = "MES",
            Config = new StrategyConfig
            {
                Name = "Donchian", Symbol = "MES",
                Parameters = new Dictionary<string, string> { ["Channel"] = channel.ToString() }
            }
        });
        return s;
    }

    private static Candle Bar(int i, decimal high, decimal low, decimal close) => new()
    {
        Symbol = "MES",
        OpenTime = T0.AddMinutes(i), CloseTime = T0.AddMinutes(i + 1),
        Open = close, High = high, Low = low, Close = close
    };

    private static List<TradeSignal> Run(DonchianBreakoutStrategy s, IEnumerable<Candle> candles)
    {
        var signals = new List<TradeSignal>();
        foreach (var c in candles)
        {
            var sig = s.OnCandle(c);
            if (sig is not null) signals.Add(sig);
        }
        return signals;
    }

    private static Candle[] Sequence() => new[]
    {
        Bar(0, 10m, 8m, 9m),   // Warmup (kein Kanal)
        Bar(1, 10m, 8m, 9m),   // Warmup
        Bar(2, 12m, 8m, 12m),  // Close 12 > oberes Kanal-High 10 -> Long
        Bar(3, 12m, 8m, 7m),   // Close 7 < unteres Kanal-Low 8 -> Short
        Bar(4, 13m, 7m, 13m),  // Close 13 > oberes Kanal-High 12 -> Long
        Bar(5, 14m, 9m, 14m),  // weiterer Ausbruch nach oben -> KEINE Wiederholung (Richtung unverändert)
    };

    [Fact]
    public void Emits_long_short_long_on_breakout_direction_changes()
    {
        var signals = Run(New(2), Sequence());

        signals.Select(x => x.Direction).Should().Equal(
            SignalDirection.Long, SignalDirection.Short, SignalDirection.Long);
        // Kein viertes Signal: Bar 5 ist derselbe Ausbruch nach oben wie Bar 4.
        signals.Should().HaveCount(3);
        signals[0].Timestamp.Should().Be(T0.AddMinutes(3)); // CloseTime der Bar 2
    }

    [Fact]
    public void Is_deterministic_same_candles_same_signals()
    {
        var a = Run(New(2), Sequence());
        var b = Run(New(2), Sequence());

        a.Select(x => (x.Direction, x.Timestamp, x.ReferencePrice))
            .Should().Equal(b.Select(x => (x.Direction, x.Timestamp, x.ReferencePrice)));
    }

    [Fact]
    public void No_signal_while_price_stays_inside_channel()
    {
        // Alle Closes bleiben zwischen dem Kanal-Low (8) und -High (10) -> nie ein Ausbruch.
        var flat = new[]
        {
            Bar(0, 10m, 8m, 9m), Bar(1, 10m, 8m, 9m),
            Bar(2, 10m, 8m, 9m), Bar(3, 10m, 8m, 9m), Bar(4, 10m, 8m, 9m),
        };
        Run(New(2), flat).Should().BeEmpty();
    }

    [Fact]
    public void Current_bar_high_low_do_not_feed_its_own_channel_no_lookahead()
    {
        // Channel 1: Kanal = genau die vorherige Bar. Steigt der Close über deren High -> Ausbruch.
        // Würde die AKTUELLE Bar ihr eigenes High in den Kanal einspeisen, könnte Close nie > eigenes High sein.
        var seq = new[]
        {
            Bar(0, 10m, 8m, 9m),   // Warmup
            Bar(1, 15m, 8m, 15m),  // Close 15 > vorheriges High 10 -> Long (Beweis: eigene High=15 zählt NICHT im Kanal)
        };
        var signals = Run(New(1), seq);
        signals.Should().ContainSingle().Which.Direction.Should().Be(SignalDirection.Long);
    }
}
