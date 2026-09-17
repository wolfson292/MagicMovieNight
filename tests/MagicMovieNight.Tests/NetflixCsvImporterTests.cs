using System.Text;
using MagicMovieNight.Core.Models;
using MagicMovieNight.Integrations.Importers;

namespace MagicMovieNight.Tests;

public class NetflixCsvImporterTests
{
    [Theory]
    [InlineData("Bodyguard: Season 1: Episode 1", "Bodyguard", 1, MediaKind.Episode)]
    [InlineData("Stranger Things: Stranger Things 4: Chapter One", "Stranger Things", null, MediaKind.Episode)]
    [InlineData("The Queen's Gambit: Limited Series: Openings", "The Queen's Gambit", null, MediaKind.Episode)]
    public void EpisodeTitlesResolveToTheirShow(
        string raw, string expectedTitle, int? expectedSeason, MediaKind expectedKind)
    {
        var (title, season, _, kind) = NetflixCsvImporter.SplitTitle(raw);

        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedSeason, season);
        Assert.Equal(expectedKind, kind);
    }

    [Theory]
    [InlineData("Glass Onion: A Knives Out Mystery")]
    [InlineData("Dune: Part Two")]
    [InlineData("Mission: Impossible")]
    public void FilmTitlesContainingColonsStayWhole(string raw)
    {
        var (title, _, _, kind) = NetflixCsvImporter.SplitTitle(raw);

        // A colon is punctuation far more often than it is a hierarchy separator.
        Assert.Equal(raw, title);
        Assert.Equal(MediaKind.Movie, kind);
    }

    [Fact]
    public async Task ParsesARealisticExport()
    {
        const string csv = """
            Title,Date
            "Slow Horses: Season 4: Identity Theft","9/12/26"
            "Hit Man","8/30/26"
            "The Bear: Season 3: Tomorrow","7/04/26"
            """;

        var importer = new NetflixCsvImporter();
        var events = new List<Core.Abstractions.RawWatchEvent>();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        await foreach (var e in importer.ParseAsync(stream))
        {
            events.Add(e);
        }

        Assert.Equal(3, events.Count);
        Assert.Equal("Slow Horses", events[0].Title);
        Assert.Equal(4, events[0].SeasonNumber);
        Assert.Equal("Hit Man", events[1].Title);
        Assert.Equal(MediaKind.Movie, events[1].Kind);
        Assert.All(events, e => Assert.Equal(SourceFidelity.TitleAndDate, e.Fidelity));
    }

    [Fact]
    public async Task RejectsAFileThatIsNotANetflixExport()
    {
        const string csv = "Something,Else\n1,2";

        var importer = new NetflixCsvImporter();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in importer.ParseAsync(stream))
            {
            }
        });
    }

    [Fact]
    public void TheSameRowAlwaysProducesTheSameKey()
    {
        var at = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        // Idempotent re-imports depend on this: the export has no row id of its own.
        Assert.Equal(
            NetflixCsvImporter.StableKey("Hit Man", at),
            NetflixCsvImporter.StableKey("Hit Man", at));

        Assert.NotEqual(
            NetflixCsvImporter.StableKey("Hit Man", at),
            NetflixCsvImporter.StableKey("Hit Man", at.AddDays(1)));
    }
}
