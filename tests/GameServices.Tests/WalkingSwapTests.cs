using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Social;
using GameServices.Api.Walking;
using GameServices.Api.Walking.Monsters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// THE DAILY SWAP: one of your monsters for one of a friend's, once a day - both agree, the server swaps them in one
/// step, and every safety rule holds (friends only, no companions, settled in first, one a day each).
/// </summary>
public class WalkingSwapTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public WalkingSwapTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer()
    {
        HttpClient client = factory.CreateClient();
        string name = "s" + Guid.NewGuid().ToString("N")[..10];
        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        return new Player(client, auth.User.Id, name);
    }

    private static async Task MakeFriends(Player a, Player b)
    {
        await a.Http.PostAsJsonAsync("/api/friends/requests", new UsernameRequest(b.Name));
        Assert.Equal(HttpStatusCode.NoContent, (await b.Http.PostAsync($"/api/friends/requests/{a.Id}/accept", null)).StatusCode);
    }

    private async Task WithDb(Func<AppDbContext, Task> change)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await change(db);
        await db.SaveChangesAsync();
    }

    private async Task<T> FromDb<T>(Func<AppDbContext, Task<T>> read)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    // A player's starter monster, settled in (been with them long enough to swap).
    private async Task<WalkingMonsterDto> SettledStarter(Player p)
    {
        WalkingMonsterDto starter = (await p.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0];
        await Settle(starter.Id);
        return starter;
    }

    private Task Settle(Guid monsterId) => WithDb(async db =>
    {
        WalkingMonster m = await db.WalkingMonsters.FirstAsync(x => x.Id == monsterId);
        m.AcquiredUtc = DateTime.UtcNow.AddDays(-4);
        m.FoundUtc = DateTime.UtcNow.AddDays(-4);
    });

    private static Task<HttpResponseMessage> Ask(Player asker, Guid mine, Guid theirs) =>
        asker.Http.PostAsJsonAsync("/api/walking/swaps", new SwapRequest(mine, theirs));

    private static async Task<SwapListDto> Swaps(Player p) => (await p.Http.GetFromJsonAsync<SwapListDto>("/api/walking/swaps"))!;

    private static async Task<SwapDto> AskOk(Player asker, Guid mine, Guid theirs)
    {
        HttpResponseMessage response = await Ask(asker, mine, theirs);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SwapDto>())!;
    }

    private static async Task<string> Refused(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private Task<Guid> OwnerOf(Guid monsterId) => FromDb(db => db.WalkingMonsters.Where(m => m.Id == monsterId).Select(m => m.OwnerId).FirstAsync());

    // ------------------------------------------------------------------ swapping

    [Fact]
    public async Task Friends_swap_monsters_when_both_agree()
    {
        Player a = await NewPlayer(), b = await NewPlayer();
        await MakeFriends(a, b);
        WalkingMonsterDto mine = await SettledStarter(a), theirs = await SettledStarter(b);

        SwapDto asked = await AskOk(a, mine.Id, theirs.Id);
        Assert.Equal("Pending", asked.Status);
        Assert.False(asked.Incoming);

        SwapDto seen = Assert.Single((await Swaps(b)).Swaps);
        Assert.True(seen.Incoming);
        Assert.Equal(theirs.Id, seen.Mine!.Id);     // from b's side: b gives their own monster...
        Assert.Equal(mine.Id, seen.Theirs!.Id);     // ...and gets a's

        HttpResponseMessage accepted = await b.Http.PostAsync($"/api/walking/swaps/{asked.Id}/accept", null);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("Done", (await accepted.Content.ReadFromJsonAsync<SwapDto>())!.Status);

        Assert.Equal(b.Id, await OwnerOf(mine.Id));
        Assert.Equal(a.Id, await OwnerOf(theirs.Id));
        WalkingMonster arrived = await FromDb(db => db.WalkingMonsters.FirstAsync(m => m.Id == theirs.Id));
        Assert.False(arrived.Seen);                 // a welcome reveal for its new walker
        Assert.Equal(b.Id, arrived.ArrivedFromUserId);
        Assert.Equal(1, arrived.TradeCount);
        Assert.NotNull((await Swaps(a)).NextSwapUtc);
        Assert.NotNull((await Swaps(b)).NextSwapUtc);
    }

    [Fact]
    public async Task Only_friends_can_swap()
    {
        Player a = await NewPlayer(), stranger = await NewPlayer();
        WalkingMonsterDto mine = await SettledStarter(a), theirs = await SettledStarter(stranger);
        Assert.Contains("friends", await Refused(await Ask(a, mine.Id, theirs.Id)));
    }

    [Fact]
    public async Task Companions_never_swap()
    {
        Player a = await NewPlayer(), b = await NewPlayer();
        await MakeFriends(a, b);
        WalkingMonsterDto mine = await SettledStarter(a), theirs = await SettledStarter(b);
        await WithDb(async db => (await WalkingEndpoints.GetOrCreateProfile(db, b.Id, CancellationToken.None)).CompanionMonsterId = theirs.Id);
        Assert.Contains("companion", await Refused(await Ask(a, mine.Id, theirs.Id)));

        await WithDb(async db => (await WalkingEndpoints.GetOrCreateProfile(db, a.Id, CancellationToken.None)).CompanionMonsterId = mine.Id);
        await WithDb(async db => (await WalkingEndpoints.GetOrCreateProfile(db, b.Id, CancellationToken.None)).CompanionMonsterId = null);
        Assert.Contains("your companion", await Refused(await Ask(a, mine.Id, theirs.Id)));
    }

    [Fact]
    public async Task A_new_arrival_settles_in_before_it_can_swap()
    {
        Player a = await NewPlayer(), b = await NewPlayer();
        await MakeFriends(a, b);
        WalkingMonsterDto mine = (await a.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0];
        WalkingMonsterDto theirs = await SettledStarter(b);
        Assert.Contains("settling in", await Refused(await Ask(a, mine.Id, theirs.Id)));
        Assert.Contains(mine.Id, (await Swaps(a)).SettlingMonsterIds);
    }

    [Fact]
    public async Task One_swap_a_day_each()
    {
        Player a = await NewPlayer(), b = await NewPlayer(), c = await NewPlayer();
        await MakeFriends(a, b);
        await MakeFriends(a, c);
        WalkingMonsterDto aStarter = await SettledStarter(a), bStarter = await SettledStarter(b), cStarter = await SettledStarter(c);

        SwapDto first = await AskOk(a, aStarter.Id, bStarter.Id);
        Assert.Equal(HttpStatusCode.OK, (await b.Http.PostAsync($"/api/walking/swaps/{first.Id}/accept", null)).StatusCode);
        await Settle(bStarter.Id);   // now a's, and settled - so only the daily limit stands in the way

        Assert.Contains("today's swap", await Refused(await Ask(a, bStarter.Id, cStarter.Id)));

        // c can ask, but a can't accept until a day has passed.
        SwapDto second = await AskOk(c, cStarter.Id, bStarter.Id);
        Assert.Contains("today's swap", await Refused(await a.Http.PostAsync($"/api/walking/swaps/{second.Id}/accept", null)));

        await WithDb(async db => (await db.MonsterSwaps.FirstAsync(s => s.Id == first.Id)).DecidedUtc = DateTime.UtcNow.AddHours(-25));
        Assert.Null((await Swaps(a)).NextSwapUtc);
        Assert.Equal(HttpStatusCode.OK, (await a.Http.PostAsync($"/api/walking/swaps/{second.Id}/accept", null)).StatusCode);
        Assert.Equal(a.Id, await OwnerOf(cStarter.Id));
    }

    [Fact]
    public async Task Requests_can_be_declined_or_cancelled_by_the_right_walker_only()
    {
        Player a = await NewPlayer(), b = await NewPlayer();
        await MakeFriends(a, b);
        WalkingMonsterDto mine = await SettledStarter(a), theirs = await SettledStarter(b);

        SwapDto asked = await AskOk(a, mine.Id, theirs.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Http.PostAsync($"/api/walking/swaps/{asked.Id}/accept", null)).StatusCode);   // not your own
        Assert.Equal(HttpStatusCode.NotFound, (await b.Http.PostAsync($"/api/walking/swaps/{asked.Id}/cancel", null)).StatusCode);   // not b's to cancel
        Assert.Equal("Declined", (await (await b.Http.PostAsync($"/api/walking/swaps/{asked.Id}/decline", null)).Content.ReadFromJsonAsync<SwapDto>())!.Status);

        SwapDto again = await AskOk(a, mine.Id, theirs.Id);
        Assert.Equal("Cancelled", (await (await a.Http.PostAsync($"/api/walking/swaps/{again.Id}/cancel", null)).Content.ReadFromJsonAsync<SwapDto>())!.Status);
        Assert.Contains("isn't open", await Refused(await b.Http.PostAsync($"/api/walking/swaps/{again.Id}/accept", null)));
        Assert.Equal(a.Id, await OwnerOf(mine.Id));   // nothing moved
    }

    [Fact]
    public async Task Each_monster_is_in_one_request_at_a_time_and_requests_expire()
    {
        Player a = await NewPlayer(), b = await NewPlayer();
        await MakeFriends(a, b);
        WalkingMonsterDto mine = await SettledStarter(a), theirs = await SettledStarter(b);

        SwapDto asked = await AskOk(a, mine.Id, theirs.Id);
        Assert.Contains("already in one of your swap requests", await Refused(await Ask(a, mine.Id, theirs.Id)));

        await WithDb(async db => (await db.MonsterSwaps.FirstAsync(s => s.Id == asked.Id)).ExpiresUtc = DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal("Expired", Assert.Single((await Swaps(b)).Swaps).Status);
        await AskOk(a, mine.Id, theirs.Id);   // free to ask again
    }

    [Fact]
    public async Task A_swap_ends_ranked_places_and_closes_other_offers_and_requests_for_both_monsters()
    {
        Player a = await NewPlayer(), b = await NewPlayer(), c = await NewPlayer();
        await MakeFriends(a, b);
        await MakeFriends(c, b);
        await MakeFriends(c, a);
        WalkingMonsterDto aStarter = await SettledStarter(a), bStarter = await SettledStarter(b), cStarter = await SettledStarter(c);

        // b's monster is on b's ranked team, c has offered Glimmer for it, and c has also asked a for a swap.
        await WithDb(async db =>
        {
            db.RankedEntries.Add(new RankedEntry { UserId = b.Id, MonsterId = bStarter.Id, Slot = 0 });
            WalkingProfile cProfile = await WalkingEndpoints.GetOrCreateProfile(db, c.Id, CancellationToken.None);
            cProfile.WalletStarted = true;
            cProfile.Glimmer = 200000;
        });
        int price = MonsterOffers.MinPrice(bStarter.RarityTier);
        Assert.Equal(HttpStatusCode.OK, (await c.Http.PostAsJsonAsync("/api/walking/offers", new OfferRequest(bStarter.Id, price))).StatusCode);
        SwapDto cAsksA = await AskOk(c, cStarter.Id, aStarter.Id);

        SwapDto swap = await AskOk(a, aStarter.Id, bStarter.Id);
        Assert.Equal(HttpStatusCode.OK, (await b.Http.PostAsync($"/api/walking/swaps/{swap.Id}/accept", null)).StatusCode);

        Assert.False(await FromDb(db => db.RankedEntries.AnyAsync(e => e.MonsterId == bStarter.Id)));
        Assert.Equal(OfferStatus.Declined, await FromDb(db => db.MonsterOffers.Where(o => o.MonsterId == bStarter.Id).Select(o => o.Status).FirstAsync()));
        Assert.Equal(200000, await FromDb(db => db.WalkingProfiles.Where(p => p.UserId == c.Id).Select(p => p.Glimmer).FirstAsync()));   // refunded
        Assert.Equal("Closed", (await Swaps(c)).Swaps.First(s => s.Id == cAsksA.Id).Status);
    }
}
