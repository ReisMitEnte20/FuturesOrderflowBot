using System.Globalization;
using System.Text;
using FluentAssertions;
using TradingBot.Infrastructure.MarketData.Import;
using Xunit;

namespace TradingBot.Tests.MarketData;

/// <summary>
/// Sichert die gezielte lokale Zeitraumwahl per Byte-Offset-Binärsuche (<see cref="SierraOrderFlowBarBuilder.BuildFileFrom"/>)
/// gegen einen EINFACHEN sequenziellen Referenz-Import (<see cref="SierraOrderFlowBarBuilder.BuildFile"/> mit fromUtc,
/// der die ganze Datei liest und ts &lt; fromUtc verwirft). Beide müssen ab dem gewählten Zeitpunkt EXAKT dieselben
/// Records in derselben Reihenfolge liefern — insbesondere darf die erste passende Zeile (Grenzzeile) NICHT verloren
/// gehen. Abgedeckt: gleiche Zeitstempel, Ziel zwischen zwei Records, Datei-Start/-Ende, LF und CRLF.
/// </summary>
public class SierraByteOffsetSeekTests
{
    private const string Header =
        "Date, Time, Open, High, Low, Last, Volume, NumberOfTrades, BidVolume, AskVolume";

    /// <summary>
    /// Schreibt eine &gt; 8 KB große Testdatei (damit die Binärsuche echte Iterationen ausführt),
    /// 1 Tick/Sekunde, mit einer Stelle aus drei Records mit EXAKT gleichem Zeitstempel.
    /// Gibt Pfad und die Zeitstempel je Record in Lesereihenfolge zurück.
    /// </summary>
    private static (string path, DateTimeOffset[] times) WriteFile(string nl, int seconds = 3000)
    {
        var t0 = new DateTimeOffset(2025, 12, 28, 23, 0, 0, TimeSpan.Zero);
        var sb = new StringBuilder();
        sb.Append(Header).Append(nl);
        var times = new List<DateTimeOffset>();
        for (int i = 0; i < seconds; i++)
        {
            var t = t0.AddSeconds(i);
            int dup = i == 1000 ? 3 : 1;   // an einer Stelle drei Records mit gleichem Zeitstempel
            for (int d = 0; d < dup; d++)
            {
                decimal p = 100m + (i % 40) * 0.25m + d * 0.05m;
                string ps = p.ToString("0.00", CultureInfo.InvariantCulture);
                sb.Append(t.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)).Append(", ")
                  .Append(t.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append(", ")
                  .Append(ps).Append(", ").Append(ps).Append(", ").Append(ps).Append(", ").Append(ps)
                  .Append(", 1, 1, 0, 1").Append(nl);
                times.Add(t);
            }
        }
        var path = Path.Combine(Path.GetTempPath(), $"sierra_seek_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, sb.ToString());
        return (path, times.ToArray());
    }

    private static IReadOnlyList<(DateTimeOffset Open, decimal O, decimal H, decimal L, decimal C, decimal V)> Bars(
        SierraAggregationResult r)
        => r.Bars.Select(b => (b.Bar.OpenTime, b.Bar.Open, b.Bar.High, b.Bar.Low, b.Bar.Close, b.Bar.TotalVolume)).ToList();

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Seek_matches_sequential_reference_for_various_targets(string nl)
    {
        var (path, times) = WriteFile(nl);
        try
        {
            var builder = new SierraOrderFlowBarBuilder();
            var interval = TimeSpan.FromMinutes(1);
            var targets = new (string Name, DateTimeOffset From)[]
            {
                ("vor Dateianfang",        times[0].AddSeconds(-60)),
                ("exakt erster Record",    times[0]),
                ("exakt Record mittig",    times[1500]),
                ("zwischen zwei Records",  times[1500].AddMilliseconds(500)),
                ("Duplikat-Zeitstempel",   times[1000]),
                ("letzter Record",         times[^1]),
                ("nach Dateiende",         times[^1].AddSeconds(60)),
            };

            foreach (var (name, from) in targets)
            {
                var reference = builder.BuildFile(path, "MES", interval, fromUtc: from);      // sequenzielle Referenz
                var seek = builder.BuildFileFrom(path, "MES", interval, from);                // Byte-Offset-Suche

                seek.ValidTicks.Should().Be(reference.ValidTicks, $"ValidTicks [{name}] {nl.Length}");
                seek.FirstTickTime.Should().Be(reference.FirstTickTime, $"FirstTickTime [{name}] darf die Grenzzeile nicht verlieren");
                seek.LastTickTime.Should().Be(reference.LastTickTime, $"LastTickTime [{name}]");
                Bars(seek).Should().Equal(Bars(reference), $"Bars [{name}] müssen exakt übereinstimmen");
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Duplicate_timestamp_boundary_keeps_all_matching_records()
    {
        // Ziel == Zeitstempel von drei identischen Records: alle drei müssen enthalten sein (keiner verloren).
        var (path, times) = WriteFile("\n");
        try
        {
            var from = times[1000];   // die Stelle mit drei gleichen Zeitstempeln
            var seek = new SierraOrderFlowBarBuilder().BuildFileFrom(path, "MES", TimeSpan.FromMinutes(1), from);
            var reference = new SierraOrderFlowBarBuilder().BuildFile(path, "MES", TimeSpan.FromMinutes(1), fromUtc: from);

            seek.FirstTickTime.Should().Be(from);
            // Anzahl Records mit ts == from im Referenzlauf:
            reference.FirstTickTime.Should().Be(from);
            seek.ValidTicks.Should().Be(reference.ValidTicks);
            seek.Bars[0].Bar.OpenTime.Should().Be(reference.Bars[0].Bar.OpenTime);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Seek_offset_is_nontrivial_for_midfile_target_and_hits_exact_record()
    {
        var (path, times) = WriteFile("\n");
        try
        {
            long off = SierraOrderFlowBarBuilder.FindByteOffsetForDate(path, times[1500]);
            off.Should().BeGreaterThan(0, "ein Ziel mitten in einer > 8 KB Datei erfordert einen echten Seek");

            var seek = new SierraOrderFlowBarBuilder().BuildFileFrom(path, "MES", TimeSpan.FromMinutes(1), times[1500]);
            seek.FirstTickTime.Should().Be(times[1500], "der erste gelesene gültige Tick ist genau die Grenzzeile");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Target_after_end_yields_no_bars_like_reference()
    {
        var (path, times) = WriteFile("\n");
        try
        {
            var from = times[^1].AddSeconds(300);
            var seek = new SierraOrderFlowBarBuilder().BuildFileFrom(path, "MES", TimeSpan.FromMinutes(1), from);
            seek.BarsCreated.Should().Be(0);
            seek.ValidTicks.Should().Be(0);
            seek.FirstTickTime.Should().BeNull();
        }
        finally { File.Delete(path); }
    }
}
