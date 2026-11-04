using System.Security.Claims;
using GameServices.Api.Data;
using GameServices.Api.Social;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Wallet;

public record WalletDto(int Glimmer, int StepsPerGlimmer, int Tonics = 0);

/// <summary>PhoneBalance: the Glimmer a phone has from before it signed in (taken at the first sync only).</summary>
public record WalletSyncRequest(int PhoneBalance);

/// <summary>
/// THE WALKING GAME'S GLIMMER, kept on the server (so packs and offers can't be paid for with made-up Glimmer).
///
///   GET  /api/walking/wallet        my balance
///   POST /api/walking/wallet/sync   the first time: bring across what a phone has from before it signed in
///
/// Where Glimmer comes from:
///   * walking - 1 Glimmer for every 100 steps the server accepts (added with each step report)
///   * milestones, quests, achievements, medals, streaks and finds on the map (Walking/Progress), and battles
///   * the Guild Journey reward (added when it's collected), and selling a monster
///   * the first sync brings the phone's existing balance across (up to 1,000). After that a phone can't add anything:
///     it only ever shows the balance kept here, so a hacked phone can't print Glimmer.
/// Every change is written to a ledger, so a balance can always be explained.
/// </summary>
public static class WalkingWallet
{
    public const int StepsPerGlimmer = 100;
    public const int StartingBalanceCap = 1000;

    public static RouteGroupBuilder MapWalkingWalletEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/wallet", Get);
        group.MapPost("/wallet/sync", Sync);
        return group;
    }

    private static async Task<IResult> Get(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        await db.SaveChangesAsync(cancel);
        return Results.Ok(ToDto(profile));
    }

    private static async Task<IResult> Sync(WalletSyncRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);

        if (!profile.WalletStarted)
        {
            // First time: the phone's balance comes across (it already includes anything just earned).
            profile.WalletStarted = true;
            int start = Math.Clamp(request?.PhoneBalance ?? 0, 0, StartingBalanceCap);
            if (start > 0) Change(db, profile, start, "start", "Glimmer brought over from this phone");
        }

        await db.SaveChangesAsync(cancel);
        return Results.Ok(ToDto(profile));
    }

    /// <summary>Glimmer for newly accepted steps (the part-way remainder carries over to the next report).</summary>
    public static void CreditSteps(AppDbContext db, WalkingProfile profile, int addedSteps)
    {
        if (addedSteps <= 0) return;
        profile.GlimmerStepRemainder += addedSteps;
        int glimmer = profile.GlimmerStepRemainder / StepsPerGlimmer;
        profile.GlimmerStepRemainder %= StepsPerGlimmer;
        if (glimmer > 0) Change(db, profile, glimmer, "steps", "Walking");
    }

    /// <summary>Adds (or, with a negative amount, takes) Glimmer and writes it in the ledger. Never goes below zero.</summary>
    public static void Change(AppDbContext db, WalkingProfile profile, int amount, string kind, string detail)
    {
        int before = profile.Glimmer;
        profile.Glimmer = Math.Max(0, before + amount);
        db.WalkingLedger.Add(new WalkingLedgerEntry
        {
            UserId = profile.UserId, Amount = profile.Glimmer - before, BalanceAfter = profile.Glimmer, Kind = kind, Detail = detail,
        });
    }

    /// <summary>Takes the full amount if it can; false (and nothing taken) if the walker can't afford it.</summary>
    public static bool TrySpend(AppDbContext db, WalkingProfile profile, int amount, string kind, string detail)
    {
        if (amount < 0 || profile.Glimmer < amount) return false;
        Change(db, profile, -amount, kind, detail);
        return true;
    }

    public static WalletDto ToDto(WalkingProfile p) => new(p.Glimmer, StepsPerGlimmer, p.Tonics);
}
