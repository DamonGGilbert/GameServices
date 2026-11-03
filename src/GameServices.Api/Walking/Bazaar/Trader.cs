using GameServices.Api.Data;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Ranked;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Bazaar;

/// <summary>
/// JUNIPER THE TRADER buys monsters from walkers and sells them on.
///
///   * She pays by rarity plus a little for power: the lowest offer price for its rarity, plus 1 Glimmer for every 20
///     power. She sells for 40% more than that.
///   * Each walker can sell her 3 and buy 3 a day. Buying needs room on your team (MonsterSlots).
///   * She won't take your companion, a crystal (walk its power back first) or a monster on the Market.
///   * Her stall is the monsters walkers sold her, and each day she brings a couple of fresh finds of her own if it's
///     looking bare.
///
/// The monsters on her stall belong to her own account (a walker nobody can sign in as), so a monster bought from her
/// arrives the same way as any other - "a new friend from Juniper the Trader" - keeping its level, training and story.
/// </summary>
public static class Trader
{
    public static readonly Guid Id = Guid.Parse("4a756e69-7065-4072-8000-000000000001");
    public const string Name = "Juniper the Trader";

    public const int SellsPerDay = 3;
    public const int BuysPerDay = 3;
    public const double Markup = 1.4;

    /// <summary>She tops her stall up to this many with fresh finds (no more than FreshPerDay of them a day).</summary>
    public const int StallSize = 4;
    public const int FreshPerDay = 2;

    /// <summary>What she pays for a monster: the lowest offer price for its rarity, plus 1 for every 20 power.</summary>
    public static int BuyingPrice(MonsterCatalog catalog, WalkingMonster m) =>
        MonsterOffers.MinPrice(m.RarityTier) + PowerBattles.PowerOf(RankedEndpoints.StatsOf(catalog, m)) / 20;

    /// <summary>What she asks for one on her stall.</summary>
    public static int SellingPrice(MonsterCatalog catalog, WalkingMonster m) => (int)Math.Round(BuyingPrice(catalog, m) * Markup);

    /// <summary>Makes her account the first time it's needed (no password: nobody can ever sign in as her).</summary>
    public static async Task EnsureAccountAsync(AppDbContext db, CancellationToken cancel)
    {
        if (await db.Users.AnyAsync(u => u.Id == Id, cancel)) return;
        db.Users.Add(new User { Id = Id, Username = Name, NormalizedUsername = Name.ToUpperInvariant(), PasswordHash = "" });
        try
        {
            await db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();   // made at the same moment by another request
        }
    }

    /// <summary>A bare stall gets a couple of fresh finds, once a day.</summary>
    public static async Task RestockAsync(AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        int stock = await db.WalkingMonsters.CountAsync(m => m.OwnerId == Id, cancel);
        if (stock >= StallSize) return;
        if (await db.WalkingMonsters.AnyAsync(m => m.OwnerId == Id && m.Source == "trader" && m.FoundDay == today, cancel)) return;

        for (int i = 0; i < Math.Min(FreshPerDay, StallSize - stock); i++)
        {
            (string species, int tier, long seed, int potential) = finder.Roll(null);
            await MonsterEndpoints.Add(db, Id, species, tier, seed, potential, finder.Catalog.RulesVersion, "trader", null, today, cancel);
        }

        try
        {
            await db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();   // another request stocked up at the same moment
        }
    }
}
