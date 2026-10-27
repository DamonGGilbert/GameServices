using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Chat;
using GameServices.Api.Data;
using GameServices.Api.Guilds;
using GameServices.Api.Infrastructure;
using GameServices.Api.Social;
using GameServices.Api.Walking.Monsters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// NAME TAGS AND THE WORD FILTER IN USE: renaming monsters (one free Name Tag, more for Glimmer), names refused by
/// the filter, and the filter on usernames, guild names and chat.
/// </summary>
public class MonsterNameTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public MonsterNameTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer(int glimmer = 0)
    {
        HttpClient client = factory.CreateClient();
        string name = "n" + Guid.NewGuid().ToString("N")[..10];
        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        await client.GetAsync("/api/walking/wallet");   // makes the walking profile
        if (glimmer > 0)
        {
            using IServiceScope scope = factory.Services.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.WalkingProfiles.FirstAsync(p => p.UserId == auth.User.Id)).Glimmer = glimmer;
            await db.SaveChangesAsync();
        }
        return new Player(client, auth.User.Id, name);
    }

    private static async Task<WalkingMonsterDto> Starter(Player p) => (await p.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0];

    private static Task<HttpResponseMessage> Rename(Player p, Guid monster, string name) =>
        p.Http.PostAsJsonAsync($"/api/walking/monsters/{monster}/name", new MonsterNameRequest(name));

    // ------------------------------------------------------------------ Name Tags

    [Fact]
    public async Task The_free_Name_Tag_renames_a_monster_everywhere()
    {
        Player p = await NewPlayer();
        WalkingMonsterDto m = await Starter(p);
        Assert.Equal(1, (await p.Http.GetFromJsonAsync<NameTagsDto>("/api/walking/name-tags"))!.NameTags);

        HttpResponseMessage r = await Rename(p, m.Id, "  Sir   Sparky  ");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(0, (await r.Content.ReadFromJsonAsync<NameTagsDto>())!.NameTags);
        Assert.Equal("Sir Sparky", (await Starter(p)).Nickname);

        // No Name Tags left: another name has to wait - but going back to the species name is free.
        Assert.Equal(HttpStatusCode.BadRequest, (await Rename(p, m.Id, "Bolt")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Rename(p, m.Id, "")).StatusCode);
        Assert.Equal("", (await Starter(p)).Nickname);
    }

    [Fact]
    public async Task Name_Tags_are_bought_with_Glimmer()
    {
        Player p = await NewPlayer(MonsterNames.NameTagPrice + 10);
        HttpResponseMessage bought = await p.Http.PostAsync("/api/walking/name-tags/buy", null);
        Assert.Equal(HttpStatusCode.OK, bought.StatusCode);
        NameTagsDto now = (await bought.Content.ReadFromJsonAsync<NameTagsDto>())!;
        Assert.Equal(2, now.NameTags);
        Assert.Equal(10, now.Glimmer);

        Assert.Equal(HttpStatusCode.BadRequest, (await p.Http.PostAsync("/api/walking/name-tags/buy", null)).StatusCode);   // can't afford another
    }

    [Theory]
    [InlineData("Fuckface")]
    [InlineData("B1g $h1t")]
    [InlineData("Lil Nazi")]
    [InlineData("A name that is far too long")]
    [InlineData("<script>")]
    public async Task Bad_names_are_refused_and_keep_the_Name_Tag(string name)
    {
        Player p = await NewPlayer();
        WalkingMonsterDto m = await Starter(p);
        Assert.Equal(HttpStatusCode.BadRequest, (await Rename(p, m.Id, name)).StatusCode);
        Assert.Equal(1, (await p.Http.GetFromJsonAsync<NameTagsDto>("/api/walking/name-tags"))!.NameTags);
    }

    [Fact]
    public async Task You_can_only_rename_your_own_monsters()
    {
        Player p = await NewPlayer(), other = await NewPlayer();
        WalkingMonsterDto theirs = await Starter(other);
        Assert.Equal(HttpStatusCode.NotFound, (await Rename(p, theirs.Id, "Mine Now")).StatusCode);
    }

    // ------------------------------------------------------------------ the filter elsewhere

    [Fact]
    public async Task Rude_usernames_are_refused()
    {
        HttpResponseMessage r = await factory.CreateClient().PostAsJsonAsync("/api/auth/register", new RegisterRequest("Big_Sh1t_" + Guid.NewGuid().ToString("N")[..4], "CorrectHorse1", null));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Rude_guild_names_are_refused()
    {
        Player p = await NewPlayer();
        HttpResponseMessage r = await p.Http.PostAsJsonAsync("/api/guilds", new CreateGuildRequest("Fuck Walkers", "FW", null, true));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Chat_is_censored()
    {
        Player alice = await NewPlayer(), bob = await NewPlayer();
        await alice.Http.PostAsJsonAsync("/api/friends/requests", new UsernameRequest(bob.Name));
        await bob.Http.PostAsync($"/api/friends/requests/{alice.Id}/accept", null);

        HttpResponseMessage sent = await alice.Http.PostAsJsonAsync($"/api/chat/{bob.Id}", new SendMessageRequest("that was a sh1t walk, a classic"));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        ChatMessageDto received = Assert.Single((await bob.Http.GetFromJsonAsync<List<ChatMessageDto>>($"/api/chat/{alice.Id}"))!);
        Assert.Equal("that was a **** walk, a classic", received.Text);
    }
}
