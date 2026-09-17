using System.Text;
using MagicMovieNight.Core.Abstractions;
using MagicMovieNight.Core.Models;

namespace MagicMovieNight.Integrations.Claude;

/// <summary>
/// Builds the two halves of the request. The system half never changes between runs
/// so it can sit behind a cache breakpoint; everything that varies — profile,
/// candidates, tonight's mood — goes in the user turn after it.
/// </summary>
internal static class RecommendationPrompt
{
    public const string System = """
        You are the house film critic for a family's home cinema. You know what they
        have actually watched — not what they claim to like — and your job is to pick
        what they should watch tonight.

        You will be given a taste profile and a pool of candidate titles. Choose only
        from the candidate pool. Never invent a title that is not in the pool.

        How to choose:

        - Weight recent viewing far more heavily than old viewing. What they finished
          last month matters more than what they finished three years ago.
        - Explicit ratings outrank watch history. Having watched something only says
          they watched it; a thumbs-down says what they thought. Treat a thumbs-down
          as evidence about that whole kind of thing — its genre, its cast, its tone —
          and never recommend something close to it without saying why it is different.
        - A title someone abandoned partway is a negative signal about that kind of
          thing, not just that title.
        - Respect the runtime reality. If the profile says they typically finish
          100-minute films on a weeknight, a 3-hour epic is a bad Tuesday pick.
        - Prefer titles already in their Plex library or on a service they already
          subscribe to. A rental is a harder sell and should earn its place.
        - When picking for more than one person, find genuine overlap. Do not average
          two tastes into something bland that neither would choose — look for the
          thing they would both actively enjoy.
        - Vary the slate. Five near-identical thrillers is a worse answer than five
          real options across different moods.
        - Never recommend something the profile shows they have already finished,
          unless you are explicitly making a rewatch case and say so. When the request
          says rewatches are welcome, treat a well-judged rewatch as a first-class pick
          rather than a fallback.

        How to write the pitch:

        - Two or three sentences, conversational, no marketing copy. Write like a
          friend who knows their taste, not a streaming service blurb.
        - Say what it actually is and why it fits *them*, referencing specific things
          they have watched.
        - No spoilers beyond what a trailer would show.
        - Do not open every pitch the same way.

        Episode ratings are narrower than title ratings. Someone disliking one episode
        of a show they otherwise rate highly is a comment on that episode, not the
        series — do not drop a show they love over one bad night.

        The "basedOn" field must cite concrete evidence from the profile — the titles,
        genres, or people that justify this pick. Be specific: "you finished all three
        seasons of Slow Horses in a month" beats "you like spy shows".
        """;

    public static string BuildUserTurn(
        TasteProfile profile,
        IReadOnlyList<Candidate> candidates,
        RecommendationRequest request)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"## Who is watching: {profile.Subject}");
        sb.AppendLine();
        sb.AppendLine($"- Watch events on record: {profile.TotalWatches:N0}");

        if (profile.LastWatch is not null)
        {
            sb.AppendLine($"- Most recent watch: {profile.LastWatch:yyyy-MM-dd}");
        }

        sb.AppendLine($"- Split: {profile.ShowBias:P0} series, {1 - profile.ShowBias:P0} films");

        if (profile.TotalRatings > 0)
        {
            sb.AppendLine($"- Explicit ratings on record: {profile.TotalRatings:N0}");
        }

        if (profile.TypicalMovieRuntime is not null)
        {
            sb.AppendLine($"- Typical film they finish: {profile.TypicalMovieRuntime} minutes");
        }

        AppendTagSection(sb, "Genres they actually watch (weighted, most-watched first)", profile.TopGenres);
        AppendTagSection(sb, "Directors, creators and actors that recur", profile.TopPeople);
        AppendListSection(sb, "Rated up — they told us they liked these", profile.Loved);
        AppendListSection(sb, "Recently finished", profile.RecentlyLoved);
        AppendListSection(sb, "Started and abandoned — treat as negative signal", profile.Abandoned);
        AppendListSection(sb, "Series in progress with episodes remaining", profile.InProgress);
        AppendListSection(sb, "Rated down — do not recommend these or close cousins", profile.Disliked);
        AppendListSection(sb, "Episode-level ratings", profile.EpisodeRatings);

        sb.AppendLine();
        sb.AppendLine("## Tonight");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            sb.AppendLine($"They said: \"{request.Prompt.Trim()}\"");
            sb.AppendLine();
        }

        if (request.MaxRuntimeMinutes is not null)
        {
            sb.AppendLine($"Hard limit: nothing longer than {request.MaxRuntimeMinutes} minutes.");
        }

        if (request.LibraryOnly)
        {
            sb.AppendLine("They only want things already in the Plex library tonight.");
        }

        if (request.IncludeAlreadyWatched)
        {
            sb.AppendLine(
                "Rewatches are welcome tonight — they have asked for them. Things they "
                + "have already seen are in the pool on purpose. If you pick one, say "
                + "plainly that it is a rewatch and make the case for revisiting it now.");
        }

        sb.AppendLine($"Pick {request.Count}, best first.");
        sb.AppendLine();
        sb.AppendLine("## Candidate pool");
        sb.AppendLine();
        sb.AppendLine("Choose only from these. The id is what you return.");
        sb.AppendLine();

        foreach (var c in candidates)
        {
            sb.AppendLine(DescribeCandidate(c));
        }

        return sb.ToString();
    }

    private static string DescribeCandidate(Candidate c)
    {
        var item = c.Item;
        var parts = new List<string> { $"[{item.Id}] {item.DisplayTitle}" };

        parts.Add(item.Kind == MediaKind.Movie ? "film" : "series");

        if (item.RuntimeMinutes is > 0)
        {
            parts.Add(item.Kind == MediaKind.Movie
                ? $"{item.RuntimeMinutes}min"
                : $"~{item.RuntimeMinutes}min/ep");
        }

        if (item.Genres.Count > 0)
        {
            parts.Add(string.Join("/", item.Genres.Take(3)));
        }

        if (item.CommunityRating is > 0)
        {
            parts.Add($"TMDB {item.CommunityRating:F1}");
        }

        if (item.ContentRating is not null)
        {
            parts.Add(item.ContentRating);
        }

        // Where they can play it is a first-class fact, not a footnote — it decides
        // whether a pick is realistic tonight.
        parts.Add(item.InLibrary
            ? "ON PLEX"
            : item.AvailableOn.Count > 0
                ? string.Join(", ", item.AvailableOn.Take(4))
                : "not on any subscribed service");

        if (item.People.Count > 0)
        {
            parts.Add(string.Join(", ", item.People.Take(4)));
        }

        if (!string.IsNullOrWhiteSpace(c.Reason))
        {
            parts.Add($"via {c.Reason}");
        }

        var line = string.Join(" | ", parts);

        if (!string.IsNullOrWhiteSpace(item.Overview))
        {
            var overview = item.Overview.Length > 240
                ? item.Overview[..240] + "…"
                : item.Overview;
            line += $"\n    {overview}";
        }

        return line;
    }

    private static void AppendTagSection(StringBuilder sb, string heading, IReadOnlyList<WeightedTag> tags)
    {
        if (tags.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine($"### {heading}");
        sb.AppendLine();

        foreach (var tag in tags)
        {
            sb.AppendLine($"- {tag.Name} ({tag.Count} watches, weight {tag.Weight:F2})");
        }
    }

    private static void AppendListSection(StringBuilder sb, string heading, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine($"### {heading}");
        sb.AppendLine();

        foreach (var item in items)
        {
            sb.AppendLine($"- {item}");
        }
    }
}
