using System.Security.Claims;
using System.Security.Cryptography;
using GameServices.Api.Data;
using GameServices.Api.Social;
using GameServices.Api.Walking.Wallet;

namespace GameServices.Api.Walking.Monsters;

public record PackDto(string Id, string Name, string Description, int Price, List<string> Odds);

public record PackOpenRequest(string? Biome = null);

/// <summary>What came out: Trail Tonics (added on the phone), Glimmer back, and maybe a sealed monster card.</summary>
public record PackResult(string PackId, int Tonics, int GlimmerBack, bool Monster, int Glimmer, string Message);

/// <summary>
/// CARD PACKS - bought with Glimmer earned by walking, never real money.
///
///   GET  /api/walking/packs              the packs, their prices and their exact odds
///   POST /api/walking/packs/{id}/open    buy and open one
///
/// Packs are mostly useful things (Trail Tonics for battles, some Glimmer back) with a small chance of a sealed
/// monster card - rolled here on the server, just like a walking find, so packs never make monsters common.
/// The odds shown are the odds used: they're written once, below, and both the list and the dice read them.
/// At most three packs a day.
/// </summary>
public static class WalkingPacks
{
    public const int MaxPacksPerDay = 3;

    private sealed record Pack(string Id, string Name, string Description, int Price, int Tonics, double ExtraTonicChance,
        int GlimmerBackMin, int GlimmerBackMax, double MonsterChance, int MonsterMinTier);

    private static readonly Pack[] Packs =
    {
        new("trail", "Trail Pack", "A handy pack for the road.", 250, 1, 0.40, 25, 100, 0.06, 0),
        new("explorer", "Explorer Pack", "For the adventurous. A better chance of a monster - and never a Common one.", 600, 2, 0.50, 50, 200, 0.15, 1),
    };

    public static RouteGroupBuilder MapWalkingPackEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/packs", () => Results.Ok(Packs.Select(ToDto).ToList()));
        group.MapPost("/packs/{packId}/open", Open);
        return group;
    }

    private static PackDto ToDto(Pack p) => new(p.Id, p.Name, p.Description, p.Price, new List<string>
    {
        p.Tonics + (p.Tonics == 1 ? " Trail Tonic" : " Trail Tonics") + ", plus a " + Percent(p.ExtraTonicChance) + " chance of one more",
        p.GlimmerBackMin + "-" + p.GlimmerBackMax + " Glimmer back",
        Percent(p.MonsterChance) + " chance of a sealed monster card" + (p.MonsterMinTier > 0 ? " (Uncommon or rarer)" : ""),
    });

    private static string Percent(double chance) => (chance * 100).ToString("0.#") + "%";

    private static async Task<IResult> Open(string packId, PackOpenRequest? request, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        Pack? pack = Packs.FirstOrDefault(p => p.Id == packId);
        if (pack == null) return Results.NotFound();

        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);

        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (profile.PacksDay != today)
        {
            profile.PacksDay = today;
            profile.PacksToday = 0;
        }

        if (profile.PacksToday >= MaxPacksPerDay)
        {
            return Results.Problem("That's three packs today - the rest of the fun is out on a walk! Packs are back tomorrow.", statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (!WalkingWallet.TrySpend(db, profile, pack.Price, "pack", pack.Name))
        {
            return Results.Problem($"You need {pack.Price:N0} Glimmer (you have {profile.Glimmer:N0}). Walking earns Glimmer!", statusCode: StatusCodes.Status400BadRequest);
        }

        profile.PacksToday++;
        int tonics = pack.Tonics + (Roll() < pack.ExtraTonicChance ? 1 : 0);
        int glimmerBack = RandomNumberGenerator.GetInt32(pack.GlimmerBackMin, pack.GlimmerBackMax + 1);
        WalkingWallet.Change(db, profile, glimmerBack, "pack", pack.Name + " - Glimmer back");

        bool monster = Roll() < pack.MonsterChance;
        if (monster)
        {
            string? biome = string.IsNullOrWhiteSpace(request?.Biome) ? null : request!.Biome!.Trim().ToLowerInvariant();
            (string species, int tier, long seed, int potential) = finder.Roll(biome, pack.MonsterMinTier);
            await MonsterEndpoints.Add(db, me, species, tier, seed, potential, finder.Catalog.RulesVersion, "pack", biome, today, cancel);
        }

        await db.SaveChangesAsync(cancel);

        string message = monster
            ? "A sealed monster card was inside!"
            : $"{tonics} Trail Tonic{(tonics == 1 ? "" : "s")} and {glimmerBack} Glimmer back.";
        return Results.Ok(new PackResult(pack.Id, tonics, glimmerBack, monster, profile.Glimmer, message));
    }

    private static double Roll() => RandomNumberGenerator.GetInt32(0, int.MaxValue) / (double)int.MaxValue;
}
