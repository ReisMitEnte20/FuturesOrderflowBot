using FluentAssertions;
using TradingBot.DevDashboard.Services;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.PaperTrading;
using Xunit;

namespace TradingBot.Tests.DevDashboard;

public class PaperDemoServiceTests
{
    private static string RepoRoot =>
        RepoLocator.FindRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("Repo-Wurzel nicht gefunden.");

    private static PaperDemoService NewService() => new(RepoRoot);

    [Fact]
    public async Task StartDemo_runs_and_completes_with_sample_csv()
    {
        var service = NewService();

        var session = await service.StartDemoAsync(ReplayOptions.Fast);

        session.Should().NotBeNull(service.LastError);
        var result = await session!.Completion;

        result.Status.Should().Be(PaperTradingRunStatus.Completed);
        result.TicksProcessed.Should().Be(120);            // Demo-CSV hat 120 Ticks
        result.FinalState.TradingMode.Should().Be(TradingMode.Paper);
        result.SignalsGenerated.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Demo_session_is_a_paper_trading_session()
    {
        var service = NewService();
        var session = await service.StartDemoAsync(ReplayOptions.Fast);

        session.Should().BeOfType<PaperTradingSession>(); // nur PaperTradingEngine/PaperExecutionAdapter
        await session!.Completion;
    }

    [Fact]
    public async Task Start_pause_resume_stop_transition_session_state()
    {
        var service = NewService();
        // RealTime: Ticks kommen alle 500 ms -> Session lebt lange genug für Transitionen.
        var session = await service.StartDemoAsync(ReplayOptions.Realtime);
        session.Should().NotBeNull(service.LastError);

        // Der Hintergrund-Replay-Loop setzt _status asynchron auf Running -> darauf warten,
        // sonst racet die Assertion (Status == Paused gilt nur, wenn _status bereits Running ist).
        await WaitUntilAsync(() => session!.Status == PaperTradingRunStatus.Running);

        service.Pause();
        session!.Status.Should().Be(PaperTradingRunStatus.Paused);
        service.GetState()!.IsPaused.Should().BeTrue();

        service.Resume();
        await WaitUntilAsync(() => session.Status == PaperTradingRunStatus.Running);
        session.Status.Should().Be(PaperTradingRunStatus.Running);

        await service.StopAsync();
        session.Status.Should().Be(PaperTradingRunStatus.Stopped);
        session.IsRunning.Should().BeFalse();
    }

    /// <summary>Pollt eine Bedingung bis Timeout (stabilisiert Tests gegen den async Replay-Loop; keine Feature-Änderung).</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(15);
        condition().Should().BeTrue($"Bedingung wurde nicht innerhalb von {timeoutMs} ms erfüllt.");
    }

    [Fact]
    public async Task Double_start_returns_same_running_session()
    {
        var service = NewService();
        var first = await service.StartDemoAsync(ReplayOptions.Realtime);
        var second = await service.StartDemoAsync(ReplayOptions.Realtime);

        second.Should().BeSameAs(first); // kein Doppelstart
        await service.StopAsync();
    }

    [Fact]
    public async Task Missing_repo_root_sets_error_and_returns_null()
    {
        var service = new PaperDemoService(null);
        var session = await service.StartDemoAsync(ReplayOptions.Fast);

        session.Should().BeNull();
        service.LastError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task StartDemo_does_not_modify_config_files()
    {
        var configDir = Path.Combine(RepoRoot, "config");
        var before = Directory.GetFiles(configDir, "*.json", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.GetLastWriteTimeUtc);

        var service = NewService();
        var session = await service.StartDemoAsync(ReplayOptions.Fast);
        await session!.Completion;

        foreach (var (file, stamp) in before)
            File.GetLastWriteTimeUtc(file).Should().Be(stamp, $"Config darf nur gelesen werden: {file}");
    }

    [Fact]
    public void Dashboard_does_not_reference_execution_or_broker_sdks()
    {
        var referenced = typeof(PaperDemoService).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToList();

        referenced.Should().NotContain(n => n.Contains("TradingBot.Execution"));
        referenced.Should().NotContain(n =>
            n.Contains("Rithmic", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("CQG", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Tradovate", StringComparison.OrdinalIgnoreCase));
        referenced.Should().Contain(n => n == "TradingBot.PaperTrading"); // Simulation ist der einzige Weg
    }
}
