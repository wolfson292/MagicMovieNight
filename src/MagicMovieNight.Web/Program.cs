using MagicMovieNight.Data;
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

// Runs history syncs on a timer so the picks are never stale by a week.
builder.Services.AddHostedService<SyncBackgroundService>();

// Health endpoint for the container — Portainer and swag both want one.
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
