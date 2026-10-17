using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Guilds;
using GameServices.Api.Infrastructure;
using GameServices.Api.Social;

namespace GameServices.Tests;

/// <summary>
/// GUILDS ARE PER GAME. One account plays every game, but the card game's guilds and the walking game's guilds must
/// never mix: each game only sees its own guilds, a player can be in one guild in EACH game, and each game has its own
/// member limit (walking 10, card game 30). The card game sends no X-Game-Id header and must keep working unchanged.
/// </summary>
public class GameScopeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public GameScopeTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    /// <summary>One account, two "games": Cards sends no header (like the real card game), Walk says "walking".</summary>
    private record Player(HttpClient Cards, HttpClient Walk, Guid Id, string Name);

    private async Task<Player> NewPlayer()
    {
        HttpClient cards = factory.CreateClient();
        string name = "s" + Guid.NewGuid().ToString("N")[..10];

        AuthResponse auth = (await (await cards.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        cards.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        HttpClient walk = factory.CreateClient();
        walk.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        walk.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);

        return new Player(cards, walk, auth.User.Id, name);
    }

    private static string UniqueName() => "Walkers " + Guid.NewGuid().ToString("N")[..8];

    private static async Task<GuildSummary> MakeGuild(HttpClient as_, string? name = null, bool open = true)
    {
        HttpResponseMessage response = await as_.PostAsJsonAsync("/api/guilds", new CreateGuildRequest(name ?? UniqueName(), "WLK", "for tests", open));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GuildSummary>())!;
    }

    private static async Task<List<GuildSummary>> Search(HttpClient as_, string query)
        => (await as_.GetFromJsonAsync<List<GuildSummary>>("/api/guilds?q=" + Uri.EscapeDataString(query)))!;

    // ------------------------------------------------------------------ the games don't see each other's guilds

    [Fact]
    public async Task A_walking_guild_is_invisible_to_the_card_game_and_the_other_way_round()
    {
        Player walker = await NewPlayer();
        Player duellist = await NewPlayer();

        GuildSummary walking = await MakeGuild(walker.Walk);
        GuildSummary cards = await MakeGuild(duellist.Cards);

        Assert.Contains(await Search(walker.Walk, walking.Name), g => g.Id == walking.Id);
        Assert.DoesNotContain(await Search(walker.Cards, walking.Name), g => g.Id == walking.Id);

        Assert.Contains(await Search(duellist.Cards, cards.Name), g => g.Id == cards.Id);
        Assert.DoesNotContain(await Search(duellist.Walk, cards.Name), g => g.Id == cards.Id);
    }

    [Fact]
    public async Task No_header_still_means_the_card_game_so_old_clients_keep_working()
    {
        Player player = await NewPlayer();
        GuildSummary guild = await MakeGuild(player.Cards);

        HttpClient explicitCards = factory.CreateClient();
        explicitCards.DefaultRequestHeaders.Authorization = player.Cards.DefaultRequestHeaders.Authorization;
        explicitCards.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Yugioh);

        GuildDetail mine = (await explicitCards.GetFromJsonAsync<GuildDetail>("/api/guilds/mine"))!;
        Assert.Equal(guild.Id, mine.Id);
    }

    [Fact]
    public async Task An_unknown_game_is_refused()
    {
        Player player = await NewPlayer();
        HttpClient odd = factory.CreateClient();
        odd.DefaultRequestHeaders.Authorization = player.Cards.DefaultRequestHeaders.Authorization;
        odd.DefaultRequestHeaders.Add(GameIds.Header, "chess");

        Assert.Equal(HttpStatusCode.BadRequest, (await odd.GetAsync("/api/guilds?q=a")).StatusCode);
    }

    [Fact]
    public async Task You_cannot_join_another_games_guild_by_its_id()
    {
        Player owner = await NewPlayer();
        Player walker = await NewPlayer();
        GuildSummary cardGuild = await MakeGuild(owner.Cards);

        HttpResponseMessage response = await walker.Walk.PostAsync($"/api/guilds/{cardGuild.Id}/join", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------------ one guild per game

    [Fact]
    public async Task A_player_can_be_in_one_guild_in_each_game()
    {
        Player player = await NewPlayer();

        GuildSummary cards = await MakeGuild(player.Cards);
        GuildSummary walking = await MakeGuild(player.Walk);

        Assert.Equal(cards.Id, (await player.Cards.GetFromJsonAsync<GuildDetail>("/api/guilds/mine"))!.Id);
        Assert.Equal(walking.Id, (await player.Walk.GetFromJsonAsync<GuildDetail>("/api/guilds/mine"))!.Id);
    }

    [Fact]
    public async Task But_still_only_one_guild_per_game()
    {
        Player player = await NewPlayer();
        Player other = await NewPlayer();
        await MakeGuild(player.Walk);
        GuildSummary second = await MakeGuild(other.Walk);

        HttpResponseMessage response = await player.Walk.PostAsync($"/api/guilds/{second.Id}/join", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Leaving_a_walking_guild_leaves_your_card_guild_alone()
    {
        Player player = await NewPlayer();
        GuildSummary cards = await MakeGuild(player.Cards);
        await MakeGuild(player.Walk);

        Assert.Equal(HttpStatusCode.NoContent, (await player.Walk.PostAsync("/api/guilds/leave", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await player.Walk.GetAsync("/api/guilds/mine")).StatusCode);
        Assert.Equal(cards.Id, (await player.Cards.GetFromJsonAsync<GuildDetail>("/api/guilds/mine"))!.Id);
    }

    [Fact]
    public async Task The_same_name_can_be_used_once_in_each_game()
    {
        Player a = await NewPlayer();
        Player b = await NewPlayer();
        Player c = await NewPlayer();
        string name = UniqueName();

        await MakeGuild(a.Cards, name);
        await MakeGuild(b.Walk, name);

        HttpResponseMessage clash = await c.Walk.PostAsJsonAsync("/api/guilds", new CreateGuildRequest(name, "WLK", "", true));
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
    }

    // ------------------------------------------------------------------ member limits

    [Fact]
    public async Task A_walking_guild_holds_ten_members_and_says_so()
    {
        Player leader = await NewPlayer();
        GuildSummary guild = await MakeGuild(leader.Walk);
        Assert.Equal(10, guild.MaxMembers);

        for (int i = 0; i < 9; i++)
        {
            Player joiner = await NewPlayer();
            Assert.Equal(HttpStatusCode.NoContent, (await joiner.Walk.PostAsync($"/api/guilds/{guild.Id}/join", null)).StatusCode);
        }

        GuildDetail detail = (await leader.Walk.GetFromJsonAsync<GuildDetail>("/api/guilds/mine"))!;
        Assert.Equal(10, detail.Members.Count);
        Assert.Equal(10, detail.MaxMembers);

        Player eleventh = await NewPlayer();
        HttpResponseMessage full = await eleventh.Walk.PostAsync($"/api/guilds/{guild.Id}/join", null);
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        Assert.Contains("10 members", await full.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_card_game_keeps_its_thirty_member_limit()
    {
        Player leader = await NewPlayer();
        GuildSummary guild = await MakeGuild(leader.Cards);

        Assert.Equal(30, guild.MaxMembers);
        Assert.Equal(GuildEndpoints.MaxMembers, GameIds.MaxGuildMembers(GameIds.Yugioh));
        Assert.Equal(10, GameIds.MaxGuildMembers(GameIds.Walking));
    }

    // ------------------------------------------------------------------ invites stay in their own game

    [Fact]
    public async Task Invites_only_show_in_their_own_game_and_joining_one_game_keeps_the_others_invites()
    {
        Player cardLeader = await NewPlayer();
        Player walkLeader = await NewPlayer();
        Player guest = await NewPlayer();

        await MakeGuild(cardLeader.Cards, open: false);
        GuildSummary walking = await MakeGuild(walkLeader.Walk, open: false);

        Assert.Equal(HttpStatusCode.OK, (await cardLeader.Cards.PostAsJsonAsync("/api/guilds/invites", new UsernameRequest(guest.Name))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await walkLeader.Walk.PostAsJsonAsync("/api/guilds/invites", new UsernameRequest(guest.Name))).StatusCode);

        List<InviteDto> walkInvites = (await guest.Walk.GetFromJsonAsync<List<InviteDto>>("/api/guilds/invites"))!;
        Assert.Single(walkInvites);
        Assert.Equal(walking.Id, walkInvites[0].GuildId);

        Assert.Equal(HttpStatusCode.NoContent, (await guest.Walk.PostAsync($"/api/guilds/invites/{walking.Id}/accept", null)).StatusCode);

        // The card-game invitation is still waiting.
        Assert.Single((await guest.Cards.GetFromJsonAsync<List<InviteDto>>("/api/guilds/invites"))!);
    }

    // ------------------------------------------------------------------ card-game-only features

    [Fact]
    public async Task Bosses_and_wars_are_not_part_of_the_walking_game()
    {
        Player leader = await NewPlayer();
        await MakeGuild(leader.Walk);

        Assert.Equal(HttpStatusCode.NotFound, (await leader.Walk.GetAsync("/api/guilds/bosses")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await leader.Walk.GetAsync("/api/guilds/war")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await leader.Walk.PostAsync("/api/guilds/boss/attack", null)).StatusCode);
    }
}
