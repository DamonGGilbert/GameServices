using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GameServices.Api.Data;

/// <summary>
/// Entity Framework Core's view of the database: each DbSet is a table.
/// EF Core turns C# objects into rows and LINQ queries into SQL, so we never
/// build SQL strings by hand (which also prevents SQL injection).
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    // Used by the PostgreSQL version below.
    protected AppDbContext(DbContextOptions options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PlayerSave> PlayerSaves => Set<PlayerSave>();
    public DbSet<CardGameProfile> CardGameProfiles => Set<CardGameProfile>();
    public DbSet<OwnedCard> OwnedCards => Set<OwnedCard>();
    public DbSet<LedgerEntry> Ledger => Set<LedgerEntry>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<UserBlock> Blocks => Set<UserBlock>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<Guild> Guilds => Set<Guild>();
    public DbSet<GuildMember> GuildMembers => Set<GuildMember>();
    public DbSet<GuildInvite> GuildInvites => Set<GuildInvite>();
    public DbSet<GuildMessage> GuildMessages => Set<GuildMessage>();
    public DbSet<GuildBoss> GuildBosses => Set<GuildBoss>();
    public DbSet<BossContribution> BossContributions => Set<BossContribution>();
    public DbSet<PasswordResetCode> PasswordResetCodes => Set<PasswordResetCode>();

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

        model.Entity<Friendship>(f =>
        {
            f.HasKey(x => x.Id);
            f.HasIndex(x => new { x.UserAId, x.UserBId }).IsUnique();
            f.HasIndex(x => x.UserBId);
            f.HasOne<User>().WithMany().HasForeignKey(x => x.UserAId).OnDelete(DeleteBehavior.Cascade);
            f.HasOne<User>().WithMany().HasForeignKey(x => x.UserBId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<UserBlock>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.BlockerId, x.BlockedId }).IsUnique();
            b.HasIndex(x => x.BlockedId);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.BlockerId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.BlockedId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ChatMessage>(m =>
        {
            m.HasKey(x => x.Id);
            m.Property(x => x.Text).HasMaxLength(500).IsRequired();
            m.HasIndex(x => new { x.RecipientId, x.SenderId, x.Id });
            m.HasIndex(x => new { x.SenderId, x.SentUtc });
            m.HasOne<User>().WithMany().HasForeignKey(x => x.SenderId).OnDelete(DeleteBehavior.Cascade);
            m.HasOne<User>().WithMany().HasForeignKey(x => x.RecipientId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Guild>(g =>
        {
            g.HasKey(x => x.Id);
            g.Property(x => x.Name).HasMaxLength(20).IsRequired();
            g.Property(x => x.NormalizedName).HasMaxLength(20).IsRequired();
            g.Property(x => x.Tag).HasMaxLength(4).IsRequired();
            g.Property(x => x.Description).HasMaxLength(140);
            g.HasIndex(x => x.NormalizedName).IsUnique();
        });

        model.Entity<GuildMember>(m =>
        {
            m.HasKey(x => x.Id);

            // One guild per player, guaranteed by the database even if two joins race.
            m.HasIndex(x => x.UserId).IsUnique();
            m.HasIndex(x => x.GuildId);
            m.HasOne<Guild>().WithMany().HasForeignKey(x => x.GuildId).OnDelete(DeleteBehavior.Cascade);
            m.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<GuildInvite>(i =>
        {
            i.HasKey(x => x.Id);
            i.HasIndex(x => new { x.GuildId, x.UserId }).IsUnique();
            i.HasIndex(x => x.UserId);
            i.HasOne<Guild>().WithMany().HasForeignKey(x => x.GuildId).OnDelete(DeleteBehavior.Cascade);
            i.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<GuildMessage>(m =>
        {
            m.HasKey(x => x.Id);
            m.Property(x => x.Text).HasMaxLength(500).IsRequired();
            m.HasIndex(x => new { x.GuildId, x.Id });
            m.HasIndex(x => new { x.SenderId, x.SentUtc });
            m.HasOne<Guild>().WithMany().HasForeignKey(x => x.GuildId).OnDelete(DeleteBehavior.Cascade);
            m.HasOne<User>().WithMany().HasForeignKey(x => x.SenderId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<GuildBoss>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.BossKey).HasMaxLength(40).IsRequired();
            b.Property(x => x.Name).HasMaxLength(60).IsRequired();
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.GuildId, x.StartedUtc });

            // A guild can only have ONE active boss at a time (status 0 = Active). A "partial" unique index.
            b.HasIndex(x => x.GuildId).IsUnique().HasFilter("\"Status\" = 0").HasDatabaseName("IX_GuildBosses_OneActivePerGuild");
            b.HasOne<Guild>().WithMany().HasForeignKey(x => x.GuildId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<BossContribution>(c =>
        {
            c.HasKey(x => x.Id);
            c.HasIndex(x => new { x.BossId, x.UserId }).IsUnique();
            c.HasOne<GuildBoss>().WithMany().HasForeignKey(x => x.BossId).OnDelete(DeleteBehavior.Cascade);
            c.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<PasswordResetCode>(c =>
        {
            c.HasKey(x => x.Id);
            c.Property(x => x.CodeHash).HasMaxLength(64).IsRequired();
            c.HasIndex(x => new { x.UserId, x.CreatedUtc });
            c.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
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
