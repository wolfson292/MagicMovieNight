using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using MagicMovieNight.Core.Abstractions;
using MagicMovieNight.Core.Models;
using Microsoft.Extensions.Logging;

namespace MagicMovieNight.Integrations.Importers;

/// <summary>
/// Parses Netflix's Ratings.csv, which arrives in the full account data export rather
/// than from the viewing-activity page.
///
/// Netflix changed scales in 2017 — five stars became thumbs — and the export still
/// carries whichever the rating was made under, so both columns are handled. A
/// thumbs-down here is the single most valuable signal in the whole system: it is the
/// household saying outright that they did not like something, which no amount of
/// watch history can tell you.
/// </summary>
public class NetflixRatingsCsvImporter(ILogger<NetflixRatingsCsvImporter> logger) : IRatingImporter
{
    public WatchSource Source => WatchSource.NetflixCsv;

    public string DisplayName => "Netflix ratings (thumbs)";

    public async IAsyncEnumerable<RawRating> ParseAsync(
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

        var titleColumn = CsvColumnMap.Resolve(csv, "Title Name", "Title", "Movie Title");
        var thumbsColumn = CsvColumnMap.Resolve(csv, "Thumbs Value", "Thumbs");
        var starsColumn = CsvColumnMap.Resolve(csv, "Star Value", "Rating", "Stars");
        var dateColumn = CsvColumnMap.Resolve(csv, "Event Utc Ts", "Devices Date", "Date", "Rating Date");
        var profileColumn = CsvColumnMap.Resolve(csv, "Profile Name", "Profile");
        var typeColumn = CsvColumnMap.Resolve(csv, "Title Type", "Type");
        var episodeColumn = CsvColumnMap.Resolve(csv, "Episode Title Name", "Episode Title");

        if (titleColumn is null || (thumbsColumn is null && starsColumn is null))
        {
            var found = csv.HeaderRecord is null ? "none" : string.Join(", ", csv.HeaderRecord);
            throw new InvalidDataException(
                "This does not look like a Netflix ratings export — expected a title column "
                + $"and either 'Thumbs Value' or 'Star Value'. Columns found: {found}. "
                + "The ratings file comes from the full account data request "
                + "(netflix.com/account/getmyinfo), not the viewing-activity page.");
        }

        logger.LogInformation(
            "Importing Netflix ratings using '{Title}' with {Scale}.",
            titleColumn,
            thumbsColumn is not null ? $"thumbs column '{thumbsColumn}'" : $"star column '{starsColumn}'");

        while (await csv.ReadAsync() && !ct.IsCancellationRequested)
        {
            var rawTitle = CsvColumnMap.Read(csv, titleColumn);
            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                continue;
            }

            var (value, stars) = ReadValue(csv, thumbsColumn, starsColumn);
            if (value is null)
            {
                continue;
            }

            // The date column is optional in some exports; an undated rating is still
            // worth having, it just gets no recency advantage.
            var ratedAt = CsvColumnMap.ParseDate(CsvColumnMap.Read(csv, dateColumn))
                ?? DateTimeOffset.UtcNow.AddYears(-1);

            var episodeTitle = CsvColumnMap.Read(csv, episodeColumn);
            var declaredType = CsvColumnMap.Read(csv, typeColumn);

            var (title, season, episode, kind) = NetflixCsvImporter.SplitTitle(rawTitle);

            // An "Episode Title Name" column means the row is about one episode even
            // when the title itself carries no season marker.
            if (!string.IsNullOrWhiteSpace(episodeTitle))
            {
                kind = MediaKind.Episode;
            }
            else if (declaredType is not null)
            {
                kind = declaredType.Contains("Show", StringComparison.OrdinalIgnoreCase)
                    || declaredType.Contains("Series", StringComparison.OrdinalIgnoreCase)
                    || declaredType.Contains("Season", StringComparison.OrdinalIgnoreCase)
                        ? MediaKind.Show
                        : kind;
            }

            yield return new RawRating
            {
                Title = title,
                Kind = kind,
                Value = value.Value,
                Stars = stars,
                RatedAt = ratedAt,
                ExternalViewerId = CsvColumnMap.Read(csv, profileColumn),
                SeasonNumber = season,
                EpisodeNumber = episode,
                SourceKey = StableKey(rawTitle, episodeTitle),
            };
        }
    }

    private static (RatingValue? Value, int? Stars) ReadValue(
        CsvReader csv,
        string? thumbsColumn,
        string? starsColumn)
    {
        var thumbsRaw = CsvColumnMap.Read(csv, thumbsColumn);
        if (int.TryParse(thumbsRaw, out var thumbs) && thumbs > 0)
        {
            return (RatingScale.FromNetflixThumbs(thumbs), null);
        }

        var starsRaw = CsvColumnMap.Read(csv, starsColumn);
        if (int.TryParse(starsRaw, out var stars) && stars > 0)
        {
            return (RatingScale.FromStars(stars, outOf: 5), stars);
        }

        return (null, null);
    }

    /// <summary>
    /// Keyed on the title rather than the date: a rating is an opinion someone holds,
    /// and re-rating the same thing should update it, not add a second one.
    /// </summary>
    private static string StableKey(string title, string? episodeTitle)
    {
        var bytes = Encoding.UTF8.GetBytes($"netflix-rating|{title}|{episodeTitle}");
        return Convert.ToHexString(MD5.HashData(bytes))[..24];
    }
}
