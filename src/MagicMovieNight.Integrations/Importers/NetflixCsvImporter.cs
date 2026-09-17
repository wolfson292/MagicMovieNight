using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using MagicMovieNight.Core.Abstractions;
using MagicMovieNight.Core.Models;

namespace MagicMovieNight.Integrations.Importers;

/// <summary>
/// Parses the per-profile "Viewing activity" CSV from netflix.com/viewingactivity.
/// Two columns, Title and Date, and the title carries the hierarchy inline:
/// "Bodyguard: Season 1: Episode 1". Splitting that back apart is what lets an
/// episode row attribute taste to the show rather than to a title nobody recognises.
/// </summary>
public class NetflixCsvImporter : IHistoryImporter
{
    public WatchSource Source => WatchSource.NetflixCsv;

    public string DisplayName => "Netflix viewing activity";

    public async IAsyncEnumerable<RawWatchEvent> ParseAsync(
        Stream csvStream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(csvStream);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null,
        });

        await csv.ReadAsync();
        csv.ReadHeader();

        var titleColumn = CsvColumnMap.Resolve(csv, "Title");
        var dateColumn = CsvColumnMap.Resolve(csv, "Date", "Start Time");
        var profileColumn = CsvColumnMap.Resolve(csv, "Profile Name", "Profile");

        if (titleColumn is null || dateColumn is null)
        {
            throw new InvalidDataException(
                "This does not look like a Netflix viewing-activity export — expected Title and Date columns.");
        }

        while (await csv.ReadAsync() && !ct.IsCancellationRequested)
        {
            var rawTitle = CsvColumnMap.Read(csv, titleColumn);
            var watchedAt = CsvColumnMap.ParseDate(CsvColumnMap.Read(csv, dateColumn));

            if (string.IsNullOrWhiteSpace(rawTitle) || watchedAt is null)
            {
                continue;
            }

            var (title, season, episode, kind) = SplitTitle(rawTitle);

            yield return new RawWatchEvent
            {
                Title = title,
                Kind = kind,
                WatchedAt = watchedAt.Value,
                ExternalProfileId = CsvColumnMap.Read(csv, profileColumn),
                SeasonNumber = season,
                EpisodeNumber = episode,
                // The export has no completion data at all — every row is a title and
                // a day. Treat it as watched and let fidelity weighting discount it.
                PercentComplete = null,
                SourceKey = StableKey(rawTitle, watchedAt.Value),
                Fidelity = SourceFidelity.TitleAndDate,
            };
        }
    }

    /// <summary>
    /// Netflix packs hierarchy into the title with colons. The shapes seen in practice:
    /// "Movie Name", "Show: Season 2: Episode Title", "Show: Limited Series: Part 3",
    /// and "Show: Stranger Things 4: Chapter One".
    ///
    /// The hard part is that a colon is punctuation more often than it is a separator —
    /// "Dune: Part Two" and "Mission: Impossible" are films. Two rules keep them apart:
    /// an explicit "Season N" segment always means episodic, and any other episodic
    /// marker only counts when the title has three or more segments, which a film title
    /// almost never does.
    /// </summary>
    internal static (string Title, int? Season, int? Episode, MediaKind Kind) SplitTitle(string raw)
    {
        var parts = raw.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            return (raw.Trim(), null, null, MediaKind.Movie);
        }

        var show = parts[0];

        foreach (var part in parts.Skip(1))
        {
            var season = ExtractNumber(part, "Season");
            if (season is not null)
            {
                return (show, season, ExtractEpisodeNumber(parts), MediaKind.Episode);
            }
        }

        if (parts.Length >= 3 && parts.Skip(1).Any(IsEpisodicMarker))
        {
            return (show, null, ExtractEpisodeNumber(parts), MediaKind.Episode);
        }

        return (raw.Trim(), null, null, MediaKind.Movie);
    }

    /// <summary>
    /// Netflix numbers episodes in words as often as digits ("Chapter One"), so the
    /// keyword itself is the signal and the number is a bonus.
    /// </summary>
    private static bool IsEpisodicMarker(string part)
    {
        string[] markers =
        [
            "Limited Series", "Miniseries", "Episode", "Part",
            "Chapter", "Book", "Volume", "Collection", "Series",
        ];

        return markers.Any(m => part.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static int? ExtractEpisodeNumber(string[] parts) =>
        parts.Skip(1).Select(p => ExtractNumber(p, "Episode")).FirstOrDefault(n => n is not null);

    private static int? ExtractNumber(string part, string keyword)
    {
        var idx = part.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var digits = new string(part[(idx + keyword.Length)..]
            .SkipWhile(c => !char.IsDigit(c))
            .TakeWhile(char.IsDigit)
            .ToArray());

        return int.TryParse(digits, out var n) ? n : null;
    }

    /// <summary>
    /// The export has no row id, so the key is a hash of the row's own content. Two
    /// identical rows in one file are genuinely the same watch as far as we can tell.
    /// </summary>
    internal static string StableKey(string title, DateTimeOffset watchedAt)
    {
        var bytes = Encoding.UTF8.GetBytes($"netflix|{title}|{watchedAt.UtcDateTime:yyyy-MM-dd}");
        return Convert.ToHexString(MD5.HashData(bytes))[..24];
    }
}
