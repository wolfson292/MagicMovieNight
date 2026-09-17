using MagicMovieNight.Core.Models;

namespace MagicMovieNight.Core.Abstractions;

/// <summary>
/// A pullable history source. Implemented by Tautulli and Trakt; CSV importers use
/// <see cref="IHistoryImporter"/> instead because they are push-shaped.
/// </summary>
public interface IHistorySource
{
    WatchSource Source { get; }

    /// <summary>True when the source has credentials configured and is worth polling.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Pull events recorded after <paramref name="since"/>. Implementations return
    /// events with a stable <see cref="WatchEvent.SourceKey"/> so re-pulling is safe.
    /// </summary>
    IAsyncEnumerable<RawWatchEvent> PullAsync(DateTimeOffset since, CancellationToken ct = default);
}

/// <summary>A one-shot import of an uploaded export file.</summary>
public interface IHistoryImporter
{
    WatchSource Source { get; }

    string DisplayName { get; }

    /// <summary>Parses the export and yields events. Does not touch the database.</summary>
    IAsyncEnumerable<RawWatchEvent> ParseAsync(Stream csv, CancellationToken ct = default);
}

/// <summary>
/// A watch as the source reports it, before title resolution. Sources know a title
/// string and maybe some ids; turning that into a <see cref="MediaItem"/> is the
/// catalog's job.
/// </summary>
public record RawWatchEvent
{
    public required string Title { get; init; }

    public int? Year { get; init; }

    public MediaKind Kind { get; init; }

    public required string SourceKey { get; init; }

    public required DateTimeOffset WatchedAt { get; init; }

    /// <summary>Source-specific profile id, resolved against <see cref="ProfileIdentity"/>.</summary>
    public string? ExternalProfileId { get; init; }

    public int? TmdbId { get; init; }

    public int? TraktId { get; init; }

    public string? ImdbId { get; init; }

    public int? PercentComplete { get; init; }

    public string? Device { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public SourceFidelity Fidelity { get; init; }
}

/// <summary>
/// A one-shot import of an uploaded ratings export. Separate from
/// <see cref="IHistoryImporter"/> because a rating is an opinion, not a viewing —
/// Netflix ships the two as different files and they mean different things.
/// </summary>
public interface IRatingImporter
{
    WatchSource Source { get; }

    string DisplayName { get; }

    IAsyncEnumerable<RawRating> ParseAsync(Stream csv, CancellationToken ct = default);
}

/// <summary>A rating as the source reports it, before title resolution.</summary>
public record RawRating
{
    public required string Title { get; init; }

    public int? Year { get; init; }

    public MediaKind Kind { get; init; }

    public required string SourceKey { get; init; }

    public required RatingValue Value { get; init; }

    public int? Stars { get; init; }

    public required DateTimeOffset RatedAt { get; init; }

    public string? ExternalProfileId { get; init; }

    public int? TmdbId { get; init; }

    public int? TraktId { get; init; }

    public string? ImdbId { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    /// <summary>The raw event this came from, reused for catalog resolution.</summary>
    public RawWatchEvent ToResolvable() => new()
    {
        Title = Title,
        Year = Year,
        Kind = Kind,
        SourceKey = SourceKey,
        WatchedAt = RatedAt,
        TmdbId = TmdbId,
        TraktId = TraktId,
        ImdbId = ImdbId,
        Fidelity = SourceFidelity.TitleAndDate,
    };
}

/// <summary>Resolves titles to catalog entries and fills in metadata.</summary>
public interface ICatalogService
{
    /// <summary>
    /// Finds or creates the item matching this raw event, resolving by external id
    /// first and falling back to a title+year search.
    /// </summary>
    Task<MediaItem?> ResolveAsync(RawWatchEvent raw, CancellationToken ct = default);

    /// <summary>Fills in genres, people, runtime, and streaming availability.</summary>
    Task EnrichAsync(MediaItem item, CancellationToken ct = default);

    /// <summary>
    /// Enriches catalog entries that are missing metadata, returning how many were
    /// attempted. Entries go bare whenever TMDB was unreachable or misconfigured at the
    /// moment they were first seen, and nothing else will revisit them — a title watched
    /// once and never again would stay bare forever.
    /// </summary>
    Task<int> BackfillAsync(int batchSize, CancellationToken ct = default);
}

/// <summary>Produces the pool of things the household could plausibly watch tonight.</summary>
public interface ICandidateSource
{
    string Name { get; }

    Task<IReadOnlyList<Candidate>> GetCandidatesAsync(
        TasteProfile profile,
        CancellationToken ct = default);
}

public record Candidate
{
    public required MediaItem Item { get; init; }

    /// <summary>Which source proposed it, for provenance in the UI.</summary>
    public required string Source { get; init; }

    public RecommendationOrigin Origin { get; init; }

    /// <summary>Cheap pre-score used to trim the pool before it reaches the model.</summary>
    public double PreScore { get; init; }

    /// <summary>Why this source proposed it, e.g. "Trakt personal recs", "next up in Severance".</summary>
    public string? Reason { get; init; }
}

/// <summary>The thing that actually picks tonight's slate.</summary>
public interface IRecommendationEngine
{
    Task<RecommendationRun> RecommendAsync(
        RecommendationRequest request,
        CancellationToken ct = default);
}

public record RecommendationRequest
{
    /// <summary>Empty means everyone flagged as part of the household.</summary>
    public IReadOnlyList<int> PersonIds { get; init; } = [];

    /// <summary>Optional mood or constraint typed by the household.</summary>
    public string? Prompt { get; init; }

    public int Count { get; init; } = 5;

    /// <summary>Cap on runtime for the night, when someone is watching the clock.</summary>
    public int? MaxRuntimeMinutes { get; init; }

    /// <summary>Restrict to titles already in the Plex library.</summary>
    public bool LibraryOnly { get; init; }

    /// <summary>
    /// Allow titles the household has already watched, or marked as already seen.
    /// Off by default — the usual request is for something new — but a rewatch night
    /// is a real thing to want, and exclusion should not be a one-way door.
    /// </summary>
    public bool IncludeAlreadyWatched { get; init; }
}
