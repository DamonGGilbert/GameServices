using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Social;
using GameServices.Api.Walking;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Ranked;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// PUBLIC PROFILES: anyone can look anyone up; who sees their Steplings is their choice (everyone, friends, nobody);
/// blocked walkers can't find each other; a challenge can pick out a monster only if you're allowed to see it.
/// </summary>
public class PublicProfileTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public PublicProfileTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer(string prefix = "p")
    {
        HttpClient client = factory.CreateClient();
        string name = prefix + Guid.NewGuid().ToString("N")[..10];
        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        await client.GetAsync("/api/walking/monsters");   // makes the profile and the starter
        return new Player(client, auth.User.Id, name);
    }

    private static Task<HttpResponseMessage> Look(Player viewer, string who) => viewer.Http.GetAsync("/api/walking/players/" + who);

    private static async Task<PublicProfileDto> Profile(Player viewer, string who) => (await (await Look(viewer, who)).Content.ReadFromJsonAsync<PublicProfileDto>())!;

    private static Task<HttpResponseMessage> SetVisibility(Player p, string visibility) =>
        p.Http.PutAsJsonAsync("/api/walking/monster-privacy", new MonsterPrivacyRequest(visibility));

    private static async Task MakeFriends(Player a, Player b)
    {
        await a.Http.PostAsJsonAsync("/api/friends/requests", new UsernameRequest(b.Name));
        await b.Http.PostAsync($"/api/friends/requests/{a.Id}/accept", null);
    }

    [Fact]
    public async Task Anyone_can_see_a_walkers_Steplings_by_default()
    {
        Player ellie = await NewPlayer(), sam = await NewPlayer();
        await WithDb(async db => (await db.WalkingMonsters.FirstAsync(m => m.OwnerId == ellie.Id)).Nickname = "Sparky");

        PublicProfileDto profile = await Profile(sam, ellie.Name.ToUpperInvariant());   // names aren't fussy about capitals
        Assert.Equal(ellie.Name, profile.Username);
        Assert.False(profile.IsMe);
        Assert.False(profile.MonstersHidden);
        Assert.Equal("Everyone", profile.MonsterVisibility);
        Assert.Equal("Sparky", Assert.Single(profile.Monsters).Nickname);
        Assert.True(profile.Monsters[0].Bst > 0);
    }

    [Fact]
    public async Task Friends_only_hides_Steplings_from_strangers()
    {
        Player ellie = await NewPlayer(), friend = await NewPlayer(), stranger = await NewPlayer();
        await MakeFriends(ellie, friend);
        Assert.Equal(HttpStatusCode.OK, (await SetVisibility(ellie, "Friends")).StatusCode);

        PublicProfileDto seenByStranger = await Profile(stranger, ellie.Name);
        Assert.True(seenByStranger.MonstersHidden);
        Assert.Empty(seenByStranger.Monsters);
        Assert.Equal(0, seenByStranger.MonsterCount);   // not even how many

        PublicProfileDto seenByFriend = await Profile(friend, ellie.Name);
        Assert.True(seenByFriend.IsFriend);
        Assert.Single(seenByFriend.Monsters);
    }

    [Fact]
    public async Task Nobody_hides_Steplings_from_everyone_but_you()
    {
        Player ellie = await NewPlayer(), friend = await NewPlayer();
        await MakeFriends(ellie, friend);
        await SetVisibility(ellie, "nobody");

        Assert.True((await Profile(friend, ellie.Name)).MonstersHidden);
        PublicProfileDto mine = await Profile(ellie, ellie.Name);
        Assert.True(mine.IsMe);
        Assert.Single(mine.Monsters);
        Assert.Equal("Nobody", (await ellie.Http.GetFromJsonAsync<PrivacyDto>("/api/walking/monster-privacy"))!.MonsterVisibility);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetVisibility(ellie, "Martians")).StatusCode);
    }

    [Fact]
    public async Task Blocked_walkers_cant_find_each_other()
    {
        Player ellie = await NewPlayer("blk"), sam = await NewPlayer("blk");
        await ellie.Http.PostAsJsonAsync("/api/blocks", new UsernameRequest(sam.Name));
        Assert.Equal(HttpStatusCode.NotFound, (await Look(sam, ellie.Name)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Look(ellie, sam.Name)).StatusCode);
        List<PlayerSearchDto> found = (await sam.Http.GetFromJsonAsync<List<PlayerSearchDto>>("/api/walking/players?search=blk"))!;
        Assert.DoesNotContain(found, f => f.UserId == ellie.Id);
    }

    [Fact]
    public async Task Search_finds_walkers_by_the_start_of_their_name()
    {
        string tag = "zq" + Guid.NewGuid().ToString("N")[..5];
        Player a = await NewPlayer(tag), b = await NewPlayer(tag), me = await NewPlayer();
        List<PlayerSearchDto> found = (await me.Http.GetFromJsonAsync<List<PlayerSearchDto>>("/api/walking/players?search=" + tag.ToUpperInvariant()))!;
        Assert.Equal(2, found.Count);
        Assert.Contains(found, f => f.Username == a.Name);
        Assert.Empty((await me.Http.GetFromJsonAsync<List<PlayerSearchDto>>("/api/walking/players?search=z"))!);   // too short
    }

    [Fact]
    public async Task A_challenge_can_pick_out_a_monster_you_can_see()
    {
        Player ellie = await NewPlayer(), sam = await NewPlayer();
        Guid hers = (await Profile(sam, ellie.Name)).Monsters[0].Id;
        Guid mine = (await Profile(sam, sam.Name)).Monsters[0].Id;

        HttpResponseMessage sent = await sam.Http.PostAsJsonAsync("/api/walking/challenges", new ChallengeSendRequest(ellie.Name, null, mine, 0, "Balanced", hers));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        ChallengeDto waiting = Assert.Single((await ellie.Http.GetFromJsonAsync<ChallengeListDto>("/api/walking/challenges"))!.Incoming);
        Assert.Equal(hers, waiting.RequestedMonsterId);

        // Hidden Steplings can't be picked out - but she can still be challenged.
        Player tom = await NewPlayer();
        await SetVisibility(ellie, "Nobody");
        Guid toms = (await Profile(tom, tom.Name)).Monsters[0].Id;
        Assert.Equal(HttpStatusCode.BadRequest,
            (await tom.Http.PostAsJsonAsync("/api/walking/challenges", new ChallengeSendRequest(ellie.Name, null, toms, 0, "Balanced", hers))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await tom.Http.PostAsJsonAsync("/api/walking/challenges", new ChallengeSendRequest(ellie.Name, null, toms, 0, "Balanced"))).StatusCode);
    }

    private async Task WithDb(Func<AppDbContext, Task> change)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await change(db);
        await db.SaveChangesAsync();
    }
}
