using TradingBot.DevDashboard.Components;
using TradingBot.DevDashboard.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Read-only Dashboard-Services (keine Order-/Broker-/Execution-Referenz).
var repoRoot = RepoLocator.FindRoot(builder.Environment.ContentRootPath);
// Lokale Rithmic-Einstellungen (AppName, weitere Gateways) – gitignored, keine Passwörter hier ablegen.
if (repoRoot is not null)
    builder.Configuration.AddJsonFile(Path.Combine(repoRoot, "config", "marketdata", "rithmic.local.json"), optional: true, reloadOnChange: false);
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

// Rithmic API endpoints (R|Protocol, NUR Marktdaten: Ticker + History Plant, kein Order-/PnL-Plant).
// Standardmäßig DEAKTIVIERT: Aktivierung nur explizit über "Rithmic:Enabled" = true (z. B. Umgebungsvariable
// Rithmic__Enabled=true). Bei deaktiviertem Modus wird der Service gar nicht erreicht -> kein Netzwerkzugriff.
// Auch aktiviert geht das Netzwerk erst bei explizitem POST /connect raus.
bool rithmicEnabled = builder.Configuration.GetValue("Rithmic:Enabled", false);
var rithmic = app.MapGroup("/api/rithmic").RequireCors(ReactCors);

if (rithmicEnabled)
{
    rithmic.MapGet("/options", (RithmicDashboardService service) =>
        Results.Ok(new { enabled = true, options = service.GetOptions() }));

    rithmic.MapPost("/connect", async (RithmicConnectRequest request, RithmicDashboardService service, CancellationToken ct) =>
    {
        var result = await service.ConnectAsync(request, ct);
        return result.Success ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status502BadGateway);
    });

    rithmic.MapPost("/disconnect", async (RithmicDashboardService service) => Results.Ok(await service.DisconnectAsync()));

    rithmic.MapGet("/status", (RithmicDashboardService service) => Results.Ok(service.GetStatus()));

    rithmic.MapPost("/subscribe", async (RithmicSubscribeRequest request, RithmicDashboardService service, CancellationToken ct) =>
        await RithmicCall(async () => { await service.SubscribeAsync(request.Symbol, request.Exchange, ct); return new { success = true }; }));

    // Conformance-Test (Order Plant von "Rithmic Test", nur Login/Heartbeat – keine Orders).
    rithmic.MapPost("/conformance/start", async (RithmicConformanceRequest request, RithmicDashboardService service, CancellationToken ct) =>
    {
        var result = await service.StartConformanceAsync(request, ct);
        return result.Success ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status502BadGateway);
    });
    rithmic.MapPost("/conformance/stop", async (RithmicDashboardService service) => Results.Ok(await service.StopConformanceAsync()));
    rithmic.MapGet("/conformance/status", (RithmicDashboardService service) => Results.Ok(service.GetConformanceStatus()));

    // Live-Tape: inkrementell per since=<lastSeq> abholen (Ticks nur für abonnierte Instrumente).
    rithmic.MapGet("/ticks", (string symbol, string? exchange, long? since, int? limit, RithmicDashboardService service) =>
        RithmicCall(() => Task.FromResult(service.GetTicks(symbol, exchange ?? "CME", since ?? 0, limit ?? 500))));

    rithmic.MapGet("/quotes", (RithmicDashboardService service) => Results.Ok(service.GetQuotes()));

    rithmic.MapGet("/frontmonth", async (string root, string? exchange, RithmicDashboardService service, CancellationToken ct) =>
        await RithmicCall(async () => new { root, symbol = await service.GetFrontMonthAsync(root, exchange ?? "CME", ct) }));

    rithmic.MapGet("/bars", async (string symbol, string? exchange, int? minutes, int? hours, RithmicDashboardService service, CancellationToken ct) =>
    {
        var to = DateTimeOffset.UtcNow;
        var from = to.AddHours(-Math.Clamp(hours ?? 24, 1, 24 * 30));
        return await RithmicCall(() => service.GetMinuteBarsAsync(symbol, exchange ?? "CME", from, to, Math.Clamp(minutes ?? 1, 1, 1440), ct));
    });
}
else
{
    const string disabledMsg = "Rithmic ist deaktiviert (Rithmic:Enabled=false) – keine externen Broker-Calls.";
    rithmic.MapGet("/options", () => Results.Ok(new { enabled = false, message = disabledMsg }));
    // /connect erreicht den Service NICHT -> kein externer Call möglich.
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

app.Run();

// Einheitliche Fehlerabbildung für Rithmic-Marktdaten-Calls.
static async Task<IResult> RithmicCall<T>(Func<Task<T>> call)
{
    try
    {
        return Results.Ok(await call());
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { success = false, message = ex.Message }, statusCode: StatusCodes.Status409Conflict);
    }
    catch (TradingBot.Infrastructure.MarketData.Rithmic.Protocol.RithmicProtocolException ex)
    {
        return Results.Json(new { success = false, message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
}
