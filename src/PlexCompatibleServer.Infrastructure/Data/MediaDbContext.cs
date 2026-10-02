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
            ("Items", "StreamsJson", "TEXT NULL")
        };

        foreach (var (table, column, definition) in additive)
        {
            if (await HasColumnAsync(table, column, ct)) continue;

            await Database.ExecuteSqlRawAsync(
                $"ALTER TABLE {table} ADD COLUMN {column} {definition}", ct);
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
