using FluentAssertions;
using TradingBot.DevDashboard.Services;
using TradingBot.DevDashboard.Services.Quant;
using TradingBot.Quant.Registry;
using Xunit;

namespace TradingBot.Tests.DevDashboard;

/// <summary>
/// Nachprüfung: Das TATSÄCHLICH verwendete Auswahlkriterium (request.SelectionMetric) ist verbindlich an
/// das gesperrte Kampagnenkriterium (request.Campaign.SelectionMetric) gekoppelt. Widersprüchliche Angaben
/// werden VOR Reservierung und Strategieausführung abgelehnt — es entstehen keine Versuche und keine
/// Auswertung.
/// </summary>
public class QuantSelectionMetricLockTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "quant-selmetric-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_repo)) Directory.Delete(_repo, recursive: true);
        GC.SuppressFinalize(this);
    }

    private QuantApiService Service() => new(new BacktestApiService(_repo), _repo);

    private static Dictionary<string, string>[] OneCandidate() => new[]
    {
        new Dictionary<string, string> { ["FastPeriod"] = "9", ["SlowPeriod"] = "21" }
    };

    [Fact]
    public async Task Walk_forward_rejects_a_request_metric_that_contradicts_the_locked_campaign_criterion()
    {
        var svc = Service();
        var request = new QuantWalkForwardRequest
        {
            Run = new BacktestRunRequest(),
            SelectionMetric = "netprofit",                       // tatsächlich angefragtes Kriterium
            Candidates = OneCandidate(),
            Campaign = new CampaignInput
            {
                Id = "c-selmetric",
                SearchSpace = "FastPeriod, SlowPeriod",
                SelectionMetric = "sharpe",                      // gesperrtes Kampagnenkriterium
                TrialBudget = 4
            }
        };

        var resp = await svc.WalkForwardAsync(request);

        resp.Ok.Should().BeFalse();
        resp.Error.Should().Contain("SELECTION_METRIC_MISMATCH");
        resp.Error.Should().Contain("sharpe");
        resp.Error.Should().Contain("netprofit");

        // Keine neuen Versuche und keine Auswertung: weder Kampagne noch Trials wurden angelegt.
        (await svc.Store.ListCampaignsAsync()).Should().BeEmpty();
        (await svc.Store.ListTrialsAsync(null)).Should().BeEmpty();
    }

    [Fact]
    public async Task Walk_forward_does_not_reject_a_matching_metric_despite_surrounding_whitespace_and_casing()
    {
        // Gegenprobe: gleiches Kriterium (nur andere Groß-/Kleinschreibung + umgebende Leerzeichen) darf NICHT
        // am Mismatch scheitern. Der Lauf schlägt hier später mangels lokaler Daten/Instrumentprofil fehl —
        // aber NICHT mit SELECTION_METRIC_MISMATCH. Das normalisierte (getrimmte) Kriterium wird ausgeführt,
        // es fällt also nicht still auf Sharpe zurück.
        var svc = Service();
        var request = new QuantWalkForwardRequest
        {
            Run = new BacktestRunRequest(),
            SelectionMetric = "  NetProfit  ",
            Candidates = OneCandidate(),
            Campaign = new CampaignInput { Id = "c-match", SearchSpace = "x", SelectionMetric = "netprofit", TrialBudget = 2 }
        };

        var resp = await svc.WalkForwardAsync(request);

        (resp.Error ?? string.Empty).Should().NotContain("SELECTION_METRIC_MISMATCH");
    }

    private async Task<QuantApiService> ServiceWithLockedCampaign(string id, string metric)
    {
        var svc = Service();
        await svc.Store.CreateCampaignAsync(new CampaignRecord
        {
            Id = id, Name = "Testkampagne", Hypothesis = "Test ohne Edge-Behauptung.",
            SearchSpace = "FastPeriod, SlowPeriod", SelectionMetric = metric, TrialBudget = 4
        });
        return svc;
    }

    [Fact]
    public async Task Existing_sharpe_campaign_rejects_a_netprofit_request_even_with_empty_campaign_criterion()
    {
        // Die Lücke: leeres request.Campaign.SelectionMetric übersprang bislang beide Prüfungen. Jetzt wird
        // das tatsächlich verwendete Kriterium DIREKT mit dem GESPEICHERTEN, gesperrten Wert verglichen.
        var svc = await ServiceWithLockedCampaign("locked-sharpe", "sharpe");

        var request = new QuantWalkForwardRequest
        {
            Run = new BacktestRunRequest(),
            SelectionMetric = "netprofit",
            Candidates = OneCandidate(),
            Campaign = new CampaignInput
            {
                Id = "locked-sharpe", SearchSpace = "FastPeriod, SlowPeriod", SelectionMetric = "", TrialBudget = 4
            }
        };

        var resp = await svc.WalkForwardAsync(request);

        resp.Ok.Should().BeFalse();
        resp.Error.Should().Contain("SELECTION_METRIC_MISMATCH");
        resp.Error.Should().Contain("sharpe");        // der gespeicherte, gesperrte Wert
        resp.Error.Should().Contain("netprofit");

        // Keine zusätzlichen Versuche unter der gesperrten Kampagne.
        (await svc.Store.ListTrialsAsync("locked-sharpe")).Should().BeEmpty();
    }

    [Fact]
    public async Task Existing_sharpe_campaign_rejects_an_empty_request_criterion()
    {
        var svc = await ServiceWithLockedCampaign("locked-sharpe2", "sharpe");

        var request = new QuantWalkForwardRequest
        {
            Run = new BacktestRunRequest(),
            SelectionMetric = "   ",                    // leer/Whitespace
            Candidates = OneCandidate(),
            Campaign = new CampaignInput
            {
                Id = "locked-sharpe2", SearchSpace = "FastPeriod, SlowPeriod", SelectionMetric = "sharpe", TrialBudget = 4
            }
        };

        var resp = await svc.WalkForwardAsync(request);

        resp.Ok.Should().BeFalse();
        resp.Error.Should().Contain("SELECTION_METRIC_MISSING");
        (await svc.Store.ListTrialsAsync("locked-sharpe2")).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_selection_metric_is_rejected_instead_of_falling_back_to_sharpe()
    {
        var svc = Service();
        var request = new QuantWalkForwardRequest
        {
            Run = new BacktestRunRequest(),
            SelectionMetric = "profitfactor",           // nicht unterstützt
            Candidates = OneCandidate(),
            Campaign = new CampaignInput { Id = "c-unknown", SearchSpace = "s", SelectionMetric = "profitfactor", TrialBudget = 2 }
        };

        var resp = await svc.WalkForwardAsync(request);

        resp.Ok.Should().BeFalse();
        resp.Error.Should().Contain("SELECTION_METRIC_UNKNOWN");
        (await svc.Store.ListCampaignsAsync()).Should().BeEmpty();   // keine Kampagne angelegt
    }
}
