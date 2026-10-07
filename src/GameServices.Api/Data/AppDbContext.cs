using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GameServices.Api.Data;

/// <summary>
/// Entity Framework Core's view of the database: each DbSet is a table.
/// EF Core turns C# objects into rows and LINQ queries into SQL, so we never
/// build SQL strings by hand (which also prevents SQL injection).
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PlayerSave> PlayerSaves => Set<PlayerSave>();
    public DbSet<CardGameProfile> CardGameProfiles => Set<CardGameProfile>();
    public DbSet<OwnedCard> OwnedCards => Set<OwnedCard>();
    public DbSet<LedgerEntry> Ledger => Set<LedgerEntry>();

    // SQLite doesn't remember that a date is UTC, so we put the "UTC" label back
    // whenever we read one. Otherwise clients would see dates without the trailing Z.
    protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
    {
        configuration.Properties<DateTime>().HaveConversion<UtcConverter>();
        configuration.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    private class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private class NullableUtcConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<User>(user =>
        {
            user.HasKey(u => u.Id);
            user.Property(u => u.Username).HasMaxLength(20).IsRequired();
            user.Property(u => u.NormalizedUsername).HasMaxLength(20).IsRequired();
            user.Property(u => u.Email).HasMaxLength(254);
            user.Property(u => u.NormalizedEmail).HasMaxLength(254);

            // The database itself guarantees no duplicate usernames, even if two
            // sign-ups arrive at the same instant (a "race condition").
            user.HasIndex(u => u.NormalizedUsername).IsUnique();
            user.HasIndex(u => u.NormalizedEmail).IsUnique();
        });

        model.Entity<PlayerSave>(save =>
        {
            save.HasKey(s => s.Id);
            save.Property(s => s.GameId).HasMaxLength(40).IsRequired();
            save.Property(s => s.Json).IsRequired();

            // One save per player per game. Also stops two simultaneous first-saves.
            save.HasIndex(s => new { s.UserId, s.GameId }).IsUnique();

            // If two requests try to update the same version at once, only one wins.
            save.Property(s => s.Version).IsConcurrencyToken();

            save.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<CardGameProfile>(profile =>
        {
            profile.HasKey(p => p.Id);
            profile.HasIndex(p => p.UserId).IsUnique();
            profile.Property(p => p.Version).IsConcurrencyToken();
            profile.HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<OwnedCard>(card =>
        {
            card.HasKey(c => c.Id);
            card.Property(c => c.CardId).HasMaxLength(20).IsRequired();
            card.HasIndex(c => new { c.UserId, c.CardId }).IsUnique();
            card.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<LedgerEntry>(entry =>
        {
            entry.HasKey(e => e.Id);
            entry.Property(e => e.Kind).HasMaxLength(30).IsRequired();
            entry.Property(e => e.Detail).HasMaxLength(200);
            entry.HasIndex(e => new { e.UserId, e.CreatedUtc });
            entry.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<RefreshToken>(token =>
        {
            token.HasKey(t => t.Id);
            token.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
            token.HasIndex(t => t.TokenHash).IsUnique();
            token.HasOne(t => t.User).WithMany(u => u.RefreshTokens).HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
