using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Walking;
using GameServices.Api.Walking.Monsters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// MONSTER SLOTS: a small group each - 6 to start, up to 12 as the walker levels up in the game. A full team finds
/// nothing new by walking and can't buy or be offered another; someone already over the limit keeps every monster.
/// </summary>
public class WalkingMonsterSlotsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public WalkingMonsterSlotsTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer(int glimmer = 0)
    {
        HttpClient client = factory.CreateClient();
        string name = "q" + Guid.NewGuid().ToString("N")[..10];
        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        await client.GetAsync("/api/walking/monsters");   // the starter
        var player = new Player(client, auth.User.Id, name);
        await WithDb(async db =>
        {
            WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, player.Id, CancellationToken.None);
            profile.WalletStarted = true;
            profile.Glimmer = glimmer;
        });
        return player;
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

    // Fills a walker's team up to this many monsters (copies of their starter, settled in).
    private Task FillTo(Player p, int count) => WithDb(async db =>
    {
        WalkingMonster starter = await db.WalkingMonsters.FirstAsync(m => m.OwnerId == p.Id);
        starter.AcquiredUtc = DateTime.UtcNow.AddDays(-5);
        int have = await db.WalkingMonsters.CountAsync(m => m.OwnerId == p.Id);
        for (int i = have; i < count; i++)
        {
            db.WalkingMonsters.Add(new WalkingMonster
            {
                OwnerId = p.Id, SpeciesId = starter.SpeciesId, RarityTier = starter.RarityTier, Seed = starter.Seed + i, Potential = starter.Potential,
                RulesVersion = starter.RulesVersion, Serial = Random.Shared.Next(100000, int.MaxValue), Source = "walk", FoundDay = starter.FoundDay,
                FoundUtc = DateTime.UtcNow.AddDays(-5), AcquiredUtc = DateTime.UtcNow.AddDays(-5), Seen = true,
            });
        }
    });

    private Task<int> SlotsOf(Player p) => FromDb(db => db.WalkingProfiles.Where(x => x.UserId == p.Id).Select(x => x.MonsterSlots).FirstAsync());

    private static Task<HttpResponseMessage> SendSlots(Player p, int slots) =>
        p.Http.PutAsJsonAsync("/api/walking/monsters/progress", new ProgressRequest(null, new List<MonsterProgressDto>(), slots));

    [Fact]
    public async Task TheGameSaysHowManySlots_KeptBetweenSixAndTwelve()
    {
        Player p = await NewPlayer();
        Assert.Equal(MonsterSlots.Starting, await SlotsOf(p));
        Assert.Equal(HttpStatusCode.NoContent, (await SendSlots(p, 8)).StatusCode);
        Assert.Equal(8, await SlotsOf(p));
        await SendSlots(p, 99);
        Assert.Equal(MonsterSlots.Most, await SlotsOf(p));
        await SendSlots(p, 1);
        Assert.Equal(MonsterSlots.Starting, await SlotsOf(p));
    }

    [Fact]
    public async Task AFullTeam_CantBuyOnTheMarket_UntilItHasAnotherSlot()
    {
        Player seller = await NewPlayer();
        Player buyer = await NewPlayer(glimmer: 5000);
        await FillTo(buyer, MonsterSlots.Starting);
        WalkingMonsterDto monster = (await seller.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0];
        await WithDb(async db => (await db.WalkingMonsters.FirstAsync(m => m.Id == monster.Id)).AcquiredUtc = DateTime.UtcNow.AddDays(-5));
        int price = MonsterOffers.MinPrice(monster.RarityTier);
        HttpResponseMessage listed = await seller.Http.PostAsJsonAsync("/api/walking/market", new ListRequest(monster.Id, price, price, 24));
        MarketListingDto listing = (await listed.Content.ReadFromJsonAsync<MarketListingDto>())!;

        HttpResponseMessage refused = await buyer.Http.PostAsync($"/api/walking/market/{listing.Id}/buy", null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("team is full", await refused.Content.ReadAsStringAsync());

        await SendSlots(buyer, MonsterSlots.Starting + 1);   // levelled up: another slot
        Assert.Equal(HttpStatusCode.OK, (await buyer.Http.PostAsync($"/api/walking/market/{listing.Id}/buy", null)).StatusCode);
    }

    [Fact]
    public async Task AFullTeam_FindsNothingNewByWalking()
    {
        Player p = await NewPlayer();
        await FillTo(p, MonsterSlots.Starting);
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        MonsterFinder finder = scope.ServiceProvider.GetRequiredService<MonsterFinder>();
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, p.Id, CancellationToken.None);

        int found = await MonsterEndpoints.RollFinds(db, finder, p.Id, profile, 10_000_000, DateTime.UtcNow.ToString("yyyy-MM-dd"), null, CancellationToken.None);
        Assert.Equal(0, found);
    }

    [Fact]
    public async Task SomeoneOverTheLimit_KeepsEveryMonster()
    {
        Player p = await NewPlayer();
        await FillTo(p, MonsterSlots.Starting + 4);
        await SendSlots(p, MonsterSlots.Starting);
        Assert.Equal(MonsterSlots.Starting + 4, (await p.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))!.Count);
    }

    [Fact]
    public async Task Packs_AreGone()
    {
        Player p = await NewPlayer(glimmer: 5000);
        Assert.Equal(HttpStatusCode.NotFound, (await p.Http.GetAsync("/api/walking/packs")).StatusCode);
    }
}
