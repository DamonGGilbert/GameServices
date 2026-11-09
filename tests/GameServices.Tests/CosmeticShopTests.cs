using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Economy;

namespace GameServices.Tests;

public class CosmeticShopTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public CosmeticShopTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private async Task<HttpClient> SignedInClient()
    {
        HttpClient client = factory.CreateClient();
        string name = "c" + Guid.NewGuid().ToString("N")[..10];

        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    [Fact]
    public async Task The_list_shows_every_item_and_nothing_owned_at_first()
    {
        HttpClient client = await SignedInClient();

        CosmeticsResponse list = (await client.GetFromJsonAsync<CosmeticsResponse>("/api/card-game/cosmetics"))!;

        Assert.Equal(900, list.DuelPoints);
        Assert.Empty(list.Owned);
        Assert.Equal(Cosmetics.All.Count, list.Items.Count);
    }

    [Fact]
    public async Task Buying_takes_the_points_and_the_item_is_yours()
    {
        HttpClient client = await SignedInClient();

        HttpResponseMessage response = await client.PostAsync("/api/card-game/cosmetics/CherryBlossomSleeve_0/buy", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        BuyCosmeticResponse bought = (await response.Content.ReadFromJsonAsync<BuyCosmeticResponse>())!;
        Assert.Equal(300, bought.DuelPoints);
        Assert.Contains("CherryBlossomSleeve_0", bought.Owned);

        CosmeticsResponse list = (await client.GetFromJsonAsync<CosmeticsResponse>("/api/card-game/cosmetics"))!;
        Assert.Equal(300, list.DuelPoints);
        Assert.Contains("CherryBlossomSleeve_0", list.Owned);
    }

    [Fact]
    public async Task You_cannot_buy_the_same_item_twice()
    {
        HttpClient client = await SignedInClient();
        await client.PostAsync("/api/card-game/cosmetics/SakuraPetals_0/buy", null);

        HttpResponseMessage again = await client.PostAsync("/api/card-game/cosmetics/SakuraPetals_0/buy", null);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<CosmeticsResponse>("/api/card-game/cosmetics"))!.DuelPoints);
    }

    [Fact]
    public async Task Too_few_points_is_refused_and_costs_nothing()
    {
        HttpClient client = await SignedInClient();

        HttpResponseMessage response = await client.PostAsync("/api/card-game/cosmetics/RoyalCourt_0/buy", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        CosmeticsResponse list = (await client.GetFromJsonAsync<CosmeticsResponse>("/api/card-game/cosmetics"))!;
        Assert.Equal(900, list.DuelPoints);
        Assert.Empty(list.Owned);
    }

    [Fact]
    public async Task An_unknown_item_is_not_found()
    {
        HttpClient client = await SignedInClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/card-game/cosmetics/Nope_0/buy", null)).StatusCode);
    }

    [Fact]
    public async Task Buying_needs_you_to_be_signed_in()
    {
        HttpResponseMessage response = await factory.CreateClient().PostAsync("/api/card-game/cosmetics/SakuraPetals_0/buy", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
