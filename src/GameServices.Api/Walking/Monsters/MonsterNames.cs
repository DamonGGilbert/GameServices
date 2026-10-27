using System.Security.Claims;
using System.Text.RegularExpressions;
using GameServices.Api.Data;
using GameServices.Api.Moderation;
using GameServices.Api.Social;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Monsters;

public record MonsterNameRequest(string? Name);

public record NameTagsDto(int NameTags, int Price, int Glimmer);

/// <summary>
/// MONSTER NAMES AND NAME TAGS:
///
///   GET  /api/walking/name-tags                    how many I have, and the price
///   POST /api/walking/name-tags/buy                one Name Tag for Glimmer
///   POST /api/walking/monsters/{id}/name           { name } - uses a Name Tag; an empty name goes back to the
///                                                  species name (free)
///
/// Names show to other walkers (leaderboards, challenges, trades), so they go through the word filter - and the
/// server keeps the name, so a renamed monster is renamed everywhere. Every walker starts with one free Name Tag.
/// A monster keeps its name when it's traded (its new walker can rename it with their own Name Tag).
/// </summary>
public static partial class MonsterNames
{
    public const int NameTagPrice = 150;
    public const int MaxNameTags = 10;
    public const int MaxLength = 16;

    // Letters (any language), numbers, spaces, apostrophes and hyphens.
    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} '\-]*$")]
    private static partial Regex NameRule();

    public static RouteGroupBuilder MapMonsterNameEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/name-tags", Get);
        group.MapPost("/name-tags/buy", Buy);
        group.MapPost("/monsters/{monsterId:guid}/name", Rename);
        return group;
    }

    /// <summary>Null when the name is fine, otherwise what's wrong with it (shown to the player).</summary>
    public static string? Problem(string name)
    {
        if (name.Length > MaxLength) return $"Names can be at most {MaxLength} characters.";
        if (!NameRule().IsMatch(name)) return "Names can use letters, numbers, spaces, ' and -.";
        if (!ContentFilter.IsCleanName(name)) return "That name isn't allowed - please pick another.";
        return null;
    }

    private static async Task<IResult> Get(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        await db.SaveChangesAsync(cancel);
        return Results.Ok(new NameTagsDto(profile.NameTags, NameTagPrice, profile.Glimmer));
    }

    private static async Task<IResult> Buy(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        if (profile.NameTags >= MaxNameTags) return Results.Problem($"You can hold up to {MaxNameTags} Name Tags - use one first!", statusCode: StatusCodes.Status409Conflict);
        if (!WalkingWallet.TrySpend(db, profile, NameTagPrice, "nametag", "Bought a Name Tag"))
        {
            return Results.Problem($"A Name Tag costs {NameTagPrice} Glimmer.", statusCode: StatusCodes.Status400BadRequest);
        }

        profile.NameTags++;
        await db.SaveChangesAsync(cancel);
        return Results.Ok(new NameTagsDto(profile.NameTags, NameTagPrice, profile.Glimmer));
    }

    private static async Task<IResult> Rename(Guid monsterId, MonsterNameRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        WalkingMonster? monster = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == monsterId && m.OwnerId == me, cancel);
        if (monster == null) return Results.NotFound();

        // Collapse runs of spaces, so "Big    Sparky" can't be used to dodge anything.
        string name = Regex.Replace((request?.Name ?? "").Trim(), @"\s+", " ");
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);

        if (name.Length > 0)
        {
            if (name == monster.Nickname) return Results.Ok(new NameTagsDto(profile.NameTags, NameTagPrice, profile.Glimmer));
            string? problem = Problem(name);
            if (problem != null) return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
            if (profile.NameTags <= 0) return Results.Problem("You need a Name Tag to rename a monster - get one in the shop.", statusCode: StatusCodes.Status400BadRequest);
            profile.NameTags--;
        }

        monster.Nickname = name;   // empty = back to its species name (free)
        await db.SaveChangesAsync(cancel);
        return Results.Ok(new NameTagsDto(profile.NameTags, NameTagPrice, profile.Glimmer));
    }
}
