namespace MagicMovieNight.Core.Models;

/// <summary>
/// A person in the household. Identities from each source are mapped onto one
/// viewer so Tautulli's "scott", Trakt's slug, and a Netflix profile name all
/// resolve to the same taste profile.
/// </summary>
public class Viewer
{
    public int Id { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>Included in household-blend recommendations by default.</summary>
    public bool IncludeInHousehold { get; set; } = true;

    /// <summary>Source-specific identities, e.g. ("Tautulli", "scott"), ("Netflix", "Kids").</summary>
    public List<ViewerIdentity> Identities { get; set; } = [];

    /// <summary>Hard filters the household has set for this viewer, e.g. max content rating.</summary>
    public string? ContentRatingCeiling { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ViewerIdentity
{
    public int Id { get; set; }

    public int ViewerId { get; set; }

    public Viewer? Viewer { get; set; }

    /// <summary>Source system this identity belongs to.</summary>
    public WatchSource Source { get; set; }

    /// <summary>The source's own identifier — Tautulli username, Trakt slug, Netflix profile name.</summary>
    public required string ExternalId { get; set; }
}
