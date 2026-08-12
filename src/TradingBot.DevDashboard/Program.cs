using TradingBot.DevDashboard.Components;
using TradingBot.DevDashboard.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Read-only Dashboard-Services (keine Order-/Broker-/Execution-Referenz).
var repoRoot = RepoLocator.FindRoot(builder.Environment.ContentRootPath);
builder.Services.AddSingleton<ProjectStatusService>();
builder.Services.AddSingleton(new GitInfoService(repoRoot));
builder.Services.AddSingleton(new ConfigOverviewService(repoRoot));
builder.Services.AddSingleton(new PaperDemoService(repoRoot)); // PAPER SIMULATION ONLY
builder.Services.AddSingleton<ResearchDemoService>();          // RESEARCH / SIMULATION ONLY (deterministische Demo)
builder.Services.AddSingleton<ReplayDemoService>();            // RESEARCH / SIMULATION ONLY (deterministisches Replay)
builder.Services.AddSingleton<SierraBacktestReplayService>(); // LOCAL HISTORICAL REPLAY / SIMULATION ONLY (lokale Datei, read-only)

var app = builder.Build();

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

app.Run();
