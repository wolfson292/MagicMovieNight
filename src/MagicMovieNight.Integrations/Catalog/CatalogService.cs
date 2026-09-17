using MagicMovieNight.Core.Abstractions;
using MagicMovieNight.Core.Models;
using MagicMovieNight.Data;
using MagicMovieNight.Integrations.Tmdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MagicMovieNight.Integrations.Catalog;

/// <summary>
/// Collapses every source's idea of a title into one catalog entry.
///
/// This is where the dedup problem actually lives. Trakt hands over clean TMDB ids;
/// a Netflix CSV hands over the string "Bodyguard: Season 1: Episode 1" and a date.
/// Resolution walks from strongest identifier to weakest and only falls back to a
/// TMDB title search when there is no id to match on.
///
/// Episodes deliberately resolve to their parent show: nobody has an affinity for
/// one episode, and keeping 4,000 episode rows out of the catalog keeps the taste
/// profile legible.
/// </summary>
public class CatalogService(
    MovieNightDbContext db,
    TmdbClient tmdb,
    ILogger<CatalogService> logger) : ICatalogService
{
    /// <summary>Metadata older than this gets refreshed — mainly so availability stays current.</summary>
    private static readonly TimeSpan EnrichmentTtl = TimeSpan.FromDays(30);

    public async Task<MediaItem?> ResolveAsync(RawWatchEvent raw, CancellationToken ct = default)
    {
        var kind = raw.Kind == MediaKind.Episode ? MediaKind.Show : raw.Kind;

        var existing = await FindExistingAsync(raw, kind, ct);
        if (existing is not null)
        {
            if (DateTimeOffset.UtcNow - existing.LastEnrichedAt > EnrichmentTtl)
            {
                await EnrichAsync(existing, ct);
            }

            return existing;
        }

        var item = new MediaItem
        {
            Title = raw.Title,
            Year = raw.Year,
            Kind = kind,
            TmdbId = raw.TmdbId,
            TraktId = raw.TraktId,
            ImdbId = raw.ImdbId,
        };

        // A CSV row arrives with no ids at all, so ask TMDB who this is before saving —
        // otherwise the same show enters the catalog once per spelling variant.
        if (item.TmdbId is null && tmdb.IsConfigured)
        {
            var match = await tmdb.SearchAsync(raw.Title, raw.Year, kind, ct);
            if (match is not null)
            {
                item.TmdbId = match.Id;
                item.Title = match.DisplayName ?? item.Title;
                item.Year ??= match.Year;

                // The search may have resolved to something already in the catalog
                // under a different spelling.
                var byTmdb = await db.MediaItems
                    .FirstOrDefaultAsync(m => m.TmdbId == item.TmdbId && m.Kind == kind, ct);

                if (byTmdb is not null)
                {
                    return byTmdb;
                }
            }
            else
            {
                logger.LogDebug(
                    "No TMDB match for '{Title}' ({Year}); keeping it as an unresolved entry.",
                    raw.Title, raw.Year);
            }
        }

        db.MediaItems.Add(item);
        await db.SaveChangesAsync(ct);

        await EnrichAsync(item, ct);

        return item;
    }

    public async Task EnrichAsync(MediaItem item, CancellationToken ct = default)
    {
        if (!tmdb.IsConfigured || item.TmdbId is null)
        {
            return;
        }

        var details = await tmdb.GetDetailsAsync(item.TmdbId.Value, item.Kind, ct);
        if (details is null)
        {
            return;
        }

        item.Title = details.Title ?? details.Name ?? item.Title;
        item.Year ??= details.Year;
        item.Overview = details.Overview;
        item.RuntimeMinutes = details.RuntimeMinutes;
        item.CommunityRating = details.VoteAverage;
        item.PosterUrl = tmdb.PosterUrl(details.PosterPath);
        item.ImdbId ??= details.ExternalIds?.ImdbId;
        item.TvdbId ??= details.ExternalIds?.TvdbId;
        item.Genres = details.Genres?.Select(g => g.Name!).Where(n => n is not null).ToList() ?? [];
        item.AvailableOn = tmdb.ExtractProviders(details).ToList();
        item.People = ExtractPeople(details.Credits);
        item.LastEnrichedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    private async Task<MediaItem?> FindExistingAsync(RawWatchEvent raw, MediaKind kind, CancellationToken ct)
    {
        if (raw.TmdbId is not null)
        {
            var byTmdb = await db.MediaItems
                .FirstOrDefaultAsync(m => m.TmdbId == raw.TmdbId && m.Kind == kind, ct);
            if (byTmdb is not null)
            {
                return byTmdb;
            }
        }

        if (raw.TraktId is not null)
        {
            var byTrakt = await db.MediaItems
                .FirstOrDefaultAsync(m => m.TraktId == raw.TraktId && m.Kind == kind, ct);
            if (byTrakt is not null)
            {
                return byTrakt;
            }
        }

        if (!string.IsNullOrWhiteSpace(raw.ImdbId))
        {
            var byImdb = await db.MediaItems
                .FirstOrDefaultAsync(m => m.ImdbId == raw.ImdbId, ct);
            if (byImdb is not null)
            {
                return byImdb;
            }
        }

        // Last resort for id-less CSV rows. Year is often absent in exports, so a
        // title match with no year still counts.
        return await db.MediaItems.FirstOrDefaultAsync(
            m => m.Kind == kind
                && m.Title.ToLower() == raw.Title.ToLower()
                && (raw.Year == null || m.Year == null || m.Year == raw.Year),
            ct);
    }

    /// <summary>
    /// Directors and showrunners first — they predict taste far better than cast — then
    /// the top-billed actors. Capped so one blockbuster's 90-person credit list does not
    /// drown the profile.
    /// </summary>
    private static List<string> ExtractPeople(TmdbCredits? credits)
    {
        if (credits is null)
        {
            return [];
        }

        var people = new List<string>();

        var directors = credits.Crew?
            .Where(c => c.Job is "Director" or "Creator" or "Executive Producer" or "Writer")
            .Select(c => c.Name!)
            .Where(n => n is not null)
            .Take(4) ?? [];

        people.AddRange(directors);

        var cast = credits.Cast?
            .OrderBy(c => c.Order)
            .Select(c => c.Name!)
            .Where(n => n is not null)
            .Take(6) ?? [];

        people.AddRange(cast);

        return people.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
