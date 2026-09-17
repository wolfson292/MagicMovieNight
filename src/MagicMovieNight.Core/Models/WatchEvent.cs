namespace MagicMovieNight.Core.Models;

/// <summary>
/// One viewing. Sources disagree wildly on fidelity: Tautulli knows who watched what
/// on which device and how far they got; a Netflix CSV knows a title and a date.
/// <see cref="Fidelity"/> records which so scoring can weight accordingly.
/// </summary>
public class WatchEvent
{
    public long Id { get; set; }

    /// <summary>The account this was watched on. Who that represents is a separate question.</summary>
    public int ProfileId { get; set; }

    public Profile? Profile { get; set; }

    public int MediaItemId { get; set; }

    public MediaItem? MediaItem { get; set; }

    public WatchSource Source { get; set; }

    public DateTimeOffset WatchedAt { get; set; }

    /// <summary>0-100 where known. Null for CSV sources that only report a date.</summary>
    public int? PercentComplete { get; set; }

    /// <summary>Playback device, when the source reports one (Tautulli does).</summary>
    public string? Device { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    /// <summary>
    /// Stable per-source key used to make ingestion idempotent — Tautulli history id,
    /// Trakt play id, or a hash of the CSV row.
    /// </summary>
    public required string SourceKey { get; set; }

    public SourceFidelity Fidelity { get; set; }

    /// <summary>
    /// A watch counts as a real signal of taste when the viewer got most of the way through.
    /// Sources without completion data are trusted at face value.
    /// </summary>
    public bool IsMeaningful => PercentComplete is null or >= 70;
}

public enum SourceFidelity
{
    /// <summary>Title and date only — CSV exports.</summary>
    TitleAndDate = 0,

    /// <summary>Per-viewer attribution, but no completion data.</summary>
    Attributed = 1,

    /// <summary>Viewer, device, and completion percentage — Tautulli.</summary>
    Full = 2,
}
