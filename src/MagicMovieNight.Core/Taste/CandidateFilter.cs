using MagicMovieNight.Core.Abstractions;

namespace MagicMovieNight.Core.Taste;

/// <summary>
/// Trims the pooled candidates down to what is worth sending to the model.
///
/// Kept separate from the engine because this is the part with rules worth pinning down:
/// the model is told not to recommend things already seen, but an instruction is a
/// request and an exclusion is a guarantee. Anything the household has demonstrably
/// watched never reaches the prompt in the first place.
/// </summary>
public static class CandidateFilter
{
    public static List<Candidate> Apply(
        IEnumerable<Candidate> candidates,
        ISet<int> excludedMediaItemIds,
        RecommendationRequest request,
        int maxCandidates)
    {
        return candidates
            // Several sources can propose the same title; keep whichever ranked it highest.
            .GroupBy(c => c.Item.Id)
            .Select(g => g.OrderByDescending(c => c.PreScore).First())
            .Where(c => !excludedMediaItemIds.Contains(c.Item.Id))
            .Where(c => !request.LibraryOnly || c.Item.InLibrary)
            // An unknown runtime is not a reason to exclude — that would drop every title
            // TMDB could not identify, which is exactly the long tail worth surfacing.
            .Where(c => request.MaxRuntimeMinutes is null
                || c.Item.RuntimeMinutes is null
                || c.Item.RuntimeMinutes <= request.MaxRuntimeMinutes)
            .OrderByDescending(c => c.PreScore)
            .Take(maxCandidates)
            .ToList();
    }
}
