using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MagicMovieNight.Data;

/// <summary>
/// Used only by `dotnet ef` at design time. Without it the tooling would boot the web
/// host, which refuses to start without a real connection string — an unhelpful
/// requirement for generating a migration.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MovieNightDbContext>
{
    public MovieNightDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MovieNightDbContext>()
            .UseNpgsql("Host=localhost;Database=magicmovienight;Username=postgres;Password=postgres")
            .Options;

        return new MovieNightDbContext(options);
    }
}
