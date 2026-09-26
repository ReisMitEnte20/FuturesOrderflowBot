using FluentAssertions;
using TradingBot.DevDashboard.Services.Quant;

namespace TradingBot.Tests.DevDashboard;

/// <summary>
/// Der Job-Manager trägt die Anforderungen „Fortschritt, Abbruch und sinnvolle Laufzeitbegrenzung"
/// für die lang laufenden Quant-Verfahren. Genau das wird hier geprüft.
/// </summary>
public class QuantJobManagerTests
{
    private static async Task<QuantJobState> WaitForAsync(QuantJobManager jobs, string id, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var s = jobs.Get(id);
            if (s is not null && s.Status != QuantJobStatus.Running) return s;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Job '{id}' wurde innerhalb von {timeout} nicht fertig.");
    }

    [Fact]
    public async Task Completed_job_reports_its_result_and_full_progress()
    {
        using var jobs = new QuantJobManager();

        var id = jobs.Start("test", async (progress, ct) =>
        {
            progress.Report(0.5);
            await Task.Delay(10, ct);
            return (object)"fertig";
        });

        var state = await WaitForAsync(jobs, id, TimeSpan.FromSeconds(10));

        state.Status.Should().Be(QuantJobStatus.Completed);
        state.Progress.Should().Be(1);
        state.Result.Should().Be("fertig");
        state.FinishedUtc.Should().NotBeNull();
        state.Error.Should().BeNull();
    }

    [Fact]
    public async Task Failing_job_reports_the_error_instead_of_a_result()
    {
        using var jobs = new QuantJobManager();

        var id = jobs.Start("test", (_, _) => throw new InvalidOperationException("Kennzahl nicht berechenbar"));
        var state = await WaitForAsync(jobs, id, TimeSpan.FromSeconds(10));

        state.Status.Should().Be(QuantJobStatus.Failed);
        state.Error.Should().Contain("nicht berechenbar");
        state.Result.Should().BeNull();
    }

    [Fact]
    public async Task Running_job_can_be_cancelled()
    {
        using var jobs = new QuantJobManager();

        var id = jobs.Start("test", async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            return (object)"nie erreicht";
        });

        jobs.Cancel(id).Should().BeTrue();
        var state = await WaitForAsync(jobs, id, TimeSpan.FromSeconds(10));

        state.Status.Should().Be(QuantJobStatus.Cancelled);
        state.Error.Should().Contain("abgebrochen");
        // Ein bereits beendeter Job lässt sich nicht erneut abbrechen.
        jobs.Cancel(id).Should().BeFalse();
    }

    [Fact]
    public async Task Time_limit_stops_a_job_that_runs_too_long()
    {
        using var jobs = new QuantJobManager();

        var id = jobs.Start("test", async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            return (object)"nie erreicht";
        }, timeLimit: TimeSpan.FromMilliseconds(120));

        var state = await WaitForAsync(jobs, id, TimeSpan.FromSeconds(10));

        state.Status.Should().Be(QuantJobStatus.Cancelled);
        state.Error.Should().NotBeNull();
    }

    [Fact]
    public async Task Listing_omits_results_so_the_overview_stays_small()
    {
        using var jobs = new QuantJobManager();

        var id = jobs.Start("test", (_, _) => Task.FromResult<object>(new string('x', 10_000)));
        await WaitForAsync(jobs, id, TimeSpan.FromSeconds(10));

        var list = jobs.List();
        list.Should().ContainSingle(j => j.Id == id);
        list.Single(j => j.Id == id).Result.Should().BeNull();
        jobs.Get(id)!.Result.Should().NotBeNull();
    }

    [Fact]
    public void Unknown_job_is_reported_as_unknown()
        => new QuantJobManager().Get("gibtsnicht").Should().BeNull();
}
