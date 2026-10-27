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
    public DbSet<OpponentReward> OpponentRewards => Set<OpponentReward>();
    public DbSet<TicketGrant> TicketGrants => Set<TicketGrant>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<UserBlock> Blocks => Set<UserBlock>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<Guild> Guilds => Set<Guild>();
    public DbSet<DuelMatch> DuelMatches => Set<DuelMatch>();
    public DbSet<GuildMember> GuildMembers => Set<GuildMember>();
    public DbSet<GuildInvite> GuildInvites => Set<GuildInvite>();
    public DbSet<GuildMessage> GuildMessages => Set<GuildMessage>();
    public DbSet<GuildBoss> GuildBosses => Set<GuildBoss>();
    public DbSet<GuildWar> GuildWars => Set<GuildWar>();
    public DbSet<GuildWarDuel> GuildWarDuels => Set<GuildWarDuel>();
    public DbSet<BossContribution> BossContributions => Set<BossContribution>();
    public DbSet<PasswordResetCode> PasswordResetCodes => Set<PasswordResetCode>();
    public DbSet<UserAvatar> Avatars => Set<UserAvatar>();
    public DbSet<UserCosmeticImage> CosmeticImages => Set<UserCosmeticImage>();

    // The walking game.
    public DbSet<WalkingProfile> WalkingProfiles => Set<WalkingProfile>();
    public DbSet<WalkingDay> WalkingDays => Set<WalkingDay>();
    public DbSet<Cheer> Cheers => Set<Cheer>();
    public DbSet<StepRace> StepRaces => Set<StepRace>();
    public DbSet<GuildJourneyClaim> GuildJourneyClaims => Set<GuildJourneyClaim>();
    public DbSet<GuildStepRace> GuildStepRaces => Set<GuildStepRace>();
    public DbSet<WalkingMonster> WalkingMonsters => Set<WalkingMonster>();
    public DbSet<MonsterSpeciesCount> MonsterSpeciesCounts => Set<MonsterSpeciesCount>();
    public DbSet<WalkingLedgerEntry> WalkingLedger => Set<WalkingLedgerEntry>();
    public DbSet<MonsterOffer> MonsterOffers => Set<MonsterOffer>();
    public DbSet<WalkingRoute> WalkingRoutes => Set<WalkingRoute>();
    public DbSet<RankedEntry> RankedEntries => Set<RankedEntry>();
    public DbSet<RankedMatch> RankedMatches => Set<RankedMatch>();
    public DbSet<RankedSeasonRecord> RankedSeasons => Set<RankedSeasonRecord>();
    public DbSet<Challenge> Challenges => Set<Challenge>();

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
            user.Property(u => u.CosmeticsJson).HasMaxLength(2000).IsRequired();
            user.Property(u => u.ProfileJson).HasMaxLength(3000).IsRequired();

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

        model.Entity<OpponentReward>(row =>
        {
            row.HasKey(r => r.Id);
            row.Property(r => r.OpponentId).HasMaxLength(64).IsRequired();
            row.HasIndex(r => new { r.UserId, r.OpponentId }).IsUnique();
            row.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<TicketGrant>(grant =>
        {
            grant.HasKey(g => g.Id);
            grant.Property(g => g.Reason).HasMaxLength(64).IsRequired();
            grant.HasIndex(g => new { g.UserId, g.Reason }).IsUnique();
            grant.HasOne<User>().WithMany().HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Cascade);
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

        model.Entity<UserAvatar>(avatar =>
        {
            avatar.HasKey(a => a.UserId);
            avatar.Property(a => a.ContentType).HasMaxLength(20).IsRequired();
            avatar.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<UserCosmeticImage>(image =>
        {
            image.HasKey(i => new { i.UserId, i.Kind });
            image.Property(i => i.ContentType).HasMaxLength(20).IsRequired();
            image.HasOne<User>().WithMany().HasForeignKey(i => i.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<DuelMatch>(match =>
        {
            match.HasKey(m => m.Id);
            match.Property(m => m.RoomName).HasMaxLength(40);
            match.Property(m => m.Player0Name).HasMaxLength(20);
            match.Property(m => m.Player1Name).HasMaxLength(20);
            match.Property(m => m.Reason).HasMaxLength(200);
            match.HasIndex(m => m.EndedUtc);
            match.HasIndex(m => m.Player0Id);
            match.HasIndex(m => m.Player1Id);
        });

        model.Entity<Guild>(g =>
        {
            g.HasKey(x => x.Id);
            g.Property(x => x.Name).HasMaxLength(20).IsRequired();
            g.Property(x => x.NormalizedName).HasMaxLength(20).IsRequired();
            g.Property(x => x.Tag).HasMaxLength(4).IsRequired();
            g.Property(x => x.Description).HasMaxLength(140);

            // Existing rows (made before games were told apart) become card-game guilds.
            g.Property(x => x.GameId).HasMaxLength(40).IsRequired().HasDefaultValue(Infrastructure.GameIds.Yugioh);

            // Names are unique WITHIN a game: the walking game can have its own "Night Owls".
            g.HasIndex(x => new { x.GameId, x.NormalizedName }).IsUnique();
        });

        model.Entity<GuildMember>(m =>
        {
            m.HasKey(x => x.Id);
            m.Property(x => x.GameId).HasMaxLength(40).IsRequired().HasDefaultValue(Infrastructure.GameIds.Yugioh);

            // One guild per player PER GAME, guaranteed by the database even if two joins race.
            m.HasIndex(x => new { x.UserId, x.GameId }).IsUnique();
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

        model.Entity<GuildWar>(w =>
        {
            w.HasKey(x => x.Id);
            w.HasIndex(x => new { x.GuildAId, x.StartsUtc });
            w.HasIndex(x => new { x.GuildBId, x.StartsUtc });
            w.HasOne<Guild>().WithMany().HasForeignKey(x => x.GuildAId).OnDelete(DeleteBehavior.Cascade);
            w.HasOne<Guild>().WithMany().HasForeignKey(x => x.GuildBId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<GuildWarDuel>(d =>
        {
            d.HasKey(x => x.Id);

            // One war duel per player per war, guaranteed by the database.
            d.HasIndex(x => new { x.WarId, x.UserId }).IsUnique();
            d.HasOne<GuildWar>().WithMany().HasForeignKey(x => x.WarId).OnDelete(DeleteBehavior.Cascade);
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

        // ---------------- the walking game ----------------

        model.Entity<WalkingProfile>(p =>
        {
            p.HasKey(x => x.UserId);
            p.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<WalkingDay>(d =>
        {
            d.HasKey(x => x.Id);
            d.Property(x => x.Day).HasMaxLength(10).IsRequired();

            // One row per player per day, guaranteed by the database even if two reports race.
            d.HasIndex(x => new { x.UserId, x.Day }).IsUnique();
            d.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<WalkingMonster>(m =>
        {
            m.HasKey(x => x.Id);
            m.Property(x => x.SpeciesId).HasMaxLength(32).IsRequired();
            m.Property(x => x.Source).HasMaxLength(16).IsRequired();
            m.Property(x => x.BiomeId).HasMaxLength(32);
            m.Property(x => x.FoundDay).HasMaxLength(10).IsRequired();
            m.HasIndex(x => new { x.OwnerId, x.FoundDay });
            // Every serial number exists once per species, guaranteed by the database even if two finds race.
            m.HasIndex(x => new { x.SpeciesId, x.Serial }).IsUnique();
            m.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<WalkingLedgerEntry>(l =>
        {
            l.HasKey(x => x.Id);
            l.Property(x => x.Kind).HasMaxLength(24).IsRequired();
            l.Property(x => x.Detail).HasMaxLength(120);
            l.HasIndex(x => new { x.UserId, x.CreatedUtc });
            l.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<MonsterOffer>(o =>
        {
            o.HasKey(x => x.Id);
            o.HasIndex(x => new { x.ToUserId, x.Status });
            o.HasIndex(x => new { x.FromUserId, x.Status });
            o.HasIndex(x => new { x.MonsterId, x.Status });
            o.HasOne<WalkingMonster>().WithMany().HasForeignKey(x => x.MonsterId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<RankedEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.MonsterId).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<WalkingMonster>().WithMany().HasForeignKey(x => x.MonsterId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<WalkingMonster>().Property(x => x.Nickname).HasMaxLength(32);

        model.Entity<Challenge>(c =>
        {
            c.HasKey(x => x.Id);
            c.Property(x => x.FromSpeciesId).HasMaxLength(32);
            c.Property(x => x.ToSpeciesId).HasMaxLength(32);
            c.Property(x => x.FromNickname).HasMaxLength(32);
            c.Property(x => x.ToNickname).HasMaxLength(32);
            c.HasIndex(x => new { x.ToUserId, x.Status });
            c.HasIndex(x => new { x.FromUserId, x.Status });
            c.HasOne<User>().WithMany().HasForeignKey(x => x.FromUserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<RankedSeasonRecord>(r =>
        {
            r.HasKey(x => x.Id);
            r.Property(x => x.Season).HasMaxLength(7).IsRequired();
            r.HasIndex(x => new { x.UserId, x.Season }).IsUnique();
            r.HasIndex(x => new { x.Season, x.Points });
            r.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<RankedMatch>(m =>
        {
            m.Property(x => x.Season).HasMaxLength(7);
            m.HasKey(x => x.Id);
            m.Property(x => x.DefenderSpeciesId).HasMaxLength(32);
            m.HasIndex(x => new { x.AttackerId, x.Status });
            m.HasIndex(x => new { x.DefenderId, x.Status });
            m.HasOne<User>().WithMany().HasForeignKey(x => x.AttackerId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<WalkingRoute>(r =>
        {
            r.HasKey(x => x.Id);
            r.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            r.Property(x => x.Biome).HasMaxLength(32);
            r.HasIndex(x => new { x.UserId, x.Status });
            r.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<MonsterSpeciesCount>(c =>
        {
            c.HasKey(x => x.SpeciesId);
            c.Property(x => x.SpeciesId).HasMaxLength(32);
        });

        model.Entity<Cheer>(c =>
        {
            c.HasKey(x => x.Id);
            c.HasIndex(x => new { x.ToUserId, x.Id });
            c.HasIndex(x => new { x.FromUserId, x.ToUserId, x.SentUtc });
            c.HasOne<User>().WithMany().HasForeignKey(x => x.FromUserId).OnDelete(DeleteBehavior.Cascade);
            c.HasOne<User>().WithMany().HasForeignKey(x => x.ToUserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<StepRace>(r =>
        {
            r.HasKey(x => x.Id);
            r.HasIndex(x => new { x.ChallengerId, x.Status });
            r.HasIndex(x => new { x.OpponentId, x.Status });
            r.HasIndex(x => new { x.Status, x.EndsUtc });
            r.HasOne<User>().WithMany().HasForeignKey(x => x.ChallengerId).OnDelete(DeleteBehavior.Cascade);
            r.HasOne<User>().WithMany().HasForeignKey(x => x.OpponentId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<GuildJourneyClaim>(c =>
        {
            c.HasKey(x => x.Id);
            c.Property(x => x.WeekStart).HasMaxLength(10).IsRequired();

            // One reward per player per week, even if two claims arrive at once.
            c.HasIndex(x => new { x.UserId, x.WeekStart }).IsUnique();
            c.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<GuildStepRace>(r =>
        {
            r.HasKey(x => x.Id);
            r.HasIndex(x => x.StartsUtc);
            r.HasIndex(x => new { x.Status, x.EndsUtc });
            r.HasIndex(x => x.GuildAId);
            r.HasIndex(x => x.GuildBId);
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
