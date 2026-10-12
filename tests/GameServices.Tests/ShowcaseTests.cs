using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Guilds;
using GameServices.Api.Social;

namespace GameServices.Tests;

public class ShowcaseTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ShowcaseTests(ApiFactory factory)
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

    private static Task<HttpResponseMessage> Set(Player player, ShowcaseDto showcase)
        => player.Http.PutAsJsonAsync("/api/profile/showcase", showcase);

    [Fact]
    public async Task Showcase_requires_sign_in()
    {
        HttpResponseMessage response = await factory.CreateClient().PutAsJsonAsync("/api/profile/showcase", new ShowcaseDto(1, 0, 0, 0, ""));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Friends_and_guildmates_see_your_showcase()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();

        Assert.Equal(HttpStatusCode.NoContent, (await Set(alice, new ShowcaseDto(42, 3, 2, 3, "Silver Striker"))).StatusCode);

        await alice.Http.PostAsJsonAsync("/api/friends/requests", new UsernameRequest(bob.Name));
        await bob.Http.PostAsync($"/api/friends/requests/{alice.Id}/accept", null);

        FriendsResponse forBob = (await bob.Http.GetFromJsonAsync<FriendsResponse>("/api/friends"))!;
        ShowcaseDto seen = Assert.Single(forBob.Friends).Showcase;
        Assert.Equal(new ShowcaseDto(42, 3, 2, 3, "Silver Striker"), seen);

        // And in a guild.
        HttpResponseMessage made = await alice.Http.PostAsJsonAsync("/api/guilds", new CreateGuildRequest("Showcase " + Guid.NewGuid().ToString("N")[..8], "SHW", "", true));
        GuildSummary guild = (await made.Content.ReadFromJsonAsync<GuildSummary>())!;
        await bob.Http.PostAsync($"/api/guilds/{guild.Id}/join", null);

        GuildDetail detail = (await bob.Http.GetFromJsonAsync<GuildDetail>("/api/guilds/mine"))!;
        Assert.Equal("Silver Striker", detail.Members.Single(m => m.UserId == alice.Id).Showcase.Title);
        Assert.Equal(3, detail.Members.Single(m => m.UserId == alice.Id).Showcase.Prestige);
    }

    [Fact]
    public async Task New_players_show_level_1_with_nothing_fancy()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();

        await alice.Http.PostAsJsonAsync("/api/friends/requests", new UsernameRequest(bob.Name));
        await bob.Http.PostAsync($"/api/friends/requests/{alice.Id}/accept", null);

        ShowcaseDto seen = Assert.Single((await bob.Http.GetFromJsonAsync<FriendsResponse>("/api/friends"))!.Friends).Showcase;
        Assert.Equal(new ShowcaseDto(1, 0, 0, 0, ""), seen);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, "")]                 // level too low
    [InlineData(51, 0, 0, 0, "")]                // level too high
    [InlineData(10, 11, 0, 0, "")]               // prestige too high
    [InlineData(10, 2, 3, 0, "")]                // emblem beyond prestige
    [InlineData(10, 2, 0, 3, "")]                // frame beyond prestige
    [InlineData(10, 2, 0, 0, "<b>Hax</b>")]      // not plain letters
    [InlineData(10, 2, 0, 0, "A title that is far too long to show")]
    public async Task Bad_showcases_are_rejected(int level, int prestige, int emblem, int frame, string title)
    {
        Player alice = await NewPlayer();

        Assert.Equal(HttpStatusCode.BadRequest, (await Set(alice, new ShowcaseDto(level, prestige, emblem, frame, title))).StatusCode);
    }
}
