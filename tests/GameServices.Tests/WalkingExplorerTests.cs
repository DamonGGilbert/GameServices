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
/// PLAYERS: walkers to meet without walking - out and about lately, fair fights for your companion, your country, your
/// friends. Each with the title they wear (cleaned), their companion only if they let people see their Steplings, and a
/// line the server writes about what they've been up to. Never anyone blocked.
/// </summary>
public class WalkingExplorerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public WalkingExplorerTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer(string country = "")
    {
        HttpClient client = factory.CreateClient();
        string name = "e" + Guid.NewGuid().ToString("N")[..10];
        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        WalkingMonsterDto starter = (await client.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0];
        var p = new Player(client, auth.User.Id, name);
        await WithDb(async db =>
        {
            WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, p.Id, CancellationToken.None);
            profile.LastReportUtc = DateTime.UtcNow;   // out and about
            profile.CompanionMonsterId = starter.Id;
            profile.Country = country;
        });
        return p;
    }

    private async Task WithDb(Func<AppDbContext, Task> change)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await change(db);
        await db.SaveChangesAsync();
    }

    private static async Task<ExplorerListDto> Explorers(Player p, string view) =>
        (await p.Http.GetFromJsonAsync<ExplorerListDto>("/api/walking/explorers?view=" + view))!;

    [Fact]
    public async Task ActiveWalkers_ShowTheirTitleAndCompanion_ButNeverMe()
    {
        Player me = await NewPlayer(), other = await NewPlayer();
        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == other.Id)).Titles = "Gold Walker");
        await other.Http.PutAsJsonAsync("/api/walking/monsters/progress", new ProgressRequest(null, null, "Gold Walker"));

        ExplorerListDto list = await Explorers(me, "active");
        ExplorerDto them = Assert.Single(list.Explorers, e => e.UserId == other.Id);
        Assert.Equal("Gold Walker", them.Title);
        Assert.NotNull(them.Companion);
        Assert.True(them.CompanionPower > 0);
        Assert.True(them.WalkingNow);
        Assert.DoesNotContain(list.Explorers, e => e.UserId == me.Id);
    }

    [Fact]
    public async Task Titles_AreCleaned()
    {
        Assert.Equal("Legend II Walker", WalkingExplorerTitles("Legend II Walker"));
        Assert.Equal("", WalkingExplorerTitles("visit www.example.com"));
        Assert.Equal("", WalkingExplorerTitles(new string('a', 60)));
        await Task.CompletedTask;
    }

    private static string WalkingExplorerTitles(string title) => GameServices.Api.Walking.Explorers.CleanTitle(title);

    [Fact]
    public async Task HiddenSteplings_StayHidden_AndBlockedWalkersArentListed()
    {
        Player me = await NewPlayer(), shy = await NewPlayer(), blocked = await NewPlayer();
        await WithDb(async db =>
        {
            (await WalkingEndpoints.GetOrCreateProfile(db, shy.Id, CancellationToken.None)).MonsterVisibility = (int)MonsterVisibility.Nobody;
            db.Blocks.Add(new UserBlock { BlockerId = blocked.Id, BlockedId = me.Id });
        });

        ExplorerListDto list = await Explorers(me, "active");
        Assert.Null(Assert.Single(list.Explorers, e => e.UserId == shy.Id).Companion);
        Assert.DoesNotContain(list.Explorers, e => e.UserId == blocked.Id);
    }

    [Fact]
    public async Task CloseFights_AndCountry_AndFriends()
    {
        Player me = await NewPlayer("GB"), local = await NewPlayer("GB"), abroad = await NewPlayer("ES");

        // Everyone's starter is the same kind of monster - close fights for each other.
        Assert.Contains((await Explorers(me, "close")).Explorers, e => e.UserId == local.Id);

        ExplorerListDto country = await Explorers(me, "country");
        Assert.Contains(country.Explorers, e => e.UserId == local.Id);
        Assert.DoesNotContain(country.Explorers, e => e.UserId == abroad.Id);

        await me.Http.PostAsJsonAsync("/api/friends/requests", new UsernameRequest(abroad.Name));
        await abroad.Http.PostAsync($"/api/friends/requests/{me.Id}/accept", null);
        ExplorerListDto friends = await Explorers(me, "friends");
        Assert.True(Assert.Single(friends.Explorers).IsFriend);
    }
}
