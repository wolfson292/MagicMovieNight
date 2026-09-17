namespace MagicMovieNight.Core.Models;

/// <summary>
/// An account on some service — a Netflix profile, a Plex user, a Trakt login. Watch
/// events and their attribution hang off this, because a profile is the only thing a
/// source actually tells us about.
///
/// Who a profile represents is a separate question, answered by
/// <see cref="ProfileMembership"/>. That indirection is the point: "Wolf Family" on Plex
/// is not a person, and Angela's Netflix profile is sometimes two.
/// </summary>
public class Profile
{
    public int Id { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>Source-specific identities, e.g. ("Tautulli", "Wolf Family").</summary>
    public List<ProfileIdentity> Identities { get; set; } = [];

    /// <summary>Which people watching on this profile counts for.</summary>
    public List<ProfileMembership> Memberships { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// A profile nobody has claimed yet. These appear automatically as history syncs and
    /// are surfaced in the UI so they can be mapped, ignored, or left alone.
    /// </summary>
    public bool IsUnassigned => Memberships.Count == 0;
}

public class ProfileIdentity
{
    public int Id { get; set; }

    public int ProfileId { get; set; }

    public Profile? Profile { get; set; }

    /// <summary>Source system this identity belongs to.</summary>
    public WatchSource Source { get; set; }

    /// <summary>The source's own identifier — Tautulli username, Trakt slug, Netflix profile name.</summary>
    public required string ExternalId { get; set; }
}
