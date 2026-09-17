using MagicMovieNight.Core.Models;

namespace MagicMovieNight.Data;

/// <summary>
/// Per-source watermark. Pullers ask for everything after <see cref="LastSyncedAt"/>
/// rather than re-reading full history on every cycle.
/// </summary>
public class IngestState
{
    public int Id { get; set; }

    public WatchSource Source { get; set; }

    public DateTimeOffset? LastSyncedAt { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }

    public int LastRunEventCount { get; set; }

    public string? LastError { get; set; }
}
