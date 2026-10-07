using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Saves;

namespace GameServices.Tests;

public class SaveTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public SaveTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    /// <summary>A browser/game client that is already signed in as a brand-new player.</summary>
    private async Task<HttpClient> SignedInClient()
    {
        HttpClient client = factory.CreateClient();
        string name = "p" + Guid.NewGuid().ToString("N")[..10];

        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private static Task<HttpResponseMessage> Put(HttpClient client, string game, string json, int expected)
        => client.PutAsJsonAsync($"/api/games/{game}/save", new SaveRequest(json, expected));

    [Fact]
    public async Task Saves_require_sign_in()
    {
        HttpClient anonymous = factory.CreateClient();

        HttpResponseMessage response = await anonymous.GetAsync("/api/games/card-game/save");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_new_player_has_no_save_yet()
    {
        HttpClient client = await SignedInClient();

        HttpResponseMessage response = await client.GetAsync("/api/games/card-game/save");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task First_save_gets_version_1_and_can_be_downloaded()
    {
        HttpClient client = await SignedInClient();

        HttpResponseMessage put = await Put(client, "card-game", "{\"duelPoints\":900}", expected: 0);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(1, (await put.Content.ReadFromJsonAsync<SaveResponse>())!.Version);

        SaveResponse? loaded = await client.GetFromJsonAsync<SaveResponse>("/api/games/card-game/save");
        Assert.Equal("{\"duelPoints\":900}", loaded!.Json);
        Assert.Equal(1, loaded.Version);
    }

    [Fact]
    public async Task Updating_with_the_right_version_bumps_the_version()
    {
        HttpClient client = await SignedInClient();
        await Put(client, "card-game", "{\"a\":1}", 0);

        HttpResponseMessage second = await Put(client, "card-game", "{\"a\":2}", 1);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, (await second.Content.ReadFromJsonAsync<SaveResponse>())!.Version);
    }

    [Fact]
    public async Task Updating_with_an_old_version_is_a_conflict_and_does_not_overwrite()
    {
        HttpClient client = await SignedInClient();
        await Put(client, "card-game", "{\"from\":\"phone\"}", 0);
        await Put(client, "card-game", "{\"from\":\"phone-again\"}", 1);

        // A second device that still thinks the save is version 1:
        HttpResponseMessage stale = await Put(client, "card-game", "{\"from\":\"laptop\"}", 1);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        SaveResponse? current = await client.GetFromJsonAsync<SaveResponse>("/api/games/card-game/save");
        Assert.Equal("{\"from\":\"phone-again\"}", current!.Json);
    }

    [Fact]
    public async Task Different_games_keep_separate_saves()
    {
        HttpClient client = await SignedInClient();

        await Put(client, "card-game", "{\"game\":\"cards\"}", 0);
        await Put(client, "walking-game", "{\"game\":\"walking\"}", 0);

        Assert.Equal("{\"game\":\"cards\"}", (await client.GetFromJsonAsync<SaveResponse>("/api/games/card-game/save"))!.Json);
        Assert.Equal("{\"game\":\"walking\"}", (await client.GetFromJsonAsync<SaveResponse>("/api/games/walking-game/save"))!.Json);
    }

    [Fact]
    public async Task Players_cannot_see_each_others_saves()
    {
        HttpClient alice = await SignedInClient();
        HttpClient bob = await SignedInClient();

        await Put(alice, "card-game", "{\"secret\":\"alice\"}", 0);

        HttpResponseMessage bobsView = await bob.GetAsync("/api/games/card-game/save");

        // Bob has no save of his own -- he does NOT get Alice's.
        Assert.Equal(HttpStatusCode.NotFound, bobsView.StatusCode);
    }

    [Theory]
    [InlineData("card-game", "not json at all")]
    [InlineData("card-game", "")]
    [InlineData("BAD GAME", "{\"a\":1}")]
    [InlineData("x", "{\"a\":1}")]
    public async Task Invalid_saves_are_rejected(string game, string json)
    {
        HttpClient client = await SignedInClient();

        HttpResponseMessage response = await Put(client, game, json, 0);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_saves_are_rejected()
    {
        HttpClient client = await SignedInClient();
        string huge = "{\"x\":\"" + new string('a', 300 * 1024) + "\"}";

        HttpResponseMessage response = await Put(client, "card-game", huge, 0);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
