namespace MagicMovieNight.Core.Models;

/// <summary>
/// One pass of the recommender. Runs are persisted whole so a pick can always be
/// traced back to the candidates and the taste profile that produced it.
/// </summary>
public class RecommendationRun
{
    public int Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>People this run was for. Empty means the whole household.</summary>
    public List<int> PersonIds { get; set; } = [];

    /// <summary>Free-text mood/constraint the household typed in, e.g. "something short and funny".</summary>
    public string? Prompt { get; set; }

    public int CandidateCount { get; set; }

    public string? ModelId { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public TimeSpan Duration { get; set; }

    /// <summary>Set when the run failed, so the UI can show why rather than an empty list.</summary>
    public string? Error { get; set; }

    public List<Recommendation> Recommendations { get; set; } = [];
}

public class Recommendation
{
    public int Id { get; set; }

    public int RunId { get; set; }

    public RecommendationRun? Run { get; set; }

    public int MediaItemId { get; set; }

    public MediaItem? MediaItem { get; set; }

    /// <summary>Position in the run, 1 = top pick.</summary>
    public int Rank { get; set; }

    /// <summary>Claude's case for watching this tonight, in the household's terms.</summary>
    public required string Pitch { get; set; }

    /// <summary>The specific history that justifies the pick, e.g. "you both finished Andor in four nights".</summary>
    public string? BasedOn { get; set; }

    /// <summary>Where to actually play it — "Plex", "Netflix", "rent on Prime".</summary>
    public string? WhereToWatch { get; set; }

    public RecommendationOrigin Origin { get; set; }

    /// <summary>0-100 confidence from the model, used only for display ordering hints.</summary>
    public int? Confidence { get; set; }

    public Verdict Verdict { get; set; } = Verdict.None;

    public DateTimeOffset? VerdictAt { get; set; }
}
