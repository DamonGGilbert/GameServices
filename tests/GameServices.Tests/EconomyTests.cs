using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Economy;

namespace GameServices.Tests;

public class EconomyTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public EconomyTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private async Task<HttpClient> SignedInClient()
    {
        HttpClient client = factory.CreateClient();
        string name = "e" + Guid.NewGuid().ToString("N")[..10];

        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private static async Task<ProfileResponse> Profile(HttpClient client)
        => (await client.GetFromJsonAsync<ProfileResponse>("/api/card-game/profile"))!;

    [Fact]
    public async Task Economy_requires_sign_in()
    {
        HttpResponseMessage response = await factory.CreateClient().GetAsync("/api/card-game/profile");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_new_player_gets_the_starter_wallet_and_deck()
    {
        HttpClient client = await SignedInClient();

        ProfileResponse profile = await Profile(client);

        Assert.Equal(900, profile.DuelPoints);
        Assert.Equal(40, profile.Collection.Sum(c => c.Quantity));
    }

    [Fact]
    public async Task Opening_a_pack_spends_points_and_adds_the_cards()
    {
        HttpClient client = await SignedInClient();

        HttpResponseMessage response = await client.PostAsync("/api/card-game/packs/damos-pack/open", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        OpenPackResponse opened = (await response.Content.ReadFromJsonAsync<OpenPackResponse>())!;
        Assert.Equal(9, opened.Cards.Count);
        Assert.Equal(600, opened.DuelPoints);

        ProfileResponse profile = await Profile(client);
        Assert.Equal(600, profile.DuelPoints);
        Assert.Equal(49, profile.Collection.Sum(c => c.Quantity));
    }

    [Fact]
    public async Task An_unknown_pack_is_not_found()
    {
        HttpClient client = await SignedInClient();

        HttpResponseMessage response = await client.PostAsync("/api/card-game/packs/free-secret-rares/open", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task You_cannot_open_a_pack_you_cannot_afford()
    {
        HttpClient client = await SignedInClient();

        // 900 DP = exactly three 300 DP packs.
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/card-game/packs/damos-pack/open", null)).StatusCode);
        }

        HttpResponseMessage broke = await client.PostAsync("/api/card-game/packs/damos-pack/open", null);
        Assert.Equal(HttpStatusCode.BadRequest, broke.StatusCode);

        // And the failed attempt changed nothing.
        ProfileResponse profile = await Profile(client);
        Assert.Equal(0, profile.DuelPoints);
        Assert.Equal(40 + 27, profile.Collection.Sum(c => c.Quantity));
    }

    [Fact]
    public async Task An_expensive_pack_is_refused_without_enough_points()
    {
        HttpClient client = await SignedInClient();

        HttpResponseMessage response = await client.PostAsync("/api/card-game/packs/secret-vault/open", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(900, (await Profile(client)).DuelPoints);
    }

    [Fact]
    public async Task Firing_many_pack_requests_at_once_cannot_overspend()
    {
        HttpClient client = await SignedInClient();

        // A hacked client sending 8 "open pack" requests in the same instant, with only 900 DP.
        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => client.PostAsync("/api/card-game/packs/damos-pack/open", null)));

        int succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        Assert.InRange(succeeded, 1, 3);
        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);

        // The books balance exactly: every success cost 300 and gave 9 cards. Nothing leaked.
        ProfileResponse profile = await Profile(client);
        Assert.Equal(900 - 300 * succeeded, profile.DuelPoints);
        Assert.Equal(40 + 9 * succeeded, profile.Collection.Sum(c => c.Quantity));
    }

    [Fact]
    public async Task A_duel_win_pays_the_server_decided_amount()
    {
        HttpClient client = await SignedInClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/card-game/duels/reward", new DuelResultRequest("win"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RewardResponse reward = (await response.Content.ReadFromJsonAsync<RewardResponse>())!;
        Assert.Equal(300, reward.Awarded);
        Assert.Equal(1200, reward.DuelPoints);
    }

    [Fact]
    public async Task Rewards_cannot_be_claimed_in_quick_succession()
    {
        HttpClient client = await SignedInClient();

        await client.PostAsJsonAsync("/api/card-game/duels/reward", new DuelResultRequest("win"));
        HttpResponseMessage second = await client.PostAsJsonAsync("/api/card-game/duels/reward", new DuelResultRequest("win"));

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(1200, (await Profile(client)).DuelPoints);
    }

    [Theory]
    [InlineData("")]
    [InlineData("jackpot")]
    public async Task Invalid_duel_results_are_rejected(string result)
    {
        HttpClient client = await SignedInClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/card-game/duels/reward", new DuelResultRequest(result));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Players_have_separate_wallets_and_collections()
    {
        HttpClient alice = await SignedInClient();
        HttpClient bob = await SignedInClient();

        await alice.PostAsync("/api/card-game/packs/damos-pack/open", null);

        Assert.Equal(600, (await Profile(alice)).DuelPoints);
        Assert.Equal(900, (await Profile(bob)).DuelPoints);
    }

    [Fact]
    public async Task The_ledger_records_every_change()
    {
        HttpClient client = await SignedInClient();

        await client.PostAsync("/api/card-game/packs/damos-pack/open", null);
        List<LedgerLine> ledger = (await client.GetFromJsonAsync<List<LedgerLine>>("/api/card-game/ledger"))!;

        Assert.Equal(2, ledger.Count);
        Assert.Equal("pack", ledger[0].Kind);
        Assert.Equal(-300, ledger[0].Amount);
        Assert.Equal(600, ledger[0].BalanceAfter);
        Assert.Equal("starter", ledger[1].Kind);
    }
}
