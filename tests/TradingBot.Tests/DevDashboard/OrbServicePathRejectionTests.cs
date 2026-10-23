using System.Globalization;
using System.Text;
using FluentAssertions;
using TradingBot.DevDashboard.Services;
using TradingBot.DevDashboard.Services.Quant;
using Xunit;

namespace TradingBot.Tests.DevDashboard;

/// <summary>
/// Belegt über den ECHTEN Research-Servicepfad (<see cref="QuantApiService.WalkForwardAsync"/>), dass ein ORB-Abschnitt
/// mit unvollständiger Session (Position offen über das vorgesehene Session-Ende hinaus bzw. am Abschnittsende =
/// tatsächliche Über-das-Session-Ende-Exposure) als Kandidat VERWORFEN wird: er liefert keine Out-of-Sample-Reihe und
/// kann damit nicht in Auswahl, Vergleich, Monte Carlo oder Overfitting eingehen. Eine Message allein genügt nicht —
/// die Ablehnung nutzt den vorhandenen Ungültigkeitsmechanismus (fehlgeschlagener Testabschnitt) mit konkretem Grund.
/// </summary>
[Collection("ResearchSerial")]
public sealed class OrbServicePathRejectionTests : IDisposable
{
    private readonly List<string> _dirs = new();
    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (var d in _dirs) try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        foreach (var f in _files) try { if (File.Exists(f)) File.Delete(f); } catch { }
        GC.SuppressFinalize(this);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradingBot.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo-Wurzel nicht gefunden.");
    }

    // 5-Min-Bars ab 09:30 ET (Sommer = 13:30 UTC): Tag 1 = volle Session (78 Bars, ORB schließt sauber 16:00 ET),
    // Tag 2 = nur 10 Bars (Session endet mitten im ORB-Trade → im Test-Fold unvollständig).
    private string WriteOrbCsv()
    {
        var path = Path.Combine(Path.GetTempPath(), "orb-reject-" + Guid.NewGuid().ToString("N") + ".csv");
        _files.Add(path);
        var sb = new StringBuilder("timestamp,open,high,low,close,volume\n");

        void Session(DateTimeOffset start, int bars)
        {
            for (int k = 0; k < bars; k++)
            {
                var t = start.AddMinutes(5 * k);
                // k==0: Opening Range aufwärts (O<C) → Long, Stop = Low; sonst flach auf 5005 (kein SL/TP-Treffer).
                (decimal o, decimal h, decimal l, decimal c) = k == 0
                    ? (5000m, 5010m, 4995m, 5005m)
                    : (5005m, 5005m, 5005m, 5005m);
                sb.Append(t.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
                  .Append(',').Append(o).Append(',').Append(h).Append(',').Append(l).Append(',').Append(c).Append(",1\n");
            }
        }

        Session(new DateTimeOffset(2026, 6, 16, 13, 30, 0, TimeSpan.Zero), 78);   // Tag 1: volle Session (Index 0..77)
        Session(new DateTimeOffset(2026, 6, 17, 13, 30, 0, TimeSpan.Zero), 10);   // Tag 2: nur 10 Bars (Index 78..87)
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private QuantApiService NewQuant()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "orb-reject-reg-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(tempRoot);
        return new QuantApiService(new BacktestApiService(RepoRoot()), tempRoot);
    }

    [Fact]
    public async Task Incomplete_orb_session_is_rejected_in_walk_forward_service_path()
    {
        var quant = NewQuant();
        var csv = WriteOrbCsv();

        var req = new QuantWalkForwardRequest
        {
            Run = new BacktestRunRequest
            {
                DataSourceId = "csv", Path = csv, Symbol = "MES", TimeframeMinutes = 5,
                Strategy = "orb", Params = new(), Quantity = 1, InitialBalance = 10_000m,
                StopLossTicks = 40, TakeProfitTicks = 60, SlippageTicks = 0m, ApplyFees = true,
                ExcludePartialEdges = false, MaxRows = 1000
            },
            Options = new QuantEvaluationOptions { Frequency = "Daily" },
            Mode = "Rolling", TrainBars = 78, TestBars = 10, StepBars = 10, HoldoutFraction = 0,
            WarmupBars = 0, LabelSpanBars = 0, EmbargoBars = 0,
            Candidates = new[] { new Dictionary<string, string>() },   // ORB = feste Baseline, ein Kandidat ohne Parameter
            SelectionMetric = "netprofit",
            Campaign = new CampaignInput { Id = "orb-service-" + Guid.NewGuid().ToString("N")[..6], SelectionMetric = "netprofit", TrialBudget = 4, HoldoutFraction = 0 }
        };

        var wf = await quant.WalkForwardAsync(req);

        wf.Ok.Should().BeTrue("WF-Antwort: " + (wf.Error ?? "(kein Fehler)") + " | Notes: " + string.Join(" || ", wf.Notes));
        wf.OosT.Should().BeEmpty("der einzige Testabschnitt endet mitten in der ORB-Session und wird als unvollständig verworfen");
        wf.Notes.Should().Contain(n => n.Contains("Unvollständige Session") || n.Contains("Testlauf fehlgeschlagen"),
            "die Ablehnung nennt den konkreten Grund (unvollständige Session / fehlgeschlagener Testabschnitt)");
    }

    // 8 vollständige RTH-Sessions (reguläre Handelstage, EDT: NY 09:30 = 13:30 UTC), je 78 5-Min-Bars 09:30→16:00 ET.
    private string WriteCompleteOrbCsv()
    {
        var days = new[]
        {
            new DateTimeOffset(2026, 3, 16, 13, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 17, 13, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 18, 13, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 19, 13, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 20, 13, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 23, 13, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 24, 13, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 25, 13, 30, 0, TimeSpan.Zero),
        };
        var path = Path.Combine(Path.GetTempPath(), "orb-complete-" + Guid.NewGuid().ToString("N") + ".csv");
        _files.Add(path);
        var sb = new StringBuilder("timestamp,open,high,low,close,volume\n");
        foreach (var day in days)
            for (int k = 0; k < 78; k++)   // 09:30 → 16:00 ET
            {
                var t = day.AddMinutes(5 * k);
                (decimal o, decimal h, decimal l, decimal c) = k == 0
                    ? (5000m, 5010m, 4995m, 5005m)   // Opening Range aufwärts → Long
                    : (5005m, 5005m, 5005m, 5005m);
                sb.Append(t.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
                  .Append(',').Append(o).Append(',').Append(h).Append(',').Append(l).Append(',').Append(c).Append(",1\n");
            }
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    [Fact]
    public async Task Complete_orb_dataset_produces_multiple_valid_oos_windows_session_aligned()
    {
        var quant = NewQuant();
        var csv = WriteCompleteOrbCsv();

        var req = new QuantWalkForwardRequest
        {
            Run = new BacktestRunRequest
            {
                DataSourceId = "csv", Path = csv, Symbol = "MES", TimeframeMinutes = 5,
                Strategy = "orb", Params = new(), Quantity = 1, InitialBalance = 10_000m,
                StopLossTicks = 40, TakeProfitTicks = 60, SlippageTicks = 0m, ApplyFees = true,
                ExcludePartialEdges = false, MaxRows = 10_000
            },
            Options = new QuantEvaluationOptions { Frequency = "Daily" },
            Mode = "Rolling",
            SessionAligned = true, TrainSessions = 4, TestSessions = 2, HoldoutFraction = 0,
            WarmupBars = 0, LabelSpanBars = 0, EmbargoBars = 0,
            Candidates = new[] { new Dictionary<string, string>() },
            SelectionMetric = "netprofit",
            Campaign = new CampaignInput { Id = "orb-aligned-" + Guid.NewGuid().ToString("N")[..6], SelectionMetric = "netprofit", TrialBudget = 4, HoldoutFraction = 0 }
        };

        var wf = await quant.WalkForwardAsync(req);

        wf.Ok.Should().BeTrue("WF-Antwort: " + (wf.Error ?? "(kein Fehler)") + " | Notes: " + string.Join(" || ", wf.Notes));
        wf.Folds.Should().HaveCount(2, "8 Sessions, Train 4 / Test 2, disjunkt → zwei OOS-Fenster");
        wf.OosT.Should().NotBeEmpty("vollständige Sessions → gültige, nicht verworfene OOS-Reihe");
        wf.Notes.Should().Contain(n => n.Contains("Session-ausgerichteter Walk-forward"));
        wf.Notes.Should().NotContain(n => n.Contains("Unvollständige Session"),
            "bei vollständigen Sessions keine Unvollständigkeits-Ablehnung");
    }
}
