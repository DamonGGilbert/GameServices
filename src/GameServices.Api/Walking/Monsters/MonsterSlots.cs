using GameServices.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Monsters;

/// <summary>
/// MONSTER SLOTS: a walker keeps a small group of monsters - 6 to start, and one more slot as they level up (up to 12) -
/// so they get attached to a few rather than collecting thousands.
///
/// Their level is the server's (Walking/Progress/Explorer), and so is their slot count, which it checks wherever a monster
/// could arrive: walking finds, map finds, rewards, offers and the Market. A walker who already
/// has more (from before slots) keeps every one of them - they just can't get another until there's room.
/// </summary>
public static class MonsterSlots
{
    public const int Starting = 6;
    public const int Most = 12;

    public const string FullMessage = "Your team is full - level up for another slot, or find one of yours a new home first.";

    public static int Of(WalkingProfile profile) => Math.Clamp(profile.MonsterSlots, Starting, Most);

    /// <summary>True if this walker has a free slot for one more monster.</summary>
    public static async Task<bool> HasRoomAsync(AppDbContext db, Guid user, CancellationToken cancel)
    {
        WalkingProfile profile = db.WalkingProfiles.Local.FirstOrDefault(p => p.UserId == user) ?? await WalkingEndpoints.GetOrCreateProfile(db, user, cancel);
        int owned = await db.WalkingMonsters.CountAsync(m => m.OwnerId == user, cancel);
        int arriving = db.ChangeTracker.Entries<WalkingMonster>().Count(e => e.State == EntityState.Added && e.Entity.OwnerId == user);   // not saved yet
        return owned + arriving < Of(profile);
    }
}
