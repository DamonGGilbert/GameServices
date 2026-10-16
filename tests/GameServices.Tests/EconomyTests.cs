using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Economy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    private async Task<(HttpClient Http, Guid Id)> SignedInPlayer()
    {
        HttpClient client = factory.CreateClient();
        string name = "e" + Guid.NewGuid().ToString("N")[..10];

        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return (client, auth.User.Id);
    }

    /// <summary>Pretends the player's last reward was a while ago, so the "too soon" speed limit does not get in the way.</summary>
    private async Task Rest(Guid userId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.CardGameProfiles.Where(p => p.UserId == userId).ExecuteUpdateAsync(u => u.SetProperty(p => p.LastRewardUtc, (DateTime?)null));
    }

    private static Task<HttpResponseMessage> Win(HttpClient client, string opponent, int prestige)
        => client.PostAsJsonAsync("/api/card-game/duels/reward", new DuelResultRequest("win", opponent, prestige));

    private static async Task<RewardResponse> Paid(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RewardResponse>())!;
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
    public async Task A_pack_is_refused_without_enough_points()
    {
        HttpClient client = await SignedInClient();

        // 900 Duel Points buys two Damo's Packs (300 each) and one Monster Mayhem (350)... then the wallet is too thin.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/card-game/packs/damos-pack/open", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/card-game/packs/damos-pack/open", null)).StatusCode);
        HttpResponseMessage response = await client.PostAsync("/api/card-game/packs/monster-mayhem/open", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(300, (await Profile(client)).DuelPoints);
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

        HttpResponseMessage response = await Win(client, "damo", 0);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RewardResponse reward = (await response.Content.ReadFromJsonAsync<RewardResponse>())!;
        Assert.Equal(300, reward.Awarded);
        Assert.Equal(1200, reward.DuelPoints);
    }

    [Fact]
    public async Task Rewards_cannot_be_claimed_in_quick_succession()
    {
        HttpClient client = await SignedInClient();

        await Win(client, "damo", 0);
        HttpResponseMessage second = await Win(client, "another-opponent", 0);

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
    public async Task A_win_needs_an_opponent_and_a_sensible_prestige()
    {
        HttpClient client = await SignedInClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/card-game/duels/reward", new DuelResultRequest("win"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Win(client, "damo", 99)).StatusCode);
        Assert.Equal(900, (await Profile(client)).DuelPoints);
    }

    [Fact]
    public async Task Losses_and_draws_pay_no_duel_points()
    {
        HttpClient client = await SignedInClient();

        RewardResponse loss = await Paid(await client.PostAsJsonAsync("/api/card-game/duels/reward", new DuelResultRequest("loss", "damo", 0)));
        RewardResponse draw = await Paid(await client.PostAsJsonAsync("/api/card-game/duels/reward", new DuelResultRequest("draw", "damo", 0)));

        Assert.Equal(0, loss.Awarded);
        Assert.Equal(0, draw.Awarded);
        Assert.Equal(900, (await Profile(client)).DuelPoints);
    }

    [Fact]
    public async Task An_opponent_pays_once_per_prestige_rank()
    {
        (HttpClient client, Guid id) = await SignedInPlayer();

        Assert.Equal(300, (await Paid(await Win(client, "damo", 0))).Awarded);

        // The same opponent at the same rank: the duel is allowed, it just pays nothing.
        await Rest(id);
        RewardResponse again = await Paid(await Win(client, "damo", 0));
        Assert.Equal(0, again.Awarded);
        Assert.Contains("Prestige", again.Note);

        // A different opponent still pays.
        await Rest(id);
        Assert.Equal(300, (await Paid(await Win(client, "joey", 0))).Awarded);

        // After prestiging, the first opponent pays once more.
        await Rest(id);
        Assert.Equal(300, (await Paid(await Win(client, "damo", 1))).Awarded);
        await Rest(id);
        Assert.Equal(0, (await Paid(await Win(client, "damo", 1))).Awarded);

        Assert.Equal(900 + 900, (await Profile(client)).DuelPoints);
    }

    [Fact]
    public async Task From_the_top_ranks_an_opponent_pays_again_after_24_hours()
    {
        (HttpClient client, Guid id) = await SignedInPlayer();

        Assert.Equal(300, (await Paid(await Win(client, "damo", RewardRules.CooldownPrestige))).Awarded);

        await Rest(id);
        RewardResponse early = await Paid(await Win(client, "damo", RewardRules.CooldownPrestige));
        Assert.Equal(0, early.Awarded);
        Assert.Contains("again in", early.Note);

        // 25 hours later it pays again.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.OpponentRewards.Where(r => r.UserId == id).ExecuteUpdateAsync(u => u.SetProperty(r => r.LastClaimUtc, DateTime.UtcNow.AddHours(-25)));
        }

        await Rest(id);
        Assert.Equal(300, (await Paid(await Win(client, "damo", RewardRules.CooldownPrestige))).Awarded);
    }

    [Fact]
    public void The_reward_rule_in_a_nutshell()
    {
        DateTime now = DateTime.UtcNow;
        OpponentReward claim = new() { OpponentId = "damo", Prestige = 2, LastClaimUtc = now.AddHours(-1) };

        Assert.True(RewardRules.IsEligible(null, 0, now, out _));
        Assert.False(RewardRules.IsEligible(claim, 2, now, out _));
        Assert.True(RewardRules.IsEligible(claim, 3, now, out _));

        claim.Prestige = 3;
        Assert.False(RewardRules.IsEligible(claim, 3, now, out _));
        claim.LastClaimUtc = now.AddHours(-24).AddMinutes(-1);
        Assert.True(RewardRules.IsEligible(claim, 3, now, out _));
    }

    [Fact]
    public async Task The_profile_lists_claims_and_tickets()
    {
        (HttpClient client, _) = await SignedInPlayer();

        await Win(client, "damo", 0);
        ProfileResponse profile = await Profile(client);

        Assert.Equal(0, profile.SpinTickets);
        OpponentClaimDto claim = Assert.Single(profile.Claims!);
        Assert.Equal("damo", claim.OpponentId);
    }

    [Fact]
    public async Task The_expensive_pack_is_gone()
    {
        HttpClient client = await SignedInClient();

        List<PackInfo> packs = (await client.GetFromJsonAsync<List<PackInfo>>("/api/card-game/packs"))!;

        Assert.DoesNotContain(packs, p => p.Id == "secret-vault");
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/card-game/packs/secret-vault/open", null)).StatusCode);
    }

    [Fact]
    public async Task A_ticket_is_claimed_once_per_reason()
    {
        HttpClient client = await SignedInClient();

        HttpResponseMessage first = await client.PostAsJsonAsync("/api/card-game/tickets/claim", new TicketClaimRequest("final-opponent", 0));
        TicketClaimResponse granted = (await first.Content.ReadFromJsonAsync<TicketClaimResponse>())!;
        Assert.True(granted.Granted);
        Assert.Equal(1, granted.SpinTickets);
        Assert.Equal(1, (await Profile(client)).SpinTickets);
    }

    [Fact]
    public async Task The_same_ticket_reason_never_pays_twice()
    {
        (HttpClient client, Guid id) = await SignedInPlayer();

        await client.PostAsJsonAsync("/api/card-game/tickets/claim", new TicketClaimRequest("prestige", 1));

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.TicketGrants.Where(g => g.UserId == id).ExecuteUpdateAsync(u => u.SetProperty(g => g.CreatedUtc, DateTime.UtcNow.AddMinutes(-5)));
        }

        TicketClaimResponse again = (await (await client.PostAsJsonAsync("/api/card-game/tickets/claim", new TicketClaimRequest("prestige", 1))).Content.ReadFromJsonAsync<TicketClaimResponse>())!;
        Assert.False(again.Granted);
        Assert.Equal(1, again.SpinTickets);
    }

    [Fact]
    public async Task Tickets_cannot_be_claimed_in_a_rush()
    {
        HttpClient client = await SignedInClient();

        await client.PostAsJsonAsync("/api/card-game/tickets/claim", new TicketClaimRequest("final-opponent", 0));
        HttpResponseMessage second = await client.PostAsJsonAsync("/api/card-game/tickets/claim", new TicketClaimRequest("final-opponent", 1));

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Theory]
    [InlineData("jackpot", 0)]
    [InlineData("prestige", 0)]
    [InlineData("final-opponent", 99)]
    [InlineData("final-opponent", -1)]
    public async Task Nonsense_ticket_claims_are_rejected(string kind, int prestige)
    {
        HttpClient client = await SignedInClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/card-game/tickets/claim", new TicketClaimRequest(kind, prestige));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_lucky_draw_needs_a_ticket()
    {
        HttpClient client = await SignedInClient();

        HttpResponseMessage response = await client.PostAsync("/api/card-game/lucky-draw", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_lucky_draw_spends_one_ticket_for_a_free_pack_of_one_official_set()
    {
        HttpClient client = await SignedInClient();
        await client.PostAsJsonAsync("/api/card-game/tickets/claim", new TicketClaimRequest("final-opponent", 0));

        HttpResponseMessage response = await client.PostAsync("/api/card-game/lucky-draw", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        LuckyDrawResponse draw = (await response.Content.ReadFromJsonAsync<LuckyDrawResponse>())!;
        Assert.Equal(9, draw.Cards.Count);
        Assert.Equal(0, draw.SpinTickets);
        Assert.False(string.IsNullOrEmpty(draw.SetName));

        ProfileResponse profile = await Profile(client);
        Assert.Equal(0, profile.SpinTickets);
        Assert.Equal(900, profile.DuelPoints);                       // free: no Duel Points taken
        Assert.Equal(40 + 9, profile.Collection.Sum(c => c.Quantity)); // the starter deck plus the 9 new cards

        // Every card really belongs to the set that was drawn.
        GameServices.Api.Catalog.CardCatalog catalog = factory.Services.GetRequiredService<GameServices.Api.Catalog.CardCatalog>();
        GameServices.Api.Catalog.CatalogSet set = catalog.FindSet(draw.SetId)!;
        Assert.All(draw.Cards, id => Assert.Contains(id, set.Cards));

        // And a second draw has no ticket left.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/card-game/lucky-draw", null)).StatusCode);
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
