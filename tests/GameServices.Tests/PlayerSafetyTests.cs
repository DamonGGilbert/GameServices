using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Moderation;
using GameServices.Api.Walking;
using GameServices.Api.Walking.Bazaar;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Ranked;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// KEEPING THE GAME FAIR AND PLAYERS SAFE: two requests can't spend the same Glimmer or buy the same monster, a phone
/// can't claim any level it likes, there's a speed limit, sign-up checks age, accounts can be deleted without costing
/// anyone else their Glimmer, and players can be reported.
/// </summary>
public class PlayerSafetyTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public PlayerSafetyTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer(int glimmer = 0)
    {
        HttpClient client = factory.CreateClient();
        string name = "s" + Guid.NewGuid().ToString("N")[..10];
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

    private Task<int> GlimmerOf(Player p) => FromDb(db => db.WalkingProfiles.Where(x => x.UserId == p.Id).Select(x => x.Glimmer).FirstAsync());

    private static async Task<Guid> StarterOf(Player p) => (await p.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0].Id;

    // A second monster, settled in, so the starter can stay the companion.
    private async Task<Guid> Extra(Player p)
    {
        Guid id = Guid.Empty;
        await WithDb(async db =>
        {
            WalkingMonster starter = await db.WalkingMonsters.FirstAsync(m => m.OwnerId == p.Id);
            var m = new WalkingMonster
            {
                OwnerId = p.Id, SpeciesId = starter.SpeciesId, Seed = starter.Seed + 7, Potential = starter.Potential, RulesVersion = starter.RulesVersion,
                Serial = Random.Shared.Next(100000, int.MaxValue), Source = "walk", FoundDay = starter.FoundDay, FoundUtc = DateTime.UtcNow.AddDays(-9),
                AcquiredUtc = DateTime.UtcNow.AddDays(-9), Seen = true,
            };
            db.WalkingMonsters.Add(m);
            (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).CompanionMonsterId = starter.Id;
            id = m.Id;
        });
        return id;
    }

    // ------------------------------------------------------------------ races

    [Fact]
    public async Task Many_taps_at_once_can_only_spend_the_Glimmer_once()
    {
        Player p = await NewPlayer(glimmer: BazaarEndpoints.TonicPrice);

        HttpResponseMessage[] answers = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => p.Http.PostAsync("/api/walking/bazaar/tonic", null)));

        Assert.Equal(1, answers.Count(a => a.StatusCode == HttpStatusCode.OK));
        Assert.Equal(0, await GlimmerOf(p));
        Assert.Equal(1, await FromDb(db => db.WalkingProfiles.Where(x => x.UserId == p.Id).Select(x => x.TonicsToday).FirstAsync()));
    }

    [Fact]
    public async Task Two_walkers_cant_both_pay_for_the_same_monster_from_Juniper()
    {
        Player seller = await NewPlayer();
        Guid monster = await Extra(seller);
        Assert.Equal(HttpStatusCode.OK, (await seller.Http.PostAsync($"/api/walking/bazaar/trader/{monster}/sell", null)).StatusCode);

        for (int round = 0; round < 3; round++)
        {
            Player a = await NewPlayer(glimmer: 50_000), b = await NewPlayer(glimmer: 50_000);
            HttpResponseMessage[] answers = await Task.WhenAll(
                a.Http.PostAsync($"/api/walking/bazaar/trader/{monster}/buy", null),
                b.Http.PostAsync($"/api/walking/bazaar/trader/{monster}/buy", null));

            Assert.Equal(1, answers.Count(x => x.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, answers.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            Player winner = answers[0].StatusCode == HttpStatusCode.OK ? a : b, loser = winner == a ? b : a;
            Assert.Equal(50_000, await GlimmerOf(loser));
            Assert.True(await GlimmerOf(winner) < 50_000);
            Assert.Equal(winner.Id, await FromDb(db => db.WalkingMonsters.Where(m => m.Id == monster).Select(m => m.OwnerId).FirstAsync()));

            // Back on the stall for another go.
            await WithDb(async db =>
            {
                WalkingMonster m = await db.WalkingMonsters.FirstAsync(x => x.Id == monster);
                m.OwnerId = Trader.Id;
            });
        }
    }

    [Fact]
    public async Task Two_walkers_cant_both_Buy_Now_the_same_listing()
    {
        Player seller = await NewPlayer();
        Guid monster = await Extra(seller);
        int price = MonsterOffers.MinPrice(0);
        MarketListingDto listing = (await (await seller.Http.PostAsJsonAsync("/api/walking/market", new ListRequest(monster, price, price, 24)))
            .Content.ReadFromJsonAsync<MarketListingDto>())!;
        Player a = await NewPlayer(glimmer: 5000), b = await NewPlayer(glimmer: 5000);

        HttpResponseMessage[] answers = await Task.WhenAll(
            a.Http.PostAsync($"/api/walking/market/{listing.Id}/buy", null),
            b.Http.PostAsync($"/api/walking/market/{listing.Id}/buy", null));

        Assert.Equal(1, answers.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5000 + 5000 - price, await GlimmerOf(a) + await GlimmerOf(b));
    }

    // ------------------------------------------------------------------ levels

    private static Task<HttpResponseMessage> Claim(Player p, Guid monster, int level, int experience = 0) =>
        p.Http.PutAsJsonAsync("/api/walking/monsters/progress",
            new ProgressRequest(null, new List<MonsterProgressDto> { new(monster, level, experience, 0, 0, 0, 0, 0) }));

    private Task<WalkingMonster> Monster(Guid id) => FromDb(db => db.WalkingMonsters.AsNoTracking().FirstAsync(m => m.Id == id));

    [Fact]
    public async Task A_phone_cant_claim_any_level_it_likes()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);

        Assert.Equal(HttpStatusCode.NoContent, (await Claim(p, monster, 100)).StatusCode);

        WalkingMonster after = await Monster(monster);
        Assert.Equal(MonsterLevels.FromTotal(MonsterLevels.MaxXpPerDay).Level, after.Level);
        Assert.True(after.Level < 20, "a day's experience, not level 100: level " + after.Level);

        // It's a one-off, for a starter's progress from before signing in: asking again changes nothing, today or ever.
        await Claim(p, monster, 100);
        Assert.Equal(after.Level, (await Monster(monster)).Level);
        await WithDb(async db => (await db.WalkingMonsters.FirstAsync(m => m.Id == monster)).XpDay = "2000-01-01");
        await Claim(p, monster, 100);
        Assert.Equal(after.Level, (await Monster(monster)).Level);
    }

    [Fact]
    public async Task A_starters_progress_from_before_signing_in_comes_across_and_a_level_never_goes_down()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);

        await Claim(p, monster, 8, 25);
        WalkingMonster levelled = await Monster(monster);
        Assert.Equal(8, levelled.Level);
        Assert.Equal(25, levelled.Experience);

        await Claim(p, monster, 3);
        Assert.Equal(8, (await Monster(monster)).Level);
    }

    [Fact]
    public async Task Only_a_starter_can_bring_progress_with_it()
    {
        Player p = await NewPlayer();
        Guid other = await Extra(p);
        int level = (await Monster(other)).Level;

        await Claim(p, other, level + 10);

        Assert.Equal(level, (await Monster(other)).Level);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(2, 79)]
    [InlineData(50, 1234)]
    public void Total_experience_and_levels_turn_into_each_other(int level, int experience)
    {
        Assert.Equal((level, experience), MonsterLevels.FromTotal(MonsterLevels.TotalXp(level, experience)));
    }

    // ------------------------------------------------------------------ the age check

    private async Task<HttpResponseMessage> SignUp(int? year, int? month, bool walking = true)
    {
        HttpClient client = factory.CreateClient();
        if (walking) client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        return await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("a" + Guid.NewGuid().ToString("N")[..10], "CorrectHorse1", null, year, month));
    }

    [Fact]
    public async Task A_walking_account_needs_the_minimum_age()
    {
        DateTime now = DateTime.UtcNow;
        int tooYoung = now.Year - AgeRules.DefaultMinimumAge + 1;

        HttpResponseMessage young = await SignUp(tooYoung, 1);
        Assert.Equal(HttpStatusCode.Forbidden, young.StatusCode);
        Assert.Contains("too_young", await young.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await SignUp(null, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SignUp(now.Year - 20, 13)).StatusCode);

        HttpResponseMessage old = await SignUp(now.Year - AgeRules.DefaultMinimumAge - 1, 1);
        Assert.Equal(HttpStatusCode.Created, old.StatusCode);
        Guid id = (await old.Content.ReadFromJsonAsync<AuthResponse>())!.User.Id;
        Assert.NotNull(await FromDb(db => db.Users.Where(u => u.Id == id).Select(u => u.AgeConfirmedUtc).FirstAsync()));
    }

    [Fact]
    public async Task The_card_game_doesnt_ask_for_a_birthday()
    {
        Assert.Equal(HttpStatusCode.Created, (await SignUp(null, null, walking: false)).StatusCode);
    }

    [Theory]
    [InlineData(2010, 6, 2023, 7, 13)]   // the month after their birthday: 13
    [InlineData(2010, 6, 2023, 6, 12)]   // the month itself: only the month is known, so not yet
    [InlineData(2010, 6, 2023, 5, 12)]
    public void Age_only_counts_once_the_birth_month_has_passed(int year, int month, int nowYear, int nowMonth, int age)
    {
        Assert.Equal(age, AgeRules.AgeIn(year, month, new DateTime(nowYear, nowMonth, 15)));
    }

    // ------------------------------------------------------------------ deleting an account

    private static Task<HttpResponseMessage> DeleteAccount(Player p, string password = "CorrectHorse1") =>
        p.Http.PostAsJsonAsync("/api/auth/delete-account", new DeleteAccountRequest(password));

    [Fact]
    public async Task Deleting_needs_the_password()
    {
        Player p = await NewPlayer();
        Assert.Equal(HttpStatusCode.BadRequest, (await DeleteAccount(p, "NotMyPassword1")).StatusCode);
        Assert.True(await FromDb(db => db.Users.AnyAsync(u => u.Id == p.Id)));
    }

    [Fact]
    public async Task Deleting_removes_the_account_and_everything_that_belongs_to_it()
    {
        Player p = await NewPlayer(glimmer: 500);
        Guid monster = await StarterOf(p);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAccount(p)).StatusCode);

        Assert.False(await FromDb(db => db.Users.AnyAsync(u => u.Id == p.Id)));
        Assert.False(await FromDb(db => db.WalkingProfiles.AnyAsync(x => x.UserId == p.Id)));
        Assert.False(await FromDb(db => db.WalkingMonsters.AnyAsync(m => m.Id == monster)));
        Assert.False(await FromDb(db => db.RefreshTokens.AnyAsync(t => t.UserId == p.Id)));

        HttpClient fresh = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.PostAsJsonAsync("/api/auth/login", new LoginRequest(p.Name, "CorrectHorse1"))).StatusCode);
    }

    [Fact]
    public async Task Deleting_gives_other_players_their_Glimmer_back()
    {
        Player leaving = await NewPlayer(glimmer: 2000);
        Guid listed = await Extra(leaving);
        Guid offeredOn = await Extra(leaving);
        int price = MonsterOffers.MinPrice(0);

        // Someone's top bid on their listing, and someone's offer on another of their monsters.
        MarketListingDto listing = (await (await leaving.Http.PostAsJsonAsync("/api/walking/market", new ListRequest(listed, price, price * 4, 24)))
            .Content.ReadFromJsonAsync<MarketListingDto>())!;
        Player bidder = await NewPlayer(glimmer: 1000);
        Assert.Equal(HttpStatusCode.OK, (await bidder.Http.PostAsJsonAsync($"/api/walking/market/{listing.Id}/bid", new BidRequest(price))).StatusCode);
        Assert.Equal(1000 - price, await GlimmerOf(bidder));

        Player offerer = await NewPlayer(glimmer: 1000);
        await WithDb(async db =>
        {
            db.MonsterOffers.Add(new MonsterOffer
            {
                MonsterId = offeredOn, FromUserId = offerer.Id, ToUserId = leaving.Id, Price = 300, ExpiresUtc = DateTime.UtcNow.AddDays(2),
            });
            (await db.WalkingProfiles.FirstAsync(x => x.UserId == offerer.Id)).Glimmer -= 300;
        });

        // Someone's challenge to them, with a stake held.
        Player challenger = await NewPlayer(glimmer: 1000);
        Guid challengerMonster = await StarterOf(challenger);
        HttpResponseMessage sent = await challenger.Http.PostAsJsonAsync("/api/walking/challenges",
            new ChallengeSendRequest(null, leaving.Id, challengerMonster, 100, "Balanced"));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal(900, await GlimmerOf(challenger));

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAccount(leaving)).StatusCode);

        Assert.Equal(1000, await GlimmerOf(bidder));
        Assert.Equal(1000, await GlimmerOf(offerer));
        Assert.Equal(1000, await GlimmerOf(challenger));
        Assert.False(await FromDb(db => db.Challenges.AnyAsync(c => c.ToUserId == leaving.Id)));
        Assert.False(await FromDb(db => db.MonsterOffers.AnyAsync(o => o.ToUserId == leaving.Id)));
    }

    [Fact]
    public async Task Deleting_leaves_other_players_listings_and_guilds_working()
    {
        // Top bidder on someone else's listing: it carries on with no bids.
        Player seller = await NewPlayer();
        Guid forSale = await Extra(seller);
        int price = MonsterOffers.MinPrice(0);
        MarketListingDto listing = (await (await seller.Http.PostAsJsonAsync("/api/walking/market", new ListRequest(forSale, price, price * 4, 24)))
            .Content.ReadFromJsonAsync<MarketListingDto>())!;
        Player leaving = await NewPlayer(glimmer: 1000);
        await leaving.Http.PostAsJsonAsync($"/api/walking/market/{listing.Id}/bid", new BidRequest(price));

        // Leader of a guild with someone else in it: the guild passes on.
        Player member = await NewPlayer();
        Guid guild = Guid.NewGuid();
        await WithDb(db =>
        {
            db.Guilds.Add(new Guild { Id = guild, GameId = GameIds.Walking, Name = "Leavers" + guild.ToString("N")[..6], NormalizedName = "LEAVERS" + guild.ToString("N")[..6].ToUpperInvariant(), Tag = "LV" });
            db.GuildMembers.Add(new GuildMember { GuildId = guild, UserId = leaving.Id, GameId = GameIds.Walking, Role = GuildRole.Leader });
            db.GuildMembers.Add(new GuildMember { GuildId = guild, UserId = member.Id, GameId = GameIds.Walking, Role = GuildRole.Member });
            return Task.CompletedTask;
        });

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAccount(leaving)).StatusCode);

        MarketListing after = await FromDb(db => db.MarketListings.AsNoTracking().FirstAsync(l => l.Id == listing.Id));
        Assert.Equal(ListingStatus.Active, after.Status);
        Assert.Null(after.TopBidderId);
        Assert.Equal(0, after.CurrentBid);
        Assert.Equal(GuildRole.Leader, await FromDb(db => db.GuildMembers.Where(m => m.UserId == member.Id).Select(m => m.Role).FirstAsync()));
    }

    // ------------------------------------------------------------------ reporting

    private static Task<HttpResponseMessage> Report(Player from, string name, string reason = "bullying") =>
        from.Http.PostAsJsonAsync("/api/reports", new ReportRequest(name, reason));

    [Fact]
    public async Task A_report_keeps_what_it_was_about_even_after_they_delete_their_account()
    {
        Player reporter = await NewPlayer(), bully = await NewPlayer();
        await WithDb(db =>
        {
            db.ChatMessages.Add(new ChatMessage { SenderId = bully.Id, RecipientId = reporter.Id, Text = "you walk funny" });
            return Task.CompletedTask;
        });

        Assert.Equal(HttpStatusCode.NoContent, (await Report(reporter, bully.Name)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAccount(bully)).StatusCode);

        PlayerReport kept = await FromDb(db => db.PlayerReports.AsNoTracking().FirstAsync(r => r.ReportedName == bully.Name));
        Assert.Null(kept.ReportedUserId);
        Assert.Equal(reporter.Id, kept.ReporterId);
        Assert.Contains("you walk funny", kept.Evidence);
    }

    [Fact]
    public async Task Reports_are_checked()
    {
        Player p = await NewPlayer(), other = await NewPlayer();
        Assert.Equal(HttpStatusCode.BadRequest, (await Report(p, p.Name)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Report(p, other.Name, "because")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Report(p, "NobodyAtAll99")).StatusCode);

        for (int i = 0; i < ReportEndpoints.MaxPerDay; i++) Assert.Equal(HttpStatusCode.NoContent, (await Report(p, other.Name)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Report(p, other.Name)).StatusCode);
    }

    [Fact]
    public async Task A_report_can_be_read_and_closed_and_someone_can_be_suspended()
    {
        Player reporter = await NewPlayer(), rude = await NewPlayer();
        await Report(reporter, rude.Name, "name");
        DateTime now = DateTime.UtcNow;

        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string line = Assert.Single((await ReportAdmin.OpenReports(db)).Where(r => r.Contains(rude.Name)));

        Assert.True(await ReportAdmin.Suspend(db, rude.Name, now.AddDays(7), now));
        HttpResponseMessage paused = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(rude.Name, "CorrectHorse1"));
        Assert.Equal(HttpStatusCode.Forbidden, paused.StatusCode);
        Assert.Contains("paused", await paused.Content.ReadAsStringAsync());

        Assert.True(await ReportAdmin.Suspend(db, rude.Name, null, now));
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(rude.Name, "CorrectHorse1"))).StatusCode);

        Assert.True(await ReportAdmin.CloseReport(db, line[..8], "Spoke to them", now));
        Assert.DoesNotContain(await ReportAdmin.OpenReports(db), r => r.Contains(rude.Name));
    }

    // ------------------------------------------------------------------ keeping only what's needed

    [Fact]
    public async Task A_finished_route_loses_its_path_and_an_old_closed_report_goes()
    {
        Player p = await NewPlayer();
        DateTime now = DateTime.UtcNow;
        Guid recent = Guid.NewGuid(), old = Guid.NewGuid(), oldReport = Guid.NewGuid(), openReport = Guid.NewGuid();
        await WithDb(db =>
        {
            db.WalkingRoutes.Add(new WalkingRoute { Id = recent, UserId = p.Id, Kind = "short", PathJson = "[[51.5,-0.1]]", CheckpointsJson = "[[51.5,-0.1]]", ExpiresUtc = now });
            db.WalkingRoutes.Add(new WalkingRoute { Id = old, UserId = p.Id, Kind = "short", PathJson = "[[51.5,-0.1]]", CheckpointsJson = "[[51.5,-0.1]]", ExpiresUtc = now.AddDays(-8) });
            db.PlayerReports.Add(new PlayerReport { Id = oldReport, ReportedName = "a", Reason = "other", ClosedUtc = now.AddDays(-400) });
            db.PlayerReports.Add(new PlayerReport { Id = openReport, ReportedName = "b", Reason = "other", CreatedUtc = now.AddDays(-400) });
            return Task.CompletedTask;
        });

        await WithDb(db => WalkingLifecycleService.RunOnce(db, now));

        Assert.Equal("[]", await FromDb(db => db.WalkingRoutes.Where(r => r.Id == old).Select(r => r.PathJson).FirstAsync()));
        Assert.NotEqual("[]", await FromDb(db => db.WalkingRoutes.Where(r => r.Id == recent).Select(r => r.PathJson).FirstAsync()));
        Assert.False(await FromDb(db => db.PlayerReports.AnyAsync(r => r.Id == oldReport)));
        Assert.True(await FromDb(db => db.PlayerReports.AnyAsync(r => r.Id == openReport)), "an open report stays however old");
    }

    // ------------------------------------------------------------------ the web pages the stores link to

    [Theory]
    [InlineData("/privacy", "Steplings Privacy Policy")]
    [InlineData("/delete-account", "Delete your Steplings account")]
    public async Task The_legal_pages_need_no_sign_in(string path, string title)
    {
        HttpResponseMessage page = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        Assert.Contains("<h1>" + title + "</h1>", await page.Content.ReadAsStringAsync());
    }
}

/// <summary>THE SPEED LIMIT, with a low limit so a test can reach it.</summary>
public class GameSpeedLimitTests : IClassFixture<GameSpeedLimitFactory>
{
    private readonly GameSpeedLimitFactory factory;

    public GameSpeedLimitTests(GameSpeedLimitFactory factory)
    {
        this.factory = factory;
    }

    private async Task<HttpClient> Player()
    {
        HttpClient client = factory.CreateClient();
        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("g" + Guid.NewGuid().ToString("N")[..10], "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        return client;
    }

    [Fact]
    public async Task Each_player_has_their_own_speed_limit()
    {
        HttpClient busy = await Player();
        HttpClient other = await Player();

        var answers = new List<HttpStatusCode>();
        for (int i = 0; i < 60; i++) answers.Add((await busy.GetAsync("/api/me")).StatusCode);

        Assert.Contains(HttpStatusCode.OK, answers);
        Assert.Contains(HttpStatusCode.TooManyRequests, answers);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await busy.GetAsync("/health")).StatusCode);
    }
}

public class GameSpeedLimitFactory : ApiFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimit:GameRequestsPerMinute", "40");
    }
}
