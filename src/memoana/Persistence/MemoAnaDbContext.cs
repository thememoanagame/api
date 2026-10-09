using Microsoft.EntityFrameworkCore;

namespace memoana.Persistence;

public sealed class MemoAnaDbContext(DbContextOptions<MemoAnaDbContext> options) : DbContext(options)
{
    public DbSet<PersistedRoom> Rooms => Set<PersistedRoom>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PersistedRoom>(entity =>
        {
            entity.HasKey(x => x.RoomId);
            entity.Property(x => x.SnapshotJson).IsRequired();
            entity.Property(x => x.ThemeId).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.ExpiresAt);
        });
    }
}

public sealed class PersistedRoom
{
    public string RoomId { get; set; } = string.Empty;
    public int Mode { get; set; }
    public int Difficulty { get; set; }
    public int Status { get; set; }
    public string ThemeId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
    public string SnapshotJson { get; set; } = string.Empty;
}
