using Microsoft.EntityFrameworkCore;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Infrastructure.Data;

public sealed class MediaDbContext : DbContext
{
    public MediaDbContext(DbContextOptions<MediaDbContext> options) : base(options) { }

    public DbSet<MediaLibrary> Libraries => Set<MediaLibrary>();
    public DbSet<MediaItem> Items => Set<MediaItem>();

    /// <summary>
    /// The database is created with EnsureCreated, which will not add columns to a table that
    /// already exists. New scan/probe fields therefore need an explicit additive step. Statements
    /// are idempotent so this is safe to run on every start.
    /// </summary>
    public async Task EnsureColumnsAsync(CancellationToken ct = default)
    {
        var additive = new (string Table, string Column, string Definition)[]
        {
            ("Libraries", "Uuid", "TEXT NOT NULL DEFAULT ''"),
            ("Items", "Bitrate", "INTEGER NULL"),
            ("Items", "Width", "INTEGER NULL"),
            ("Items", "Height", "INTEGER NULL"),
            ("Items", "VideoCodec", "TEXT NULL"),
            ("Items", "VideoProfile", "TEXT NULL"),
            ("Items", "AudioCodec", "TEXT NULL"),
            ("Items", "AudioChannels", "INTEGER NULL"),
            ("Items", "FrameRate", "REAL NULL"),
            ("Items", "Container", "TEXT NULL"),
            ("Items", "StreamsJson", "TEXT NULL"),
            ("Items", "LibrarySectionId", "INTEGER NULL"),
            ("Items", "ParentId", "INTEGER NULL"),
            ("Items", "MetadataType", "INTEGER NULL"),
            ("Items", "Guid", "TEXT NULL"),
            ("Items", "MediaItemCount", "INTEGER NULL"),
            ("Items", "OriginalTitle", "TEXT NULL"),
            ("Items", "Studio", "TEXT NULL"),
            ("Items", "Rating", "REAL NULL"),
            ("Items", "RatingCount", "INTEGER NULL"),
            ("Items", "Tagline", "TEXT NULL"),
            ("Items", "Trivia", "TEXT NULL"),
            ("Items", "Quotes", "TEXT NULL"),
            ("Items", "ContentRating", "TEXT NULL"),
            ("Items", "ContentRatingAge", "INTEGER NULL"),
            ("Items", "[Index]", "INTEGER NULL"),
            ("Items", "AbsoluteIndex", "INTEGER NULL"),
            ("Items", "UserThumbUrl", "TEXT NULL"),
            ("Items", "UserArtUrl", "TEXT NULL"),
            ("Items", "UserBannerUrl", "TEXT NULL"),
            ("Items", "UserMusicUrl", "TEXT NULL"),
            ("Items", "UserFields", "TEXT NULL"),
            ("Items", "TagsGenre", "TEXT NULL"),
            ("Items", "TagsCollection", "TEXT NULL"),
            ("Items", "TagsDirector", "TEXT NULL"),
            ("Items", "TagsWriter", "TEXT NULL"),
            ("Items", "TagsStar", "TEXT NULL"),
            ("Items", "OriginallyAvailableAt", "TEXT NULL"),
            ("Items", "AvailableAt", "TEXT NULL"),
            ("Items", "ExpiresAt", "TEXT NULL"),
            ("Items", "RefreshedAt", "TEXT NULL"),
            ("Items", "TagsCountry", "TEXT NULL"),
            ("Items", "ExtraData", "TEXT NULL"),
            ("Items", "Hash", "TEXT NULL"),
            ("Items", "AudienceRating", "REAL NULL"),
            ("Items", "ChangedAt", "INTEGER NULL"),
            ("Items", "ResourcesChangedAt", "INTEGER NULL"),
            ("Items", "Remote", "INTEGER NULL"),
            ("Items", "EditionTitle", "TEXT NULL"),
            ("Items", "Slug", "TEXT NULL"),
            ("Items", "UserClearLogoUrl", "TEXT NULL"),
            ("Items", "IsAdult", "INTEGER NULL"),
            ("Items", "MetadataAgentProviderGroupId", "INTEGER NULL"),
            ("Items", "UserSquareArtUrl", "TEXT NULL"),
            ("Items", "ViewOffset", "INTEGER NULL"),
            ("Items", "LastViewedAt", "TEXT NULL"),
            ("Items", "ViewCount", "INTEGER NOT NULL DEFAULT 0"),
            ("Items", "OfficialPosterPath", "TEXT NULL"),
            ("Items", "OfficialArtPath", "TEXT NULL"),
            ("Items", "OfficialParentPosterPath", "TEXT NULL"),
            ("Items", "OfficialGrandparentPosterPath", "TEXT NULL"),
            ("Items", "ContinueWatchingDismissedAt", "TEXT NULL")
        };

        foreach (var (table, column, definition) in additive)
        {
            if (await HasColumnAsync(table, column, ct)) continue;

            // The statement is assembled from the fixed additive list above - table, column and
            // definition are never user input - which EF1002 cannot prove about interpolation.
#pragma warning disable EF1002
            await Database.ExecuteSqlRawAsync(
                $"ALTER TABLE {table} ADD COLUMN {column} {definition}", ct);
#pragma warning restore EF1002
        }

        // Newly added probe columns are null in existing rows, which is exactly the condition the
        // scanner uses to re-probe, so no explicit invalidation is needed.

        // Backfill the generated library identifiers now that the column exists.
        var libraries = await Libraries.ToListAsync(ct);
        var missing = libraries.Where(x => string.IsNullOrEmpty(x.Uuid)).ToList();
        foreach (var library in missing)
        {
            library.Uuid = Guid.NewGuid().ToString();
            await Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE Libraries SET Uuid = {library.Uuid} WHERE Id = {library.Id}", ct);
        }
    }

    private async Task<bool> HasColumnAsync(string table, string column, CancellationToken ct)
    {
        var connection = Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({table})";

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (column == "[Index]")
                {
                    column = "Index";
                }
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MediaLibrary>()
            .HasMany(x => x.Items)
            .WithOne(x => x.Library)
            .HasForeignKey(x => x.LibraryId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MediaLibrary>()
            .HasIndex(x => new { x.Name, x.RootPath })
            .IsUnique();

        modelBuilder.Entity<MediaItem>()
            .HasIndex(x => new { x.LibraryId, x.FilePath })
            .IsUnique();
    }
}
