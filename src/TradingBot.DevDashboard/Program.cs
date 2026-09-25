using TradingBot.DevDashboard.Components;
using TradingBot.DevDashboard.Services;

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
builder.Services.AddSingleton(new BacktestApiService(repoRoot)); // OHLC-BACKTEST (Simulation-only, keine Execution/Broker)

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
bt.MapPost("/run", async (BacktestRunRequest request, BacktestApiService s, CancellationToken ct) =>
{
    var result = await s.RunAsync(request, ct);
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
});

app.Run();
