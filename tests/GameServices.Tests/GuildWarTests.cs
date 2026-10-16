using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Guilds;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

public class GuildWarTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public GuildWarTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer()
    {
        HttpClient client = factory.CreateClient();
        string name = "w" + Guid.NewGuid().ToString("N")[..10];

        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new Player(client, auth.User.Id, name);
    }

    /// <summary>A guild whose leader and (members - 1) others are all in the main team.</summary>
    private async Task<(Player Leader, List<Player> Members, Guid GuildId)> MakeGuild(int members, int rating = 1000)
    {
        Player leader = await NewPlayer();
        HttpResponseMessage created = await leader.Http.PostAsJsonAsync("/api/guilds", new CreateGuildRequest("War " + Guid.NewGuid().ToString("N")[..8], "WAR", "", true));
        GuildSummary guild = (await created.Content.ReadFromJsonAsync<GuildSummary>())!;

        List<Player> all = new() { leader };

        for (int i = 1; i < members; i++)
        {
            Player member = await NewPlayer();
            Assert.Equal(HttpStatusCode.NoContent, (await member.Http.PostAsync($"/api/guilds/{guild.Id}/join", null)).StatusCode);
            all.Add(member);
        }

        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Guilds.Where(g => g.Id == guild.Id).ExecuteUpdateAsync(u => u.SetProperty(g => g.Rating, rating));

        return (leader, all, guild.Id);
    }

    private static readonly DateTime Saturday = new(2031, 3, 1, 10, 0, 0, DateTimeKind.Utc);   // a Saturday

    private async Task<T> WithDb<T>(Func<AppDbContext, Task<T>> work)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [Fact]
    public void The_test_date_really_is_a_Saturday()
    {
        Assert.Equal(DayOfWeek.Saturday, Saturday.DayOfWeek);
        Assert.Equal(Saturday.Date, GuildWars.WeekendStart(Saturday));
        Assert.Equal(Saturday.Date, GuildWars.WeekendStart(Saturday.AddDays(1)));
        Assert.Null(GuildWars.WeekendStart(Saturday.AddDays(2)));
        Assert.Null(GuildWars.WeekendStart(Saturday.AddDays(-1)));
    }

    [Fact]
    public async Task Guilds_with_enough_main_team_players_are_paired_on_the_weekend_by_rating()
    {
        // Three guilds of 3 and one of 2 (too small): only two can be paired, and the nearest ratings meet.
        var strong = await MakeGuild(3, 1900);
        var alsoStrong = await MakeGuild(3, 1880);
        var tooSmall = await MakeGuild(2, 1500);

        await WithDb(async db =>
        {
            await GuildWars.RunOnce(db, Saturday);
            return 0;
        });

        GuildWar? war = await WithDb(db => db.GuildWars.AsNoTracking().FirstOrDefaultAsync(w => w.GuildAId == strong.GuildId || w.GuildBId == strong.GuildId));
        Assert.NotNull(war);
        Assert.True((war!.GuildAId == strong.GuildId && war.GuildBId == alsoStrong.GuildId) || (war.GuildBId == strong.GuildId && war.GuildAId == alsoStrong.GuildId));
        Assert.Equal(0, await WithDb(db => db.GuildWars.CountAsync(w => w.GuildAId == tooSmall.GuildId || w.GuildBId == tooSmall.GuildId)));

        // Running the job again never pairs the same guilds twice.
        await WithDb(async db => { await GuildWars.RunOnce(db, Saturday.AddHours(5)); return 0; });
        Assert.Equal(1, await WithDb(db => db.GuildWars.CountAsync(w => w.GuildAId == strong.GuildId || w.GuildBId == strong.GuildId)));
    }

    [Fact]
    public async Task No_wars_start_on_a_weekday()
    {
        var one = await MakeGuild(3);
        var two = await MakeGuild(3);

        await WithDb(async db => { await GuildWars.RunOnce(db, Saturday.AddDays(-3)); return 0; });
        Assert.Equal(0, await WithDb(db => db.GuildWars.CountAsync(w => w.GuildAId == one.GuildId || w.GuildBId == two.GuildId)));
    }

    [Fact]
    public async Task Winning_a_war_duel_earns_a_lucky_draw_ticket_once()
    {
        var a = await MakeGuild(3, 1700);
        var b = await MakeGuild(3, 1690);
        await WithDb(async db => { await GuildWars.RunOnce(db, Saturday); return 0; });

        Guid userA = a.Members[0].Id;
        Guid userB = b.Members[0].Id;

        // Both players have a wallet (it is created the first time they look at it).
        await a.Members[0].Http.GetAsync("/api/card-game/profile");
        await b.Members[0].Http.GetAsync("/api/card-game/profile");

        for (int i = 1; i <= 2; i++)
        {
            await WithDb(async db =>
            {
                await GuildWars.RecordDuelAsync(db, a.GuildId, b.GuildId, userA, userB, 0, Saturday.AddHours(i), CancellationToken.None);
                await db.SaveChangesAsync();
                return 0;
            });
        }

        Assert.Equal(1, (await a.Members[0].Http.GetFromJsonAsync<GameServices.Api.Economy.ProfileResponse>("/api/card-game/profile"))!.SpinTickets);
        Assert.Equal(0, (await b.Members[0].Http.GetFromJsonAsync<GameServices.Api.Economy.ProfileResponse>("/api/card-game/profile"))!.SpinTickets);
    }

    [Fact]
    public async Task A_players_first_war_duel_counts_once_and_a_win_scores_a_point()
    {
        var a = await MakeGuild(3, 1700);
        var b = await MakeGuild(3, 1690);
        await WithDb(async db => { await GuildWars.RunOnce(db, Saturday); return 0; });

        Guid userA = a.Members[0].Id;
        Guid userB = b.Members[0].Id;

        // Seat 0 (guild A's player) wins.
        await WithDb(async db =>
        {
            await GuildWars.RecordDuelAsync(db, a.GuildId, b.GuildId, userA, userB, 0, Saturday.AddHours(1), CancellationToken.None);
            await db.SaveChangesAsync();
            return 0;
        });

        // The same two players fight again: it does not count a second time.
        await WithDb(async db =>
        {
            await GuildWars.RecordDuelAsync(db, a.GuildId, b.GuildId, userA, userB, 0, Saturday.AddHours(2), CancellationToken.None);
            await db.SaveChangesAsync();
            return 0;
        });

        GuildWar war = await WithDb(db => db.GuildWars.AsNoTracking().FirstAsync(w => w.GuildAId == a.GuildId || w.GuildBId == a.GuildId));
        int scoreOfA = war.GuildAId == a.GuildId ? war.ScoreA : war.ScoreB;
        int scoreOfB = war.GuildAId == a.GuildId ? war.ScoreB : war.ScoreA;
        Assert.Equal(1, scoreOfA);
        Assert.Equal(0, scoreOfB);
        Assert.Equal(2, await WithDb(db => db.GuildWarDuels.CountAsync(d => d.WarId == war.Id)));

        // A player who is NOT in the main team does not count.
        Player reserve = await NewPlayer();
        Assert.Equal(HttpStatusCode.NoContent, (await reserve.Http.PostAsync($"/api/guilds/{a.GuildId}/join", null)).StatusCode);

        await WithDb(async db =>
        {
            await db.GuildMembers.Where(m => m.UserId == reserve.Id).ExecuteUpdateAsync(u => u.SetProperty(m => m.Slot, 0));
            await GuildWars.RecordDuelAsync(db, a.GuildId, b.GuildId, reserve.Id, b.Members[1].Id, 0, Saturday.AddHours(3), CancellationToken.None);
            await db.SaveChangesAsync();
            return 0;
        });

        Assert.Equal(0, await WithDb(db => db.GuildWarDuels.CountAsync(d => d.UserId == reserve.Id)));
    }

    [Fact]
    public async Task When_the_weekend_ends_the_winner_is_decided_and_fighters_are_paid()
    {
        var a = await MakeGuild(3, 1600);
        var b = await MakeGuild(3, 1590);
        await WithDb(async db => { await GuildWars.RunOnce(db, Saturday); return 0; });

        // Players who have duelled have a wallet; make sure these two do.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppDbContext walletDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            GameServices.Api.Catalog.CardCatalog catalog = scope.ServiceProvider.GetRequiredService<GameServices.Api.Catalog.CardCatalog>();
            await GameServices.Api.Economy.EconomyEndpoints.GetOrCreateProfile(walletDb, catalog, a.Members[0].Id, CancellationToken.None);
            await GameServices.Api.Economy.EconomyEndpoints.GetOrCreateProfile(walletDb, catalog, b.Members[0].Id, CancellationToken.None);
        }

        await WithDb(async db =>
        {
            await GuildWars.RecordDuelAsync(db, a.GuildId, b.GuildId, a.Members[0].Id, b.Members[0].Id, 0, Saturday.AddHours(1), CancellationToken.None);
            await db.SaveChangesAsync();
            return 0;
        });

        // Still the weekend: nothing is closed.
        Assert.Equal(0, await WithDb(db => GuildWars.RunOnce(db, Saturday.AddDays(1))));

        // Monday: the war ends.
        await WithDb(async db => { await GuildWars.RunOnce(db, Saturday.AddDays(2).AddHours(1)); return 0; });

        Guild winner = await WithDb(db => db.Guilds.AsNoTracking().FirstAsync(g => g.Id == a.GuildId));
        Guild loser = await WithDb(db => db.Guilds.AsNoTracking().FirstAsync(g => g.Id == b.GuildId));
        Assert.Equal(1, winner.WarWins);
        Assert.Equal(1620, winner.Rating);
        Assert.Equal(1, loser.WarLosses);

        int winnerPaid = await WithDb(db => db.Ledger.Where(l => l.UserId == a.Members[0].Id && l.Kind == "guild-war").SumAsync(l => l.Amount));
        int loserPaid = await WithDb(db => db.Ledger.Where(l => l.UserId == b.Members[0].Id && l.Kind == "guild-war").SumAsync(l => l.Amount));
        Assert.Equal(GuildWars.WinnerReward, winnerPaid);
        Assert.Equal(GuildWars.LoserReward, loserPaid);

        // The players' view of the war.
        WarInfo info = (await a.Leader.Http.GetFromJsonAsync<WarInfo>("/api/guilds/war"))!;
        Assert.Equal("Finished", info.Status);
        Assert.Equal("Won", info.Result);
        Assert.Equal(1, info.MyScore);
    }

    [Fact]
    public async Task The_war_endpoint_needs_a_guild_and_explains_when_there_is_no_war()
    {
        Player loner = await NewPlayer();
        Assert.Equal(HttpStatusCode.Forbidden, (await loner.Http.GetAsync("/api/guilds/war")).StatusCode);

        var small = await MakeGuild(1);
        WarInfo info = (await small.Leader.Http.GetFromJsonAsync<WarInfo>("/api/guilds/war"))!;
        Assert.Equal("None", info.Status);
        Assert.Contains("at least 3", info.Note);
    }

    [Fact]
    public async Task Tiers_follow_rating_and_war_wins_and_limit_the_banner_styles()
    {
        Assert.Equal(0, GuildTiers.TierOf(1000, 0));
        Assert.Equal(1, GuildTiers.TierOf(1150, 0));
        Assert.Equal(3, GuildTiers.TierOf(1400, 2));   // 1400 + 100 = 1500
        Assert.Equal(4, GuildTiers.TierOf(1750, 0));

        var guild = await MakeGuild(1, 1000);

        // A rookie guild can't use a Gold banner ...
        Assert.Equal(HttpStatusCode.BadRequest, (await guild.Leader.Http.PutAsJsonAsync("/api/guilds/details", new UpdateGuildRequest(null, null, null, null, 3))).StatusCode);

        // ... until it has earned the tier.
        await WithDb(async db => { await db.Guilds.Where(g => g.Id == guild.GuildId).ExecuteUpdateAsync(u => u.SetProperty(g => g.Rating, 1500)); return 0; });
        Assert.Equal(HttpStatusCode.OK, (await guild.Leader.Http.PutAsJsonAsync("/api/guilds/details", new UpdateGuildRequest(null, null, null, null, 3))).StatusCode);

        GuildDetail detail = (await guild.Leader.Http.GetFromJsonAsync<GuildDetail>("/api/guilds/mine"))!;
        Assert.Equal(3, detail.BannerStyle);
        Assert.Equal(3, detail.Tier);
        Assert.Equal("Gold", detail.TierName);
    }
}
