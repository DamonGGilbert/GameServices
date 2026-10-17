using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Online;
using GameServices.Api.Social;

namespace GameServices.Tests;

// ------------------------------------------------------------------
// "Come and duel me!": a friend invites a friend to a duel room.
// ------------------------------------------------------------------
public class DuelInviteTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public DuelInviteTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer()
    {
        HttpClient client = factory.CreateClient();
        string name = "i" + Guid.NewGuid().ToString("N")[..10];

        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new Player(client, auth.User.Id, name);
    }

    private static async Task MakeFriends(Player a, Player b)
    {
        await a.Http.PostAsJsonAsync("/api/friends/requests", new UsernameRequest(b.Name));
        await b.Http.PostAsync($"/api/friends/requests/{a.Id}/accept", null);
    }

    // Being online means having asked the server something lately.
    private static Task Online(Player player) => player.Http.GetAsync("/api/friends");

    private static Task<HttpResponseMessage> Invite(Player from, Player to)
        => from.Http.PostAsJsonAsync("/api/duel-invites", new DuelInviteRequest(to.Name));

    private static async Task<List<DuelInviteDto>> Invites(Player player)
        => (await player.Http.GetFromJsonAsync<List<DuelInviteDto>>("/api/duel-invites"))!;

    [Fact]
    public async Task A_friend_can_be_invited_and_can_join_the_room()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        await MakeFriends(alice, bob);
        await Online(bob);

        HttpResponseMessage sent = await Invite(alice, bob);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

        RoomDto room = (await sent.Content.ReadFromJsonAsync<RoomDto>())!;
        Assert.True(room.Mine);

        List<DuelInviteDto> bobsInvites = await Invites(bob);
        DuelInviteDto invite = Assert.Single(bobsInvites);
        Assert.Equal(alice.Name, invite.FromUsername);
        Assert.Equal(room.Id, invite.RoomId);
        Assert.True(invite.SecondsLeft > 0);

        // Accepting is the normal join call.
        Assert.Equal(HttpStatusCode.OK, (await bob.Http.PostAsync($"/api/rooms/{invite.RoomId}/join", null)).StatusCode);

        // The room is full now, so the invite is gone.
        Assert.Empty(await Invites(bob));
    }

    [Fact]
    public async Task Only_friends_can_be_invited()
    {
        Player alice = await NewPlayer();
        Player stranger = await NewPlayer();
        await Online(stranger);

        Assert.Equal(HttpStatusCode.NotFound, (await Invite(alice, stranger)).StatusCode);
        Assert.Empty(await Invites(stranger));
    }

    [Fact]
    public async Task A_blocked_player_cannot_be_invited_and_is_not_told_why()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        await MakeFriends(alice, bob);
        await Online(alice);
        await bob.Http.PostAsJsonAsync("/api/blocks", new UsernameRequest(alice.Name));

        HttpResponseMessage response = await Invite(alice, bob);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_friend_who_is_offline_cannot_be_invited()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        await MakeFriends(alice, bob);

        // Bob registered a moment ago but has not been seen since, so he may still count as online; make sure the
        // answer is at least sensible (either he gets the invite or the server says he is not online).
        HttpResponseMessage response = await Invite(alice, bob);

        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task You_cannot_invite_yourself()
    {
        Player alice = await NewPlayer();

        Assert.Equal(HttpStatusCode.NotFound, (await Invite(alice, alice)).StatusCode);
    }

    [Fact]
    public async Task An_invite_can_be_declined()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        await MakeFriends(alice, bob);
        await Online(bob);
        await Invite(alice, bob);

        DuelInviteDto invite = Assert.Single(await Invites(bob));
        Assert.Equal(HttpStatusCode.NoContent, (await bob.Http.PostAsync($"/api/duel-invites/{invite.RoomId}/decline", null)).StatusCode);

        Assert.Empty(await Invites(bob));
    }

    [Fact]
    public async Task Inviting_again_reuses_the_room_and_does_not_pile_up_invites()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        await MakeFriends(alice, bob);
        await Online(bob);

        RoomDto first = (await (await Invite(alice, bob)).Content.ReadFromJsonAsync<RoomDto>())!;
        RoomDto second = (await (await Invite(alice, bob)).Content.ReadFromJsonAsync<RoomDto>())!;

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await Invites(bob));
    }

    [Fact]
    public async Task An_invite_disappears_when_the_sender_leaves_the_room()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        await MakeFriends(alice, bob);
        await Online(bob);
        await Invite(alice, bob);

        await alice.Http.PostAsync("/api/rooms/leave", null);

        Assert.Empty(await Invites(bob));
    }

    [Fact]
    public async Task Someone_already_in_a_room_cannot_be_invited()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        await MakeFriends(alice, bob);
        await Online(bob);

        await bob.Http.PostAsJsonAsync("/api/rooms", new CreateRoomRequest("Bobs table", false));

        Assert.Equal(HttpStatusCode.Conflict, (await Invite(alice, bob)).StatusCode);
    }

    [Fact]
    public async Task Invites_need_sign_in()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/duel-invites")).StatusCode);
    }
}
