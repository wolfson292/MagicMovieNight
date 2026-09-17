namespace MagicMovieNight.Core.Models;

/// <summary>
/// A human being. Distinct from <see cref="Profile"/>, which is an account on some
/// service, because the two genuinely are not the same thing: a Netflix profile can
/// represent one person tonight and two people tomorrow, and a Plex profile named after
/// the household represents everyone in it.
///
/// Recommendations are asked for by person. Taste is gathered from every profile that
/// person is a member of, so watching together on one account still informs both their
/// profiles.
/// </summary>
public class Person
{
    public int Id { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// Part of the default "what should we watch" pick. Typically the couple who
    /// actually choose the film, rather than everyone who lives in the house.
    /// </summary>
    public bool InHousehold { get; set; }

    /// <summary>Lives here, as opposed to an occasional visitor.</summary>
    public bool IsResident { get; set; } = true;

    /// <summary>Hard ceiling on content rating, for younger members of the household.</summary>
    public string? ContentRatingCeiling { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ProfileMembership> Memberships { get; set; } = [];
}

/// <summary>
/// Says that watching on a profile counts as this person watching.
///
/// A profile can have several members — a shared account — and a person can belong to
/// several profiles, which is the normal case: their own account plus whatever joint
/// ones the household uses.
/// </summary>
public class ProfileMembership
{
    public int Id { get; set; }

    public int PersonId { get; set; }

    public Person? Person { get; set; }

    public int ProfileId { get; set; }

    public Profile? Profile { get; set; }

    /// <summary>
    /// True when this profile is principally this person's own. Used to tell a solo
    /// account apart from a shared one without asking.
    /// </summary>
    public bool IsPrimary { get; set; }
}
