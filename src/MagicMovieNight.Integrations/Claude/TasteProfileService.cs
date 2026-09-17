using MagicMovieNight.Core.Models;
using MagicMovieNight.Core.Taste;
using MagicMovieNight.Data;
using Microsoft.EntityFrameworkCore;

namespace MagicMovieNight.Integrations.Claude;

/// <summary>
/// Loads history out of the database and hands it to the profile builder.
///
/// Passing no viewer ids means the household blend: every viewer flagged for
/// inclusion, pooled. That is deliberately not the same as averaging separate
/// profiles — pooling lets a title both people watched outweigh two titles only
/// one of them watched, which is exactly what "what should *we* watch" means.
/// </summary>
public class TasteProfileService(MovieNightDbContext db)
{
    /// <summary>A series watched this recently is assumed to still be in progress.</summary>
    private static readonly TimeSpan InProgressWindow = TimeSpan.FromDays(60);

    public async Task<TasteProfile> BuildAsync(
        IReadOnlyList<int> viewerIds,
        CancellationToken ct = default)
    {
        var resolvedIds = viewerIds.Count > 0
            ? viewerIds.ToList()
            : await db.Viewers
                .Where(v => v.IncludeInHousehold)
                .Select(v => v.Id)
                .ToListAsync(ct);

        var subject = await DescribeSubjectAsync(viewerIds, resolvedIds, ct);

        var events = await db.WatchEvents
            .Include(e => e.MediaItem)
            .Where(e => resolvedIds.Contains(e.ViewerId))
            .OrderByDescending(e => e.WatchedAt)
            .ToListAsync(ct);

        var ratings = await db.Ratings
            .Include(r => r.MediaItem)
            .Where(r => resolvedIds.Contains(r.ViewerId))
            .OrderByDescending(r => r.RatedAt)
            .ToListAsync(ct);

        var inProgress = FindInProgress(events);

        return TasteProfileBuilder.Build(subject, resolvedIds, events, ratings, inProgress);
    }

    private async Task<string> DescribeSubjectAsync(
        IReadOnlyList<int> requested,
        IReadOnlyList<int> resolved,
        CancellationToken ct)
    {
        var names = await db.Viewers
            .Where(v => resolved.Contains(v.Id))
            .Select(v => v.DisplayName)
            .ToListAsync(ct);

        if (names.Count == 0)
        {
            return "the household (no viewers configured yet)";
        }

        if (requested.Count == 0)
        {
            return $"the whole household — {string.Join(", ", names)}";
        }

        return names.Count == 1
            ? names[0]
            : $"{string.Join(" and ", names)}, watching together";
    }

    /// <summary>
    /// We do not know a show's episode count, so "in progress" means episodes watched
    /// recently and not obviously finished. Imperfect, but it surfaces the thing the
    /// household is most likely to want next — the show they are already mid-way through.
    /// </summary>
    private static List<string> FindInProgress(List<WatchEvent> events)
    {
        var cutoff = DateTimeOffset.UtcNow - InProgressWindow;

        return events
            .Where(e => e.MediaItem is not null)
            .Where(e => e.MediaItem!.Kind is MediaKind.Show or MediaKind.Episode)
            .Where(e => e.WatchedAt >= cutoff)
            .GroupBy(e => e.MediaItem!.DisplayTitle)
            .OrderByDescending(g => g.Max(e => e.WatchedAt))
            .Select(g => g.Key)
            .Take(10)
            .ToList();
    }
}
