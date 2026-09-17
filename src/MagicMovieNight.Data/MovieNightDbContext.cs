using MagicMovieNight.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace MagicMovieNight.Data;

public class MovieNightDbContext(DbContextOptions<MovieNightDbContext> options) : DbContext(options)
{
    public DbSet<Viewer> Viewers => Set<Viewer>();

    public DbSet<ViewerIdentity> ViewerIdentities => Set<ViewerIdentity>();

    public DbSet<MediaItem> MediaItems => Set<MediaItem>();

    public DbSet<WatchEvent> WatchEvents => Set<WatchEvent>();

    public DbSet<RecommendationRun> RecommendationRuns => Set<RecommendationRun>();

    public DbSet<Recommendation> Recommendations => Set<Recommendation>();

    public DbSet<Rating> Ratings => Set<Rating>();

    public DbSet<IngestState> IngestStates => Set<IngestState>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Viewer>(e =>
        {
            e.Property(v => v.DisplayName).HasMaxLength(120).IsRequired();
            e.HasIndex(v => v.DisplayName).IsUnique();
            e.HasMany(v => v.Identities)
                .WithOne(i => i.Viewer!)
                .HasForeignKey(i => i.ViewerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ViewerIdentity>(e =>
        {
            e.Property(i => i.ExternalId).HasMaxLength(200).IsRequired();
            // One external identity can only belong to one viewer.
            e.HasIndex(i => new { i.Source, i.ExternalId }).IsUnique();
        });

        b.Entity<MediaItem>(e =>
        {
            e.Property(m => m.Title).HasMaxLength(500).IsRequired();
            e.Property(m => m.ImdbId).HasMaxLength(20);
            e.Property(m => m.PlexRatingKey).HasMaxLength(64);
            e.Property(m => m.ContentRating).HasMaxLength(20);

            // Postgres text[] — queryable, and avoids a join table for what is
            // always read whole alongside its parent.
            e.Property(m => m.Genres).HasColumnType("text[]");
            e.Property(m => m.People).HasColumnType("text[]");
            e.Property(m => m.AvailableOn).HasColumnType("text[]");

            e.HasIndex(m => m.TmdbId);
            e.HasIndex(m => m.TraktId);
            e.HasIndex(m => m.ImdbId);
            // Title+year+kind is the fallback dedup key for CSV rows with no ids.
            e.HasIndex(m => new { m.Title, m.Year, m.Kind });
        });

        b.Entity<WatchEvent>(e =>
        {
            e.Property(w => w.SourceKey).HasMaxLength(200).IsRequired();
            e.Property(w => w.Device).HasMaxLength(200);

            // The idempotency guarantee: re-pulling a source can never double-count.
            e.HasIndex(w => new { w.Source, w.SourceKey }).IsUnique();
            e.HasIndex(w => w.WatchedAt);
            e.HasIndex(w => new { w.ViewerId, w.WatchedAt });

            e.HasOne(w => w.Viewer!)
                .WithMany()
                .HasForeignKey(w => w.ViewerId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(w => w.MediaItem!)
                .WithMany(m => m.WatchEvents)
                .HasForeignKey(w => w.MediaItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RecommendationRun>(e =>
        {
            e.Property(r => r.ViewerIds).HasColumnType("integer[]");
            e.Property(r => r.ModelId).HasMaxLength(100);
            e.HasIndex(r => r.CreatedAt);
            e.HasMany(r => r.Recommendations)
                .WithOne(x => x.Run!)
                .HasForeignKey(x => x.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Recommendation>(e =>
        {
            e.Property(r => r.Pitch).IsRequired();
            e.Property(r => r.WhereToWatch).HasMaxLength(200);
            e.HasOne(r => r.MediaItem!)
                .WithMany()
                .HasForeignKey(r => r.MediaItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Rating>(e =>
        {
            e.Property(r => r.SourceKey).HasMaxLength(200).IsRequired();

            // Same idempotency guarantee as watch events: re-importing a ratings
            // export cannot create duplicates.
            e.HasIndex(r => new { r.Source, r.SourceKey }).IsUnique();

            // One opinion per person per thing. Re-rating updates in place rather than
            // stacking, which is what the UI's toggle behaviour depends on.
            e.HasIndex(r => new { r.ViewerId, r.MediaItemId, r.SeasonNumber, r.EpisodeNumber })
                .IsUnique();

            e.HasOne(r => r.Viewer!)
                .WithMany()
                .HasForeignKey(r => r.ViewerId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(r => r.MediaItem!)
                .WithMany()
                .HasForeignKey(r => r.MediaItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<IngestState>(e =>
        {
            e.HasIndex(s => s.Source).IsUnique();
        });
    }
}
