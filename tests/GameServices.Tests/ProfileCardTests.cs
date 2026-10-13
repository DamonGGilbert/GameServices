using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Catalog;
using GameServices.Api.Data;
using GameServices.Api.Guilds;
using GameServices.Api.Social;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

public class ProfileCardTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ProfileCardTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer()
    {
        HttpClient client = factory.CreateClient();
        string name = "p" + Guid.NewGuid().ToString("N")[..10];

        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new Player(client, auth.User.Id, name);
    }

    private string SomeCard(int skip = 0) => factory.Services.GetRequiredService<CardCatalog>().Cards.Skip(skip).First().Id;

    private static Task<HttpResponseMessage> Put(Player player, ProfileCardDto card) => player.Http.PutAsJsonAsync("/api/profile/card", card);

    private static Task<ProfileViewDto?> View(Player viewer, string name) => viewer.Http.GetFromJsonAsync<ProfileViewDto>($"/api/users/{name}/profile");

    [Fact]
    public async Task Profiles_need_a_sign_in()
    {
        HttpClient anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/users/someone/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/profile/card", ProfileCardEndpoints.Empty)).StatusCode);
    }

    [Fact]
    public async Task A_new_player_has_an_empty_card_and_others_can_look_at_it()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();

        ProfileViewDto seen = (await View(bob, alice.Name))!;

        Assert.Equal(alice.Name, seen.Username);
        Assert.Equal("", seen.Card.Greeting);
        Assert.Equal("", seen.Card.FavCard);
        Assert.Null(seen.Guild);
        Assert.Equal("none", seen.Relationship);
        Assert.Equal(0, seen.OnlineWins);
    }

    [Fact]
    public async Task The_card_a_player_sets_is_what_others_see()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();

        string a = SomeCard(0);
        string b = SomeCard(1);
        ProfileCardDto card = new("Good luck, have fun!", a, "Dragon Deck", $"{a}:3,{b}:2|", 12, 5, 1, 4, 80, 531);

        Assert.Equal(HttpStatusCode.NoContent, (await Put(alice, card)).StatusCode);

        ProfileViewDto seen = (await View(bob, alice.Name))!;

        Assert.Equal(card, seen.Card);
        Assert.Equal(DateTime.UtcNow.Date, seen.MemberSinceUtc.Date);
    }

    [Fact]
    public async Task Only_valid_cards_are_accepted()
    {
        Player alice = await NewPlayer();
        string a = SomeCard(0);

        ProfileCardDto good = new("", a, "My deck", $"{a}:3", 1, 1, 1, 1, 1, 1);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(alice, good)).StatusCode);

        // A greeting that isn't on the list.
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(alice, good with { Greeting = "Anything I like to type" })).StatusCode);

        // Bad deck lists: 4 copies, wrong shape, far too many cards.
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(alice, good with { Deck = $"{a}:4" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(alice, good with { Deck = $"{a}" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(alice, good with { Deck = string.Join(",", Enumerable.Range(0, 30).Select(i => $"{SomeCard(i)}:3")) })).StatusCode);

        // A deck name with odd characters, and silly numbers.
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(alice, good with { DeckName = "<script>" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(alice, good with { Wins = -5 })).StatusCode);

        // The good card is still the one that is stored.
        ProfileViewDto seen = (await View(alice, alice.Name))!;
        Assert.Equal("My deck", seen.Card.DeckName);
    }

    [Fact]
    public async Task A_card_the_server_does_not_know_yet_is_left_out_not_refused()
    {
        Player alice = await NewPlayer();
        string a = SomeCard(0);

        ProfileCardDto card = new("", "brand-new-card", "New deck", $"{a}:2,brand-new-card:3|", 0, 0, 0, 0, 0, 0);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(alice, card)).StatusCode);

        ProfileViewDto seen = (await View(alice, alice.Name))!;
        Assert.Equal("", seen.Card.FavCard);
        Assert.Equal($"{a}:2|", seen.Card.Deck);
    }

    [Fact]
    public async Task Every_greeting_in_the_list_is_accepted()
    {
        Player alice = await NewPlayer();

        foreach (string greeting in Greetings.All)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await Put(alice, ProfileCardEndpoints.Empty with { Greeting = greeting })).StatusCode);
        }
    }

    [Fact]
    public async Task The_profile_shows_the_guild_the_friendship_and_the_online_record()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();

        string tag = "P" + Guid.NewGuid().ToString("N")[..3].ToUpperInvariant();
        await alice.Http.PostAsJsonAsync("/api/guilds", new CreateGuildRequest("Profile " + Guid.NewGuid().ToString("N")[..8], tag, "", true));

        // Two online duels: alice won one and drew one.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DuelMatches.Add(new DuelMatch { Player0Id = alice.Id, Player1Id = bob.Id, Player0Name = alice.Name, Player1Name = bob.Name, WinnerSeat = 0 });
            db.DuelMatches.Add(new DuelMatch { Player0Id = bob.Id, Player1Id = alice.Id, Player0Name = bob.Name, Player1Name = alice.Name, WinnerSeat = -1 });
            await db.SaveChangesAsync();
        }

        ProfileViewDto seen = (await View(bob, alice.Name))!;
        Assert.Equal(tag, seen.Guild!.Tag);
        Assert.Equal("Leader", seen.Guild.Role);
        Assert.Equal(1, seen.OnlineWins);
        Assert.Equal(0, seen.OnlineLosses);
        Assert.Equal(1, seen.OnlineDraws);
        Assert.Equal("none", seen.Relationship);

        await alice.Http.PostAsJsonAsync("/api/friends/requests", new UsernameRequest(bob.Name));
        Assert.Equal("requested", (await View(alice, bob.Name))!.Relationship);
        Assert.Equal("incoming", (await View(bob, alice.Name))!.Relationship);

        await bob.Http.PostAsync($"/api/friends/requests/{alice.Id}/accept", null);
        Assert.Equal("friend", (await View(bob, alice.Name))!.Relationship);
        Assert.Equal("self", (await View(bob, bob.Name))!.Relationship);
    }

    [Fact]
    public async Task A_blocked_player_cannot_look_at_the_profile()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();

        await alice.Http.PostAsJsonAsync("/api/blocks", new UsernameRequest(bob.Name));

        Assert.Equal(HttpStatusCode.NotFound, (await bob.Http.GetAsync($"/api/users/{alice.Name}/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.Http.GetAsync($"/api/users/{bob.Name}/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.Http.GetAsync("/api/users/nobody-at-all/profile")).StatusCode);
    }
}
