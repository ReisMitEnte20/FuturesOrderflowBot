using TradingBot.DevDashboard.Components;
using TradingBot.DevDashboard.Services;
using TradingBot.DevDashboard.Services.Quant;
using TradingBot.Quant.Research;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Read-only Dashboard-Services (keine Order-/Broker-/Execution-Referenz).
var repoRoot = RepoLocator.FindRoot(builder.Environment.ContentRootPath);
builder.Services.AddSingleton<RithmicDashboardService>();
builder.Services.AddSingleton<ProjectStatusService>();
builder.Services.AddSingleton(new GitInfoService(repoRoot));
builder.Services.AddSingleton(new ConfigOverviewService(repoRoot));
builder.Services.AddSingleton(new PaperDemoService(repoRoot)); // PAPER SIMULATION ONLY
builder.Services.AddSingleton<ResearchDemoService>();          // RESEARCH / SIMULATION ONLY (deterministische Demo)
builder.Services.AddSingleton<ReplayDemoService>();            // RESEARCH / SIMULATION ONLY (deterministisches Replay)
builder.Services.AddSingleton<SierraBacktestReplayService>(); // LOCAL HISTORICAL REPLAY / SIMULATION ONLY (lokale Datei, read-only)
var backtestApi = new BacktestApiService(repoRoot);
builder.Services.AddSingleton(backtestApi); // OHLC-BACKTEST (Simulation-only, keine Execution/Broker)
// QUANT-RESEARCH (Auswertung, Validierung, Monte Carlo, Overfitting) — rechnet ausschließlich lokal
// auf den Ergebnissen der OHLC-Engine. Keine Broker-/Marktdaten-Calls, keine Orders.
builder.Services.AddSingleton(new QuantApiService(backtestApi, repoRoot));
builder.Services.AddSingleton<QuantJobManager>();

// JSON: Enums als Strings (z. B. ExitReason) für das React-Frontend.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// CORS: das separate React-Dashboard (Vite) darf die API lokal aufrufen.
const string ReactCors = "react-dashboard";
builder.Services.AddCors(o => o.AddPolicy(ReactCors, p => p
    .WithOrigins("http://127.0.0.1:5899", "http://localhost:5899", "http://127.0.0.1:4173", "http://localhost:4173")
    .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseCors(ReactCors);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Rithmic API endpoints.
// Im lokalen Backtesting-Modus sind die EXTERNEN Rithmic-Endpunkte standardmäßig DEAKTIVIERT: /connect
// würde sonst einen echten HTTPS-Call an ein Broker-Gateway auslösen. Aktivierung nur explizit über die
// Konfiguration "Rithmic:Enabled" = true (z. B. Umgebungsvariable RITHMIC_ENABLED=true). "Im Test nicht
// aufgerufen" wäre keine Deaktivierung – deshalb wird der Service bei deaktiviertem Modus gar nicht erreicht.
bool rithmicEnabled = builder.Configuration.GetValue("Rithmic:Enabled", false);
var rithmic = app.MapGroup("/api/rithmic");

if (rithmicEnabled)
{
    rithmic.MapPost("/connect", async (RithmicConnectRequest request, RithmicDashboardService service) =>
    {
        var result = await service.ConnectAsync(request);
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    });

    rithmic.MapPost("/disconnect", async (RithmicDashboardService service) =>
    {
        var result = await service.DisconnectAsync();
        return Results.Ok(result);
    });

    rithmic.MapGet("/status", (RithmicDashboardService service) => Results.Ok(service.GetStatus()));
}
else
{
    const string disabledMsg = "Rithmic ist im lokalen Backtesting-Modus deaktiviert (keine externen Broker-Calls).";
    // /connect erreicht den Service NICHT -> kein externer HTTPS-Call möglich.
    rithmic.MapPost("/connect", () => Results.Json(
        new { enabled = false, success = false, message = disabledMsg },
        statusCode: StatusCodes.Status503ServiceUnavailable));
    rithmic.MapPost("/disconnect", () => Results.Ok(new { enabled = false, success = true, message = "Bereits deaktiviert." }));
    // /status ist ein rein lokaler Snapshot (kein externer Call) und meldet den deaktivierten Zustand.
    rithmic.MapGet("/status", () => Results.Ok(new { enabled = false, isConnected = false, lastError = (string?)null, message = disabledMsg }));
}

// OHLC-Backtest API (Simulation-only) für das React-Dashboard.
var bt = app.MapGroup("/api/backtest").RequireCors(ReactCors);

bt.MapGet("/strategies", (BacktestApiService s) => Results.Ok(s.GetStrategies()));
bt.MapGet("/instruments", async (BacktestApiService s, CancellationToken ct) => Results.Ok(await s.GetInstrumentsAsync(ct)));
bt.MapGet("/data-sources", (BacktestApiService s) => Results.Ok(s.GetDataSources()));
// Nur echte OHLC-Kerzen laden (ohne Strategielauf) — für „Daten laden" und das Bar-Replay.
bt.MapPost("/candles", async (BacktestRunRequest request, BacktestApiService s, CancellationToken ct) =>
{
    var result = await s.LoadDataAsync(request, ct);
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
});
bt.MapPost("/run", async (BacktestRunRequest request, BacktestApiService s, CancellationToken ct) =>
{
    var result = await s.RunAsync(request, ct);
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
});

// Quant-Research-API (Auswertung, Validierung, Monte Carlo, Overfitting, Versuchsregister).
// Alle Berechnungen laufen im Backend; das React-Dashboard stellt ausschließlich echte Ergebnisse dar.
// Lang laufende Verfahren werden als Job gestartet: mit Fortschritt, Abbruch und Laufzeitgrenze.
var quant = app.MapGroup("/api/quant").RequireCors(ReactCors);

quant.MapGet("/status", async (QuantApiService s, CancellationToken ct) =>
    Results.Ok(await s.GetStatusAsync(rithmicEnabled, ct)));

quant.MapGet("/benchmarks", async (QuantApiService s, CancellationToken ct) =>
    Results.Ok(await s.ListBenchmarksAsync(ct)));

// Einzelauswertung: schnell genug für einen direkten Aufruf.
quant.MapPost("/analyze", async (QuantAnalyzeRequest request, QuantApiService s, CancellationToken ct) =>
{
    var result = await s.AnalyzeAsync(request, null, ct);
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
});

// Lang laufende Verfahren als Job.
quant.MapPost("/jobs/walkforward", (QuantWalkForwardRequest request, QuantApiService s, QuantJobManager jobs) =>
    Results.Ok(new { jobId = jobs.Start("walkforward", async (p, ct) => await s.WalkForwardAsync(request, p, ct)) }));

quant.MapPost("/jobs/montecarlo", (QuantMonteCarloRequest request, QuantApiService s, QuantJobManager jobs) =>
    Results.Ok(new { jobId = jobs.Start("montecarlo", async (p, ct) => await s.MonteCarloAsync(request, p, ct)) }));

quant.MapPost("/jobs/robustness", (QuantRobustnessRequest request, QuantApiService s, QuantJobManager jobs) =>
    Results.Ok(new { jobId = jobs.Start("robustness", async (p, ct) => await s.RobustnessAsync(request, p, ct)) }));

quant.MapPost("/jobs/overfitting", (QuantOverfittingRequest request, QuantApiService s, QuantJobManager jobs) =>
    Results.Ok(new { jobId = jobs.Start("overfitting", async (p, ct) => await s.OverfittingAsync(request, p, ct)) }));

quant.MapGet("/jobs", (QuantJobManager jobs) => Results.Ok(jobs.List()));
quant.MapGet("/jobs/{id}", (string id, QuantJobManager jobs) =>
    jobs.Get(id) is { } state ? Results.Ok(state) : Results.NotFound(new { error = $"Job '{id}' unbekannt." }));
quant.MapPost("/jobs/{id}/cancel", (string id, QuantJobManager jobs) =>
    Results.Ok(new { cancelled = jobs.Cancel(id) }));

// Versuchsregister (vollständig, inklusive negativer und verworfener Versuche).
quant.MapGet("/campaigns", async (QuantApiService s, CancellationToken ct) => Results.Ok(await s.ListCampaignsAsync(ct)));
quant.MapGet("/trials", async (string? campaignId, QuantApiService s, CancellationToken ct) =>
    Results.Ok(await s.ListTrialsAsync(campaignId, ct)));
quant.MapGet("/trials/{id}", async (string id, QuantApiService s, CancellationToken ct) =>
    await s.GetTrialAsync(id, ct) is { } t ? Results.Ok(t) : Results.NotFound(new { error = $"Versuch '{id}' unbekannt." }));

// Paper-Research-Einträge (Quelle, Hypothese, Regeln, dokumentierte Abweichungen).
quant.MapGet("/papers", async (QuantApiService s, CancellationToken ct) => Results.Ok(await s.ListPapersAsync(ct)));
quant.MapPost("/papers", async (PaperResearchEntry entry, QuantApiService s, CancellationToken ct) =>
{
    try { return Results.Ok(await s.SavePaperAsync(entry, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.Run();
