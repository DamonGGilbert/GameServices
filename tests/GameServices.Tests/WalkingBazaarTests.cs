using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Walking;
using GameServices.Api.Walking.Bazaar;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Ranked;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// THE BAZAAR (Juniper the Trader, the Monster Maker's card styles, the Tonic Stand) and POWER from ranked and wild
/// battles: a winning attacker takes a bite, a beaten one turns to crystal; wild wins take a small bite, 3 a day.
/// </summary>
public class WalkingBazaarTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public WalkingBazaarTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer(int glimmer = 0)
    {
        HttpClient client = factory.CreateClient();
        string name = "z" + Guid.NewGuid().ToString("N")[..10];
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

    private MonsterCatalog Catalog => factory.Services.GetRequiredService<MonsterFinder>().Catalog;

    // A second monster (a copy of the starter), so the starter can stay the companion.
    private async Task<Guid> Extra(Player p)
    {
        Guid id = Guid.Empty;
        await WithDb(async db =>
        {
            WalkingMonster starter = await db.WalkingMonsters.FirstAsync(m => m.OwnerId == p.Id);
            var m = new WalkingMonster
            {
                OwnerId = p.Id, SpeciesId = starter.SpeciesId, RarityTier = 1, Seed = starter.Seed + 7, Potential = starter.Potential,
                RulesVersion = starter.RulesVersion, Serial = Random.Shared.Next(100000, int.MaxValue), Source = "walk", FoundDay = starter.FoundDay,
                Level = 20, Seen = true,
            };
            db.WalkingMonsters.Add(m);
            WalkingProfile profile = await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id);
            profile.CompanionMonsterId = starter.Id;
            id = m.Id;
        });
        return id;
    }

    private static async Task<BazaarDto> Ok(HttpResponseMessage r)
    {
        Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<BazaarDto>())!;
    }

    private static Task<BazaarDto> Bazaar(Player p) => p.Http.GetFromJsonAsync<BazaarDto>("/api/walking/bazaar")!;

    // ------------------------------------------------------------------ Juniper the Trader

    [Fact]
    public async Task Juniper_buys_a_monster_for_its_price_but_never_your_companion()
    {
        Player p = await NewPlayer();
        Guid extra = await Extra(p);
        BazaarDto bazaar = await Bazaar(p);
        TraderQuoteDto quote = bazaar.Quotes.Single(q => q.MonsterId == extra);
        Assert.Null(quote.WhyNot);
        TraderQuoteDto companion = bazaar.Quotes.Single(q => q.MonsterId != extra);
        Assert.NotNull(companion.WhyNot);

        WalkingMonster before = await FromDb(db => db.WalkingMonsters.AsNoTracking().FirstAsync(m => m.Id == extra));
        Assert.Equal(MonsterOffers.MinPrice(1) + PowerBattles.PowerOf(RankedEndpoints.StatsOf(Catalog, before)) / 20, quote.Price);

        BazaarDto sold = await Ok(await p.Http.PostAsync($"/api/walking/bazaar/trader/{extra}/sell", null));
        Assert.Equal(quote.Price, sold.Glimmer);
        Assert.Equal(Trader.SellsPerDay - 1, sold.SellsLeft);
        Assert.Equal(Trader.Id, await FromDb(db => db.WalkingMonsters.Where(m => m.Id == extra).Select(m => m.OwnerId).FirstAsync()));
        TraderStockDto onStall = sold.Stock.Single(s => s.Monster.Id == extra);
        Assert.Equal((int)Math.Round(quote.Price * Trader.Markup), onStall.Price);

        HttpResponseMessage refused = await p.Http.PostAsync($"/api/walking/bazaar/trader/{companion.MonsterId}/sell", null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Juniper_buys_three_a_day_from_each_walker()
    {
        Player p = await NewPlayer();
        var extras = new List<Guid>();
        for (int i = 0; i < Trader.SellsPerDay + 1; i++) extras.Add(await Extra(p));
        for (int i = 0; i < Trader.SellsPerDay; i++) await Ok(await p.Http.PostAsync($"/api/walking/bazaar/trader/{extras[i]}/sell", null));
        HttpResponseMessage fourth = await p.Http.PostAsync($"/api/walking/bazaar/trader/{extras[^1]}/sell", null);
        Assert.Equal(HttpStatusCode.BadRequest, fourth.StatusCode);
        Assert.Contains("tomorrow", await fourth.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Buying_from_Juniper_costs_Glimmer_and_the_monster_arrives_from_her()
    {
        Player seller = await NewPlayer();
        Guid extra = await Extra(seller);
        await Ok(await seller.Http.PostAsync($"/api/walking/bazaar/trader/{extra}/sell", null));

        Player poor = await NewPlayer(glimmer: 1);
        HttpResponseMessage cantAfford = await poor.Http.PostAsync($"/api/walking/bazaar/trader/{extra}/buy", null);
        Assert.Equal(HttpStatusCode.BadRequest, cantAfford.StatusCode);

        Player buyer = await NewPlayer(glimmer: 50_000);
        int price = (await Bazaar(buyer)).Stock.Single(s => s.Monster.Id == extra).Price;
        BazaarDto bought = await Ok(await buyer.Http.PostAsync($"/api/walking/bazaar/trader/{extra}/buy", null));
        Assert.Equal(50_000 - price, bought.Glimmer);
        Assert.DoesNotContain(bought.Stock, s => s.Monster.Id == extra);

        List<WalkingMonsterDto> mine = (await buyer.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))!;
        WalkingMonsterDto arrived = mine.Single(m => m.Id == extra);
        Assert.Equal(Trader.Name, arrived.ArrivedFrom);

        HttpResponseMessage gone = await poor.Http.PostAsync($"/api/walking/bazaar/trader/{extra}/buy", null);
        Assert.Equal(HttpStatusCode.Conflict, gone.StatusCode);
    }

    [Fact]
    public async Task A_full_team_cant_buy_from_Juniper()
    {
        Player p = await NewPlayer(glimmer: 100_000);
        await WithDb(async db =>
        {
            WalkingMonster starter = await db.WalkingMonsters.FirstAsync(m => m.OwnerId == p.Id);
            for (int i = 1; i < MonsterSlots.Starting; i++)
                db.WalkingMonsters.Add(new WalkingMonster
                {
                    OwnerId = p.Id, SpeciesId = starter.SpeciesId, Seed = starter.Seed + i, Potential = 50, RulesVersion = starter.RulesVersion,
                    Serial = Random.Shared.Next(100000, int.MaxValue), Source = "walk", FoundDay = starter.FoundDay, Seen = true,
                });
        });
        BazaarDto bazaar = await Bazaar(p);   // a bare stall gets fresh finds
        Assert.False(bazaar.HasRoom);
        Assert.NotEmpty(bazaar.Stock);
        HttpResponseMessage refused = await p.Http.PostAsync($"/api/walking/bazaar/trader/{bazaar.Stock[0].Monster.Id}/buy", null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("team is full", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_bare_stall_gets_fresh_finds_once_a_day()
    {
        Player p = await NewPlayer();
        await WithDb(async db => db.WalkingMonsters.RemoveRange(await db.WalkingMonsters.Where(m => m.OwnerId == Trader.Id).ToListAsync()));
        Assert.Equal(Trader.FreshPerDay, (await Bazaar(p)).Stock.Count);
        Assert.Equal(Trader.FreshPerDay, (await Bazaar(p)).Stock.Count);   // not again today
    }

    // ------------------------------------------------------------------ the Monster Maker

    [Fact]
    public async Task A_card_style_is_bought_once_and_worn_by_a_monster()
    {
        Player p = await NewPlayer(glimmer: 1000);
        Guid monster = (await p.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0].Id;
        CardStyles.Style style = CardStyles.Find("ocean")!;

        HttpResponseMessage notYet = await p.Http.PostAsJsonAsync("/api/walking/bazaar/styles/wear", new WearStyleRequest(monster, style.Id));
        Assert.Equal(HttpStatusCode.BadRequest, notYet.StatusCode);

        BazaarDto bought = await Ok(await p.Http.PostAsync($"/api/walking/bazaar/styles/{style.Id}/buy", null));
        Assert.Equal(1000 - style.Price, bought.Glimmer);
        Assert.True(bought.Styles.Single(s => s.Id == style.Id).Owned);
        Assert.Equal(HttpStatusCode.Conflict, (await p.Http.PostAsync($"/api/walking/bazaar/styles/{style.Id}/buy", null)).StatusCode);

        await Ok(await p.Http.PostAsJsonAsync("/api/walking/bazaar/styles/wear", new WearStyleRequest(monster, style.Id)));
        Assert.Equal(style.Id, (await p.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0].CardStyle);

        await Ok(await p.Http.PostAsJsonAsync("/api/walking/bazaar/styles/wear", new WearStyleRequest(monster, "")));
        Assert.Equal("", (await p.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0].CardStyle);
    }

    // ------------------------------------------------------------------ the Tonic Stand

    [Fact]
    public async Task Trail_Tonics_cost_Glimmer_three_a_day()
    {
        Player p = await NewPlayer(glimmer: 10_000);
        for (int i = 0; i < BazaarEndpoints.TonicsPerDay; i++) await Ok(await p.Http.PostAsync("/api/walking/bazaar/tonic", null));
        BazaarDto after = await Bazaar(p);
        Assert.Equal(10_000 - BazaarEndpoints.TonicPrice * BazaarEndpoints.TonicsPerDay, after.Glimmer);
        Assert.Equal(0, after.TonicsLeft);
        Assert.Equal(HttpStatusCode.BadRequest, (await p.Http.PostAsync("/api/walking/bazaar/tonic", null)).StatusCode);
    }

    [Fact]
    public async Task A_Crystal_Mender_walks_a_crystal_closer_to_whole()
    {
        Player p = await NewPlayer(glimmer: 10_000);
        Guid monster = (await p.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0].Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await p.Http.PostAsync($"/api/walking/bazaar/mender/{monster}", null)).StatusCode);

        await WithDb(async db => PowerBattles.Crystallise(await db.WalkingMonsters.FirstAsync(m => m.Id == monster), 30, 4000));
        await Ok(await p.Http.PostAsync($"/api/walking/bazaar/mender/{monster}", null));
        Assert.Equal(1000, await FromDb(db => db.WalkingMonsters.Where(m => m.Id == monster).Select(m => m.CrystalStepsLeft).FirstAsync()));
        await Ok(await p.Http.PostAsync($"/api/walking/bazaar/mender/{monster}", null));
        WalkingMonster whole = await FromDb(db => db.WalkingMonsters.AsNoTracking().FirstAsync(m => m.Id == monster));
        Assert.Equal(0, whole.CrystalStepsLeft);
        Assert.Equal(0, whole.CrystalPower);
    }

    // ------------------------------------------------------------------ wild battles

    [Fact]
    public async Task A_wild_win_takes_a_small_bite_three_a_day()
    {
        Player p = await NewPlayer();
        Guid monster = (await p.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0].Id;
        WalkingMonster before = await FromDb(db => db.WalkingMonsters.AsNoTracking().FirstAsync(m => m.Id == monster));
        int myPower = PowerBattles.PowerOf(RankedEndpoints.StatsOf(Catalog, before));

        // A wild monster is about as strong as yours: a huge claim is taken as a quarter above yours at most.
        HttpResponseMessage r = await p.Http.PostAsJsonAsync($"/api/walking/monsters/{monster}/wild", new WildBattleRequest(true, 1_000_000));
        WildBattleResultDto win = (await r.Content.ReadFromJsonAsync<WildBattleResultDto>())!;
        Assert.Equal(PowerBattles.Share((int)Math.Ceiling(myPower * WildBattles.MostAboveMine), PowerBattles.WildBiteShare), win.PowerChange);
        Assert.Equal(win.PowerChange, win.Monster.AbsorbedPower + win.Monster.AbsorbedGuard + win.Monster.AbsorbedTricks + win.Monster.AbsorbedWits);

        for (int i = 1; i < PowerBattles.WildBitesPerDay; i++) await p.Http.PostAsJsonAsync($"/api/walking/monsters/{monster}/wild", new WildBattleRequest(true, 10));
        WildBattleResultDto noMore = (await (await p.Http.PostAsJsonAsync($"/api/walking/monsters/{monster}/wild", new WildBattleRequest(true, 10)))
            .Content.ReadFromJsonAsync<WildBattleResultDto>())!;
        Assert.Equal(0, noMore.PowerChange);
        Assert.Equal(0, noMore.BitesLeftToday);
    }

    [Fact]
    public async Task A_wild_loss_turns_the_monster_to_crystal()
    {
        Player p = await NewPlayer();
        Guid monster = (await p.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0].Id;
        WildBattleResultDto lost = (await (await p.Http.PostAsJsonAsync($"/api/walking/monsters/{monster}/wild", new WildBattleRequest(false, 100)))
            .Content.ReadFromJsonAsync<WildBattleResultDto>())!;
        Assert.True(lost.PowerChange < 0);
        Assert.Equal(PowerBattles.DefeatSteps, lost.Monster.CrystalStepsLeft);
        Assert.Equal(-lost.PowerChange, lost.Monster.CrystalPower);

        // A crystal can't battle until it's walked back.
        HttpResponseMessage again = await p.Http.PostAsJsonAsync($"/api/walking/monsters/{monster}/wild", new WildBattleRequest(true, 100));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    // ------------------------------------------------------------------ ranked: bites and crystals

    private async Task<(Player P, Guid Monster)> RankedPlayer()
    {
        await WithDb(async db => db.RankedEntries.RemoveRange(await db.RankedEntries.ToListAsync()));   // shadows only
        Player p = await NewPlayer();
        Guid monster = (await p.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0].Id;
        await WithDb(async db => (await db.WalkingMonsters.FirstAsync(m => m.Id == monster)).Level = 40);
        HttpResponseMessage r = await p.Http.PutAsJsonAsync("/api/walking/ranked/entries",
            new RankedEntriesRequest(new List<RankedEntryRequest> { new(monster, "Balanced") }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (p, monster);
    }

    private async Task<RankedFightResultDto> Fight(Player p, Guid monster, int defenderStat)
    {
        RankedMatchDto match = (await (await p.Http.PostAsJsonAsync("/api/walking/ranked/match", new RankedMatchRequest(monster)))
            .Content.ReadFromJsonAsync<RankedMatchDto>())!;
        await WithDb(async db =>
        {
            RankedMatch m = await db.RankedMatches.FirstAsync(x => x.Id == match.Id);
            m.DefenderPower = m.DefenderGuard = m.DefenderTricks = m.DefenderWits = defenderStat;
            m.Seed = 1;   // spin 0.54 (see DuelRulesTests)
        });
        HttpResponseMessage r = await p.Http.PostAsJsonAsync($"/api/walking/ranked/matches/{match.Id}/fight", new RankedFightRequest("Balanced"));
        Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<RankedFightResultDto>())!;
    }

    [Fact]
    public async Task A_winning_attacker_takes_a_bite_on_its_first_rewarded_wins()
    {
        (Player p, Guid monster) = await RankedPlayer();
        WalkingMonster before = await FromDb(db => db.WalkingMonsters.AsNoTracking().FirstAsync(m => m.Id == monster));
        int each = Math.Max(1, PowerBattles.PowerOf(RankedEndpoints.StatsOf(Catalog, before)) / 40);   // a defender a tenth as strong
        RankedFightResultDto won = await Fight(p, monster, each);
        Assert.True(won.Won, won.Message);
        Assert.Equal(PowerBattles.Share(each * 4, PowerBattles.RankedBiteShare), won.PowerChange);
        WalkingMonster after = await FromDb(db => db.WalkingMonsters.AsNoTracking().FirstAsync(m => m.Id == monster));
        Assert.Equal(won.PowerChange, after.AbsorbedPower + after.AbsorbedGuard + after.AbsorbedTricks + after.AbsorbedWits);

        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).RankedRewardedWinsToday = PowerBattles.RankedBitesPerDay);
        RankedFightResultDto noBite = await Fight(p, monster, each);
        Assert.True(noBite.Won);
        Assert.Equal(0, noBite.PowerChange);
    }

    [Fact]
    public async Task A_beaten_attacker_turns_to_crystal()
    {
        (Player p, Guid monster) = await RankedPlayer();
        RankedFightResultDto lost = await Fight(p, monster, 100_000);
        Assert.False(lost.Won);
        Assert.True(lost.PowerChange < 0);
        Assert.Equal(PowerBattles.DefeatSteps, lost.CrystalSteps);
        WalkingMonster after = await FromDb(db => db.WalkingMonsters.AsNoTracking().FirstAsync(m => m.Id == monster));
        Assert.Equal(PowerBattles.DefeatSteps, after.CrystalStepsLeft);
        Assert.Equal(-lost.PowerChange, after.CrystalPower);
    }
}
