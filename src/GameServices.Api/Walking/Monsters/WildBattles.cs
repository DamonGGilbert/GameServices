using System.Security.Claims;
using GameServices.Api.Data;
using GameServices.Api.Social;
using GameServices.Api.Walking.Ranked;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Monsters;

public record WildBattleRequest(bool Won, int OpponentPower);

/// <param name="PowerChange">Power taken (a bite, +) or drained (a crystal, -).</param>
public record WildBattleResultDto(WalkingMonsterDto Monster, int PowerChange, int CrystalSteps, int BitesLeftToday, string Message);

/// <summary>
/// WILD BATTLES are played on the phone (there's nobody on the other side), so the phone says how one went:
///
///   POST /api/walking/monsters/{id}/wild   { won, opponentPower }
///
///   * A win takes a small BITE - 1% of the wild monster's power, for good - on the first 3 wins a day.
///   * A loss turns the monster to CRYSTAL: 3% of its power drained, walked back over 3,000 steps (PowerBattles).
///
/// The phone can't be watched, so what it says is kept small: a wild monster is always about as strong as yours, so
/// its power is taken as no more than a quarter above your monster's, and a crystal can't battle at all.
/// </summary>
public static class WildBattles
{
    /// <summary>A wild monster is matched to yours: its power is believed up to this much above your monster's.</summary>
    public const double MostAboveMine = 1.25;

    public static async Task<IResult> Report(Guid monsterId, WildBattleRequest request, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder,
        CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        if (request == null) return Results.Problem("Say how the battle went.", statusCode: StatusCodes.Status400BadRequest);

        WalkingMonster? monster = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == monsterId && m.OwnerId == me, cancel);
        if (monster == null) return Results.NotFound();
        if (PowerBattles.IsCrystal(monster))
            return Results.Problem("That monster is a crystal - walk to bring its power back before it battles.", statusCode: StatusCodes.Status409Conflict);

        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (profile.WildDay != today)
        {
            profile.WildDay = today;
            profile.WildBitesToday = 0;
        }

        MonsterBattleStats stats = RankedEndpoints.StatsOf(finder.Catalog, monster);
        int myPower = PowerBattles.PowerOf(stats);
        int powerChange = 0, crystalSteps = 0;
        string message;

        if (request.Won)
        {
            if (profile.WildBitesToday < PowerBattles.WildBitesPerDay)
            {
                int theirs = Math.Clamp(request.OpponentPower, 1, (int)Math.Ceiling(myPower * MostAboveMine));
                powerChange = PowerBattles.Share(theirs, PowerBattles.WildBiteShare);
                PowerBattles.Absorb(monster, stats, powerChange);
                profile.WildBitesToday++;
                message = $"A bite: +{powerChange} power, for good!";
            }
            else
            {
                message = "That's today's wild bites - wins still count.";
            }
        }
        else
        {
            powerChange = -PowerBattles.Share(myPower, PowerBattles.DefeatShare);
            PowerBattles.Crystallise(monster, -powerChange, PowerBattles.DefeatSteps);
            crystalSteps = PowerBattles.DefeatSteps;
            message = $"It turned to crystal. Walk {PowerBattles.DefeatSteps:N0} steps to bring its power back.";
        }

        await db.SaveChangesAsync(cancel);
        return Results.Ok(new WildBattleResultDto(MonsterEndpoints.ToDto(monster, null), powerChange, crystalSteps,
            Math.Max(0, PowerBattles.WildBitesPerDay - profile.WildBitesToday), message));
    }
}
