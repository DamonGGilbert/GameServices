using System.Security.Claims;
using GameServices.Api.Catalog;
using GameServices.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace GameServices.Api.Economy;

public record OwnedCardDto(string CardId, int Quantity);

public record ProfileResponse(int DuelPoints, int Version, int RewardsLeftToday, List<OwnedCardDto> Collection);

public record PackInfo(string Id, string Name, int Price, int CardCount);

public record OpenPackResponse(string PackId, int Spent, int DuelPoints, List<string> Cards);

public record DuelResultRequest(string Result);

public record RewardResponse(int Awarded, int DuelPoints, int RewardsLeftToday);

public record LedgerLine(string Kind, string Detail, int Amount, int BalanceAfter, DateTime CreatedUtc);

/// <summary>
/// THE SERVER-AUTHORITATIVE ECONOMY. The rule that makes online games cheat-resistant:
///
///     the client ASKS, the server DECIDES.
///
/// The game never says "give me this card" or "set my points to 5000". It says "open pack X".
/// The server checks the price, takes the points, rolls the cards with its own random numbers,
/// stores them and writes the ledger, all in one all-or-nothing database save.
/// </summary>
public static class EconomyEndpoints
{
    public const int WinReward = 300;
    public const int LossReward = 75;

    // A real duel takes minutes. These caps stop a hacked client from spamming "I won!".
    public static readonly TimeSpan MinTimeBetweenRewards = TimeSpan.FromSeconds(45);
    public const int MaxRewardsPerDay = 30;

    public static IEndpointRouteBuilder MapEconomyEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/card-game")
            .WithTags("Card game economy")
            .RequireAuthorization();

        group.MapGet("/profile", GetProfile);
        group.MapGet("/packs", () => Results.Ok(Packs.All.Select(p => new PackInfo(p.Id, p.Name, p.Price, p.CardCount))));
        group.MapPost("/packs/{packId}/open", OpenPack);
        group.MapPost("/duels/reward", ClaimDuelReward);
        group.MapGet("/ledger", GetLedger);

        return app;
    }

    // ------------------------------------------------------------------ profile

    private static async Task<IResult> GetProfile(ClaimsPrincipal principal, AppDbContext db, CardCatalog catalog, CancellationToken cancel)
    {
        if (!TryGetUserId(principal, out Guid userId)) return Results.Unauthorized();

        CardGameProfile profile = await GetOrCreateProfile(db, catalog, userId, cancel);
        return Results.Ok(await BuildProfile(db, profile, cancel));
    }

    private static async Task<ProfileResponse> BuildProfile(AppDbContext db, CardGameProfile profile, CancellationToken cancel)
    {
        List<OwnedCardDto> collection = await db.OwnedCards.AsNoTracking()
            .Where(c => c.UserId == profile.UserId && c.Quantity > 0)
            .OrderBy(c => c.CardId)
            .Select(c => new OwnedCardDto(c.CardId, c.Quantity))
            .ToListAsync(cancel);

        return new ProfileResponse(profile.DuelPoints, profile.Version, RewardsLeft(profile, DateTime.UtcNow), collection);
    }

    /// <summary>First visit: create the wallet and hand over the starter deck (written in the ledger).</summary>
    private static async Task<CardGameProfile> GetOrCreateProfile(AppDbContext db, CardCatalog catalog, Guid userId, CancellationToken cancel)
    {
        CardGameProfile? profile = await db.CardGameProfiles.FirstOrDefaultAsync(p => p.UserId == userId, cancel);

        if (profile != null)
        {
            return profile;
        }

        profile = new CardGameProfile { UserId = userId, DuelPoints = catalog.StarterDuelPoints };
        db.CardGameProfiles.Add(profile);

        foreach (StarterEntry entry in catalog.StarterDeck)
        {
            db.OwnedCards.Add(new OwnedCard { UserId = userId, CardId = entry.Id, Quantity = entry.Qty });
        }

        db.Ledger.Add(new LedgerEntry
        {
            UserId = userId,
            Kind = "starter",
            Detail = $"Starter deck of {catalog.StarterDeck.Sum(e => e.Qty)} cards",
            Amount = catalog.StarterDuelPoints,
            BalanceAfter = catalog.StarterDuelPoints
        });

        try
        {
            await db.SaveChangesAsync(cancel);
            return profile;
        }
        catch (DbUpdateException)
        {
            // Two first requests arrived together and the other one created it. Use theirs.
            db.ChangeTracker.Clear();
            return await db.CardGameProfiles.FirstAsync(p => p.UserId == userId, cancel);
        }
    }

    // ------------------------------------------------------------------ packs

    private static async Task<IResult> OpenPack(string packId, ClaimsPrincipal principal, AppDbContext db, CardCatalog catalog, CancellationToken cancel)
    {
        if (!TryGetUserId(principal, out Guid userId)) return Results.Unauthorized();

        PackDefinition? pack = Packs.Find(packId);

        if (pack == null)
        {
            return Results.Problem("There is no such pack.", statusCode: StatusCodes.Status404NotFound);
        }

        CardGameProfile profile = await GetOrCreateProfile(db, catalog, userId, cancel);

        if (profile.DuelPoints < pack.Price)
        {
            return Results.Problem($"Not enough Duel Points: {pack.Name} costs {pack.Price} and you have {profile.DuelPoints}.", statusCode: StatusCodes.Status400BadRequest);
        }

        List<string> cards = Packs.Roll(pack, catalog, Random.Shared);

        profile.DuelPoints -= pack.Price;
        profile.Version += 1;

        Dictionary<string, OwnedCard> owned = await db.OwnedCards
            .Where(c => c.UserId == userId)
            .ToDictionaryAsync(c => c.CardId, cancel);

        foreach (string cardId in cards)
        {
            if (owned.TryGetValue(cardId, out OwnedCard? row))
            {
                row.Quantity += 1;
            }
            else
            {
                row = new OwnedCard { UserId = userId, CardId = cardId, Quantity = 1 };
                owned[cardId] = row;
                db.OwnedCards.Add(row);
            }
        }

        db.Ledger.Add(new LedgerEntry { UserId = userId, Kind = "pack", Detail = pack.Name, Amount = -pack.Price, BalanceAfter = profile.DuelPoints });

        // ONE save = ONE database transaction: the points leave and the cards arrive together, or neither happens.
        try
        {
            await db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateException)
        {
            // Someone else changed this wallet at the same moment (a double-click, or a hacked client
            // firing many requests at once). Only one of them can win; the rest are refused.
            return Results.Problem("Your wallet changed while this was being processed. Please try again.", statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Ok(new OpenPackResponse(pack.Id, pack.Price, profile.DuelPoints, cards));
    }

    // ------------------------------------------------------------------ duel rewards

    private static async Task<IResult> ClaimDuelReward(DuelResultRequest request, ClaimsPrincipal principal, AppDbContext db, CardCatalog catalog, CancellationToken cancel)
    {
        if (!TryGetUserId(principal, out Guid userId)) return Results.Unauthorized();

        string result = (request?.Result ?? "").Trim().ToLowerInvariant();

        if (result is not ("win" or "loss" or "draw"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["result"] = new[] { "Result must be win, loss or draw." } });
        }

        // The amount is decided HERE. The client only says how the duel went, never how much to pay.
        int amount = result == "win" ? WinReward : LossReward;

        CardGameProfile profile = await GetOrCreateProfile(db, catalog, userId, cancel);
        DateTime now = DateTime.UtcNow;

        if (profile.LastRewardUtc != null && now - profile.LastRewardUtc.Value < MinTimeBetweenRewards)
        {
            return Results.Problem("That was too soon after your last reward. Duels take longer than that!", statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (RewardsLeft(profile, now) <= 0)
        {
            return Results.Problem("You've reached today's reward limit. Come back tomorrow!", statusCode: StatusCodes.Status429TooManyRequests);
        }

        int today = DayNumber(now);

        if (profile.RewardDay != today)
        {
            profile.RewardDay = today;
            profile.RewardsToday = 0;
        }

        profile.RewardsToday += 1;
        profile.LastRewardUtc = now;
        profile.DuelPoints += amount;
        profile.Version += 1;

        db.Ledger.Add(new LedgerEntry { UserId = userId, Kind = "duel-" + result, Detail = "Duel reward", Amount = amount, BalanceAfter = profile.DuelPoints });

        try
        {
            await db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateException)
        {
            return Results.Problem("Your wallet changed while this was being processed. Please try again.", statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Ok(new RewardResponse(amount, profile.DuelPoints, RewardsLeft(profile, now)));
    }

    // ------------------------------------------------------------------ ledger

    private static async Task<IResult> GetLedger(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!TryGetUserId(principal, out Guid userId)) return Results.Unauthorized();

        List<LedgerLine> lines = await db.Ledger.AsNoTracking()
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.CreatedUtc)
            .Take(50)
            .Select(e => new LedgerLine(e.Kind, e.Detail, e.Amount, e.BalanceAfter, e.CreatedUtc))
            .ToListAsync(cancel);

        return Results.Ok(lines);
    }

    // ------------------------------------------------------------------ helpers

    private static int DayNumber(DateTime utc) => (int)(utc.Date - DateTime.UnixEpoch.Date).TotalDays;

    private static int RewardsLeft(CardGameProfile profile, DateTime now)
    {
        return profile.RewardDay == DayNumber(now) ? Math.Max(0, MaxRewardsPerDay - profile.RewardsToday) : MaxRewardsPerDay;
    }

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        return Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out userId);
    }
}
