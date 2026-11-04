using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Walking.Monsters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// GROUNDWORK: choosing a clan when you sign up, so your starter comes in its colours. The game doesn't ask yet
/// (ClanChooser.AskAtSignUp), and sign-ups that don't choose work just as they did.
/// </summary>
public class ChooseClanTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ChooseClanTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private MonsterCatalog Catalog => factory.Services.GetRequiredService<MonsterFinder>().Catalog;

    private async Task<(HttpClient Client, HttpResponseMessage Response, AuthResponse? Auth)> SignUp(string? clan, bool walkingGame = true)
    {
        HttpClient client = factory.CreateClient();
        if (walkingGame) client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        string name = "c" + Guid.NewGuid().ToString("N")[..10];
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null, 1996, 3, clan));
        AuthResponse? auth = null;
        if (response.IsSuccessStatusCode)
        {
            auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        }
        return (client, response, auth);
    }

    private static async Task<List<WalkingMonsterDto>> Monsters(HttpClient client) =>
        (await client.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))!;

    [Theory]
    [InlineData(Clan.Fire)]
    [InlineData(Clan.Water)]
    [InlineData(Clan.Earth)]
    [InlineData(Clan.Wind)]
    [InlineData(Clan.Light)]
    [InlineData(Clan.Darkness)]
    public async Task Your_starter_comes_in_the_clan_you_chose(Clan clan)
    {
        (HttpClient client, HttpResponseMessage response, _) = await SignUp(clan.ToString());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        WalkingMonsterDto starter = Assert.Single(await Monsters(client));

        Assert.Equal(Catalog.StarterSpeciesId, starter.SpeciesId);
        Assert.Equal("starter", starter.Source);
        Assert.Equal(0, starter.RarityTier);
        Assert.Equal(clan, Clans.Of(Catalog, starter.SpeciesId, starter.Seed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Not_choosing_works_as_it_always_has(string? clan)
    {
        (HttpClient client, HttpResponseMessage response, _) = await SignUp(clan);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.Equal("starter", Assert.Single(await Monsters(client)).Source);
    }

    [Theory]
    [InlineData("Banana")]
    [InlineData("3")]
    [InlineData("fire")]
    public async Task Only_the_six_clans_are_accepted(string clan)
    {
        (_, HttpResponseMessage response, _) = await SignUp(clan);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_card_game_takes_no_notice()
    {
        (_, HttpResponseMessage response, AuthResponse? auth) = await SignUp("Water", walkingGame: false);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.WalkingMonsters.AnyAsync(m => m.OwnerId == auth!.User.Id));
    }
}
