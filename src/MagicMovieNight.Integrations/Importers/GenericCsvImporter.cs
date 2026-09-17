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
/// Base for the exports that are not worth a bespoke parser. Amazon and Hulu both
/// ship wide CSVs whose column names drift between requests, so each subclass just
/// declares the aliases it has seen and inherits the rest.
/// </summary>
public abstract class GenericCsvImporter(ILogger logger) : IHistoryImporter
{
    public abstract WatchSource Source { get; }

    public abstract string DisplayName { get; }

    protected abstract string[] TitleAliases { get; }

    protected abstract string[] DateAliases { get; }

    protected virtual string[] ProfileAliases => ["Profile Name", "Profile", "Account", "User"];

    /// <summary>Columns that mark a row as a trailer, preview, or otherwise not a real watch.</summary>
    protected virtual string[] DurationAliases => ["Seconds Viewed", "Playback Duration", "Duration", "Minutes Viewed"];

    /// <summary>Rows shorter than this are previews and autoplay, not viewing.</summary>
    protected virtual int MinimumSecondsViewed => 120;

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

        var titleColumn = CsvColumnMap.Resolve(csv, TitleAliases);
        var dateColumn = CsvColumnMap.Resolve(csv, DateAliases);
        var profileColumn = CsvColumnMap.Resolve(csv, ProfileAliases);
        var durationColumn = CsvColumnMap.Resolve(csv, DurationAliases);

        if (titleColumn is null || dateColumn is null)
        {
            var found = csv.HeaderRecord is null ? "none" : string.Join(", ", csv.HeaderRecord);
            throw new InvalidDataException(
                $"Could not find a title and date column in this {DisplayName} export. "
                + $"Columns found: {found}. Expected a title column named one of "
                + $"[{string.Join(", ", TitleAliases)}] and a date column named one of "
                + $"[{string.Join(", ", DateAliases)}].");
        }

        logger.LogInformation(
            "Importing {Source} export using title column '{Title}' and date column '{Date}'.",
            Source, titleColumn, dateColumn);

        while (await csv.ReadAsync() && !ct.IsCancellationRequested)
        {
            var title = CsvColumnMap.Read(csv, titleColumn);
            var watchedAt = CsvColumnMap.ParseDate(CsvColumnMap.Read(csv, dateColumn));

            if (string.IsNullOrWhiteSpace(title) || watchedAt is null)
            {
                continue;
            }

            if (IsTooShort(csv, durationColumn))
            {
                continue;
            }

            yield return new RawWatchEvent
            {
                Title = CleanTitle(title),
                Kind = GuessKind(title),
                WatchedAt = watchedAt.Value,
                ExternalViewerId = CsvColumnMap.Read(csv, profileColumn),
                SourceKey = StableKey(title, watchedAt.Value),
                Fidelity = SourceFidelity.TitleAndDate,
            };
        }
    }

    protected virtual string CleanTitle(string title) => title.Trim();

    /// <summary>
    /// Without ids there is nothing to go on but the title text. Season markers are
    /// the only reliable tell; everything else resolves as a movie and gets corrected
    /// when the catalog looks it up.
    /// </summary>
    protected virtual MediaKind GuessKind(string title) =>
        title.Contains("Season", StringComparison.OrdinalIgnoreCase)
        || title.Contains("Episode", StringComparison.OrdinalIgnoreCase)
            ? MediaKind.Episode
            : MediaKind.Movie;

    private bool IsTooShort(CsvReader csv, string? durationColumn)
    {
        var raw = CsvColumnMap.Read(csv, durationColumn);
        if (string.IsNullOrWhiteSpace(raw) || !double.TryParse(raw, out var value))
        {
            return false;
        }

        var seconds = durationColumn?.Contains("Minute", StringComparison.OrdinalIgnoreCase) == true
            ? value * 60
            : value;

        return seconds < MinimumSecondsViewed;
    }

    private string StableKey(string title, DateTimeOffset watchedAt)
    {
        var bytes = Encoding.UTF8.GetBytes($"{Source}|{title}|{watchedAt.UtcDateTime:yyyy-MM-ddTHH:mm}");
        return Convert.ToHexString(MD5.HashData(bytes))[..24];
    }
}

/// <summary>
/// Amazon's "Request your data" bundle. The viewing report lives in
/// Digital.PrimeVideo.ViewingHistory or Digital-Video-Playback depending on when the
/// request was made, hence the wide alias list.
/// </summary>
public class PrimeVideoCsvImporter(ILogger<PrimeVideoCsvImporter> logger) : GenericCsvImporter(logger)
{
    public override WatchSource Source => WatchSource.PrimeCsv;

    public override string DisplayName => "Prime Video watch history";

    protected override string[] TitleAliases =>
    [
        "Title", "Item Name", "Video Title", "Playback Title",
        "Original Title", "Episode Title", "Series Title",
    ];

    protected override string[] DateAliases =>
    [
        "Playback Start Datetime (UTC)", "Playback Start Date Time",
        "Playback Date", "Event Date", "Date", "Start Time", "Watch Date",
    ];

    protected override string[] ProfileAliases => ["Profile Name", "Profile", "Account Name"];
}

/// <summary>Hulu's privacy-portal viewing export.</summary>
public class HuluCsvImporter(ILogger<HuluCsvImporter> logger) : GenericCsvImporter(logger)
{
    public override WatchSource Source => WatchSource.HuluCsv;

    public override string DisplayName => "Hulu viewing history";

    protected override string[] TitleAliases =>
    [
        "Title", "Content Title", "Video Title", "Series Name", "Program Title", "Name",
    ];

    protected override string[] DateAliases =>
    [
        "Date", "Watch Date", "Playback Date", "Timestamp", "Event Time", "Start Time",
    ];
}
