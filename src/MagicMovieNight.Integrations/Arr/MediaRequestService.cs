using MagicMovieNight.Core.Models;
using MagicMovieNight.Data;
using Microsoft.EntityFrameworkCore;

namespace MagicMovieNight.Integrations.Arr;

/// <summary>
/// Routes a request to whichever server handles that kind of media, and records the
/// outcome so the UI can say "requested" rather than offering the button again.
/// </summary>
public class MediaRequestService(
    MovieNightDbContext db,
    SonarrClient sonarr,
    RadarrClient radarr)
{
    /// <summary>True when the right server for this kind of media is set up.</summary>
    public bool CanRequest(MediaItem item) =>
        item.Kind == MediaKind.Movie ? radarr.IsConfigured : sonarr.IsConfigured;

    public string ServerNameFor(MediaItem item) =>
        item.Kind == MediaKind.Movie ? radarr.Name : sonarr.Name;

    public async Task<ArrResult> RequestAsync(int mediaItemId, CancellationToken ct = default)
    {
        var item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == mediaItemId, ct)
            ?? throw new InvalidOperationException($"Media item {mediaItemId} not found.");

        if (item.InLibrary)
        {
            return ArrResult.Ok("Already in the library");
        }

        var result = item.Kind == MediaKind.Movie
            ? await radarr.RequestAsync(item, ct)
            : await sonarr.RequestAsync(item, ct);

        // Only a success is recorded. A failed request should leave the button available
        // so it can be retried once the cause is fixed.
        if (result.Success)
        {
            item.RequestedAt = DateTimeOffset.UtcNow;
            item.RequestNote = result.Message;
            await db.SaveChangesAsync(ct);
        }

        return result;
    }
}
