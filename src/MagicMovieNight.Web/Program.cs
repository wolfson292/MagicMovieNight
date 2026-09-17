using MagicMovieNight.Data;
using Microsoft.AspNetCore.DataProtection;
using MagicMovieNight.Integrations;
using MagicMovieNight.Web.Components;
using MagicMovieNight.Web.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContext<MovieNightDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("MovieNight")
        ?? throw new InvalidOperationException(
            "No 'MovieNight' connection string. Set ConnectionStrings__MovieNight.")));

builder.Services.AddMovieNightIntegrations(builder.Configuration);

// Without this, data-protection keys live in the container filesystem and are lost on
// every redeploy — which silently invalidates antiforgery tokens and every open Blazor
// circuit. The config volume already persists for the Trakt token, so keys go there too.
var keyPath = builder.Configuration["DataProtection:KeyPath"] ?? "/config/keys";

if (TryPrepareKeyDirectory(keyPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keyPath))
        .SetApplicationName("MagicMovieNight");
}

// Background work reaches out to Tautulli, Trakt and TMDB, which a test run must not
// do — the smoke tests only care that pages render against a real schema.
if (!builder.Environment.IsEnvironment("Testing"))
{
    // Runs history syncs on a timer so the picks are never stale by a week.
    builder.Services.AddHostedService<SyncBackgroundService>();

    // Repairs catalog entries left bare when TMDB was unavailable at ingest time.
    builder.Services.AddHostedService<CatalogBackfillService>();
}

// Health endpoint for the container — orchestrators and reverse proxies both want one.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<MovieNightDbContext>();

var app = builder.Build();

// The schema is applied on boot rather than by a separate migration step. This is a
// single-instance household app; there is no rolling deploy to coordinate with.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MovieNightDbContext>();
    await db.Database.MigrateAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthChecks("/health");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Local development has no /config volume, so fall back to the default in-memory
// behaviour rather than failing to start over a directory that cannot be created.
static bool TryPrepareKeyDirectory(string path)
{
    try
    {
        Directory.CreateDirectory(path);
        return true;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return false;
    }
}

/// <summary>
/// Named so the test host can find this assembly's entry point. Top-level statements
/// generate an internal Program class, which WebApplicationFactory cannot reach.
/// </summary>
public partial class Program;
