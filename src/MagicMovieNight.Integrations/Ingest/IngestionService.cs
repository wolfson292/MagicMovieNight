using MagicMovieNight.Core.Abstractions;
using MagicMovieNight.Core.Models;
using MagicMovieNight.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MagicMovieNight.Integrations.Ingest;

/// <summary>
/// Pulls every configured source into the database, and handles one-shot CSV imports.
///
/// The whole thing is built to be safely re-runnable. Sources get re-read with some
/// overlap on every cycle (clocks drift, Tautulli backfills), so ingestion leans on
/// the unique (Source, SourceKey) index rather than trying to be clever about
/// watermarks.
/// </summary>
public class IngestionService(
    MovieNightDbContext db,
    IEnumerable<IHistorySource> sources,
    ICatalogService catalog,
    IOptions<HouseholdOptions> household,
    ILogger<IngestionService> logger)
{
    /// <summary>
    /// Sources are re-read from slightly before the watermark. Cheap insurance against
    /// events that land out of order.
    /// </summary>
    private static readonly TimeSpan ResyncOverlap = TimeSpan.FromDays(2);

    private readonly HouseholdOptions _household = household.Value;

    public async Task<IngestResult> SyncAllAsync(CancellationToken ct = default)
    {
        var total = new IngestResult();

        foreach (var source in sources)
        {
            if (!source.IsConfigured)
            {
                logger.LogDebug("Skipping {Source}: not configured.", source.Source);
                continue;
            }

            var result = await SyncAsync(source, ct);
            total = total with
            {
                Imported = total.Imported + result.Imported,
                Skipped = total.Skipped + result.Skipped,
                Unresolved = total.Unresolved + result.Unresolved,
            };
        }

        return total;
    }

    public async Task<IngestResult> SyncAsync(IHistorySource source, CancellationToken ct = default)
    {
        var state = await db.IngestStates.FirstOrDefaultAsync(s => s.Source == source.Source, ct);
        if (state is null)
        {
            state = new IngestState { Source = source.Source };
            db.IngestStates.Add(state);
        }

        var since = state.LastSyncedAt is null
            ? DateTimeOffset.UtcNow.AddDays(-_household.InitialBackfillDays)
            : state.LastSyncedAt.Value - ResyncOverlap;

        logger.LogInformation("Syncing {Source} since {Since:yyyy-MM-dd}.", source.Source, since);

        var result = new IngestResult();
        var highWater = state.LastSyncedAt;

        try
        {
            await foreach (var raw in source.PullAsync(since, ct))
            {
                var outcome = await IngestOneAsync(raw, source.Source, profileOverride: null, ct);
                result = result.Add(outcome);

                if (highWater is null || raw.WatchedAt > highWater)
                {
                    highWater = raw.WatchedAt;
                }
            }

            state.LastSyncedAt = highWater;
            state.LastError = null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sync of {Source} failed.", source.Source);
            state.LastError = ex.Message;
        }

        state.LastRunAt = DateTimeOffset.UtcNow;
        state.LastRunEventCount = result.Imported;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "{Source}: {Imported} new, {Skipped} already known, {Unresolved} unresolved.",
            source.Source, result.Imported, result.Skipped, result.Unresolved);

        return result;
    }

    /// <summary>
    /// Imports an uploaded export. Unlike a pull, the caller says which profile the file
    /// belongs to, because a per-profile Netflix download carries no profile column.
    /// A full account export does, and those rows route themselves.
    /// </summary>
    public async Task<IngestResult> ImportAsync(
        IHistoryImporter importer,
        Stream file,
        int profileId,
        CancellationToken ct = default)
    {
        var result = new IngestResult();

        await foreach (var raw in importer.ParseAsync(file, ct))
        {
            var outcome = await IngestOneAsync(raw, importer.Source, profileId, ct);
            result = result.Add(outcome);
        }

        logger.LogInformation(
            "Imported {File}: {Imported} new, {Skipped} duplicates, {Unresolved} unresolved.",
            importer.DisplayName, result.Imported, result.Skipped, result.Unresolved);

        return result;
    }

    private async Task<IngestOutcome> IngestOneAsync(
        RawWatchEvent raw,
        WatchSource source,
        int? profileOverride,
        CancellationToken ct)
    {
        var exists = await db.WatchEvents
            .AnyAsync(e => e.Source == source && e.SourceKey == raw.SourceKey, ct);

        if (exists)
        {
            return IngestOutcome.Skipped;
        }

        var item = await catalog.ResolveAsync(raw, ct);
        if (item is null)
        {
            return IngestOutcome.Unresolved;
        }

        var profileId = profileOverride ?? await ResolveProfileAsync(raw.ExternalProfileId, source, ct);

        db.WatchEvents.Add(new WatchEvent
        {
            ProfileId = profileId,
            MediaItemId = item.Id,
            Source = source,
            SourceKey = raw.SourceKey,
            WatchedAt = raw.WatchedAt,
            PercentComplete = raw.PercentComplete,
            Device = raw.Device,
            SeasonNumber = raw.SeasonNumber,
            EpisodeNumber = raw.EpisodeNumber,
            Fidelity = raw.Fidelity,
        });

        await db.SaveChangesAsync(ct);

        return IngestOutcome.Imported;
    }

    /// <summary>
    /// Maps a source's own user id onto a profile, creating one on first sight.
    ///
    /// Deliberately creates a profile and not a person: a source only ever tells us
    /// which account was used, and deciding who that represents is a judgement the
    /// household makes in the UI. A new profile simply arrives unassigned.
    /// </summary>
    private async Task<int> ResolveProfileAsync(
        string? externalId,
        WatchSource source,
        CancellationToken ct)
    {
        externalId = string.IsNullOrWhiteSpace(externalId) ? "unknown" : externalId.Trim();

        var identity = await db.ProfileIdentities
            .FirstOrDefaultAsync(i => i.Source == source && i.ExternalId == externalId, ct);

        if (identity is not null)
        {
            return identity.ProfileId;
        }

        // The same display name on another source is almost always the same account,
        // so match on that rather than creating a near-duplicate profile.
        var profile = await db.Profiles
            .FirstOrDefaultAsync(p => p.DisplayName.ToLower() == externalId.ToLower(), ct);

        if (profile is null)
        {
            profile = new Profile { DisplayName = externalId };
            db.Profiles.Add(profile);
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Discovered new profile '{Name}' from {Source}; it is unassigned until "
                + "someone says who it represents.", externalId, source);
        }

        db.ProfileIdentities.Add(new ProfileIdentity
        {
            ProfileId = profile.Id,
            Source = source,
            ExternalId = externalId,
        });

        await db.SaveChangesAsync(ct);

        return profile.Id;
    }
}

public enum IngestOutcome
{
    Imported,
    Skipped,
    Unresolved,
}

public record IngestResult
{
    public int Imported { get; init; }

    public int Skipped { get; init; }

    public int Unresolved { get; init; }

    public int Total => Imported + Skipped + Unresolved;

    public IngestResult Add(IngestOutcome outcome) => outcome switch
    {
        IngestOutcome.Imported => this with { Imported = Imported + 1 },
        IngestOutcome.Skipped => this with { Skipped = Skipped + 1 },
        _ => this with { Unresolved = Unresolved + 1 },
    };
}
