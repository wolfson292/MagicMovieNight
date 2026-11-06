using MagicMovieNight.Core.Abstractions;
using MagicMovieNight.Core.Models;
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

// Served two ways: directly on the LAN at the root, and through SWAG under a subfolder
// (nas.denlair.com/magicmovienight/). The proxy strips the prefix and names it in
// X-Forwarded-Prefix; setting PathBase from it makes the <base href>, the Blazor
// WebSocket, static assets and redirects all resolve under the subfolder. Without the
// header nothing changes, so the LAN address keeps working. First, so every later
// middleware — error pages included — sees the right base.
app.Use((context, next) =>
{
    var prefix = context.Request.Headers["X-Forwarded-Prefix"].ToString().TrimEnd('/');

    if (prefix.StartsWith('/') && !prefix.StartsWith("//") && Uri.IsWellFormedUriString(prefix, UriKind.Relative))
    {
        context.Request.PathBase = new PathString(prefix);
    }

    return next(context);
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthChecks("/health");

// The last set of picks, as JSON.
//
// FOR OTHER THINGS IN THE HOUSE TO READ. The family app shows tonight's
// suggestion on its admin screen, and the alternative was for it to reach into
// this app's Postgres and depend on a schema it does not own. A small endpoint
// here is the narrower coupling: this app keeps the right to change how it
// stores a recommendation, and owes only this shape.
//
// UNAUTHENTICATED, like the rest of this app, which is LAN-only and always has
// been. The exposure does not change: whatever reaches this from outside the
// house reaches it through something that authenticates in front.
app.MapGet("/api/tonight", async (MovieNightDbContext db, int? take, CancellationToken token) =>
{
    RecommendationRun? run = await db.RecommendationRuns
        .Where(candidate => candidate.Error == null)
        .OrderByDescending(candidate => candidate.CreatedAt)
        .FirstOrDefaultAsync(token);

    if (run is null)
    {
        return Results.Ok(new { run = (object?)null, picks = Array.Empty<object>() });
    }

    var picks = await db.Recommendations
        .Where(pick => pick.RunId == run.Id)
        .OrderBy(pick => pick.Rank)
        .Take(Math.Clamp(take ?? 5, 1, 20))
        .Select(pick => new
        {
            pick.Rank,
            pick.Pitch,
            pick.BasedOn,
            pick.WhereToWatch,
            pick.Confidence,
            verdict = pick.Verdict.ToString(),
            title = pick.MediaItem!.Title,
            pick.MediaItem.Year,
            kind = pick.MediaItem.Kind.ToString(),
            pick.MediaItem.PosterUrl,
            pick.MediaItem.Overview,
            pick.MediaItem.InLibrary,
            pick.MediaItem.TmdbId,
            pick.MediaItem.TvdbId,
            pick.MediaItem.RuntimeMinutes,
        })
        .ToListAsync(token);

    return Results.Ok(new
    {
        run = new { run.Id, run.CreatedAt, run.ModelId },
        picks,
    });
});

// Ask for a suggestion, in words.
//
// The same engine the Home page drives, reached over HTTP so the family app can
// ask from a phone. Deliberately NOT a second recommendation path: everything
// that makes a good suggestion -- the taste profile, the exclusions, the
// candidate pool -- lives in that engine, and a simpler one here would quietly
// give worse answers.
//
// SLOW AND NOT FREE. It gathers candidates and asks Claude, which takes tens of
// seconds and costs tokens, so it is a POST rather than something a page can
// trigger by being opened.
app.MapPost("/api/suggest", async (
    SuggestRequest ask,
    IRecommendationEngine engine,
    MovieNightDbContext db,
    CancellationToken token) =>
{
    MediaKind? kind = ask.Kind?.ToLowerInvariant() switch
    {
        "movie" or "film" => MediaKind.Movie,
        "show" or "tv" or "series" => MediaKind.Show,
        _ => null,
    };

    RecommendationRun run = await engine.RecommendAsync(
        new RecommendationRequest
        {
            Prompt = string.IsNullOrWhiteSpace(ask.Prompt) ? null : ask.Prompt.Trim(),
            Count = Math.Clamp(ask.Count ?? 5, 1, 10),
            Kind = kind,
            LibraryOnly = ask.LibraryOnly ?? false,
            MaxRuntimeMinutes = ask.MaxRuntimeMinutes,
        },
        token);

    if (run.Error is not null)
    {
        // Answered rather than thrown: the caller is a phone, and "the model
        // refused" is something to read, not a 500 to guess at.
        return Results.Ok(new { error = run.Error, picks = Array.Empty<object>() });
    }

    var picks = await db.Recommendations
        .Where(pick => pick.RunId == run.Id)
        .OrderBy(pick => pick.Rank)
        .Select(pick => new
        {
            pick.Rank,
            pick.Pitch,
            pick.BasedOn,
            pick.WhereToWatch,
            pick.Confidence,
            verdict = pick.Verdict.ToString(),
            title = pick.MediaItem!.Title,
            pick.MediaItem.Year,
            kind = pick.MediaItem.Kind.ToString(),
            pick.MediaItem.PosterUrl,
            pick.MediaItem.Overview,
            pick.MediaItem.InLibrary,
            pick.MediaItem.TmdbId,
            pick.MediaItem.TvdbId,
            pick.MediaItem.RuntimeMinutes,
        })
        .ToListAsync(token);

    return Results.Ok(new
    {
        run = new { run.Id, run.CreatedAt, run.ModelId },
        picks,
    });
});

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

/// <summary>A suggestion asked for in words, as the family app sends it.</summary>
internal sealed record SuggestRequest(
    string? Prompt, string? Kind, int? Count, bool? LibraryOnly, int? MaxRuntimeMinutes);
